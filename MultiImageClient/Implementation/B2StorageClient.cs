using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// <summary>
    /// Minimal Backblaze B2 native-API client for UI image hosting
    /// (docs/b2-image-hosting-plan.md). Upload/delete only — downloads are
    /// anonymous via the public bucket URL. Uploads stream from disk paths
    /// (never retained heap buffers) and carry X-Bz-Content-Sha1 so the B2
    /// server verifies the bytes before accepting them. All failures throw;
    /// callers own the retry policy (retry x3 then visible hard-fail, never a
    /// local-URL substitute — owner decision 2026-08-05).
    /// </summary>
    public class B2StorageClient
    {
        private const string AuthorizeUrl = "https://api.backblazeb2.com/b2api/v3/b2_authorize_account";
        private static readonly TimeSpan AuthMaxAge = TimeSpan.FromHours(20); // tokens last 24h

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        private readonly Settings _settings;
        private readonly SemaphoreSlim _authLock = new SemaphoreSlim(1, 1);
        private string _authToken;
        private string _apiUrl;
        private DateTime _authAcquiredUtc;

        public B2StorageClient(Settings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (!settings.EnableB2ImageHosting)
            {
                throw new InvalidOperationException("B2StorageClient constructed while EnableB2ImageHosting is false.");
            }
        }

        /// Public capability URL for an uploaded object key.
        public string DownloadUrlFor(string objectKey)
        {
            return $"{_settings.B2DownloadBaseUrl}/{EscapeKeyForUrl(objectKey)}";
        }

        /// ui/{jobId}/{gen}/{n}-{128-bit random hex}.{ext} — the random
        /// segment IS the access capability; never mint a key without it.
        public static string BuildObjectKey(string jobId, string generatorKey, int index, string extension)
        {
            var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var ext = extension.TrimStart('.');
            return $"ui/{jobId}/{generatorKey}/{index}-{random}.{ext}";
        }

        /// Uploads one file. Single attempt (fresh upload URL per call, which
        /// is how B2 wants transient 50x errors handled); throws on any
        /// failure including a server-side SHA1 mismatch. Returns the B2
        /// fileId (needed for future deletion).
        public async Task<string> UploadFileAsync(string localPath, string objectKey, string contentType, CancellationToken cancellationToken)
        {
            if (!File.Exists(localPath))
            {
                throw new FileNotFoundException($"B2 upload source missing: {localPath}", localPath);
            }

            var sha1Hex = await ComputeSha1Async(localPath, cancellationToken);
            var (apiUrl, authToken) = await GetAuthAsync(cancellationToken);

            // b2_get_upload_url: per-call URL + token. One concurrent upload
            // per URL is a B2 rule; a fresh URL per upload keeps concurrent
            // generator results safe without pooling machinery.
            string uploadUrl;
            string uploadToken;
            using (var request = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/b2api/v3/b2_get_upload_url"))
            {
                request.Headers.TryAddWithoutValidation("Authorization", authToken);
                request.Content = new StringContent($"{{\"bucketId\":\"{_settings.B2BucketId}\"}}", Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidateAuth();
                }
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"b2_get_upload_url failed: HTTP {(int)response.StatusCode} {Truncate(body)}");
                }
                using var doc = JsonDocument.Parse(body);
                uploadUrl = doc.RootElement.GetProperty("uploadUrl").GetString();
                uploadToken = doc.RootElement.GetProperty("authorizationToken").GetString();
            }

            using (var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            using (var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl))
            {
                request.Headers.TryAddWithoutValidation("Authorization", uploadToken);
                request.Headers.TryAddWithoutValidation("X-Bz-File-Name", EscapeKeyForUrl(objectKey));
                request.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", sha1Hex);
                request.Content = new StreamContent(fileStream);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                request.Content.Headers.ContentLength = new FileInfo(localPath).Length;

                using var response = await _http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"B2 upload of '{objectKey}' failed: HTTP {(int)response.StatusCode} {Truncate(body)}");
                }

                using var doc = JsonDocument.Parse(body);
                var returnedName = doc.RootElement.GetProperty("fileName").GetString();
                var returnedSha1 = doc.RootElement.GetProperty("contentSha1").GetString();
                var fileId = doc.RootElement.GetProperty("fileId").GetString();
                if (!string.Equals(returnedName, objectKey, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"B2 upload identity mismatch: sent key '{objectKey}', server recorded '{returnedName}'.");
                }
                if (!string.Equals(returnedSha1?.Replace("unverified:", ""), sha1Hex, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"B2 upload checksum mismatch for '{objectKey}': local {sha1Hex}, server {returnedSha1}.");
                }
                if (string.IsNullOrWhiteSpace(fileId))
                {
                    throw new InvalidOperationException($"B2 upload of '{objectKey}' returned no fileId.");
                }
                return fileId;
            }
        }

        public async Task DeleteFileAsync(string objectKey, string fileId, CancellationToken cancellationToken)
        {
            var (apiUrl, authToken) = await GetAuthAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/b2api/v3/b2_delete_file_version");
            request.Headers.TryAddWithoutValidation("Authorization", authToken);
            var payload = JsonSerializer.Serialize(new { fileName = objectKey, fileId });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                InvalidateAuth();
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"b2_delete_file_version for '{objectKey}' failed: HTTP {(int)response.StatusCode} {Truncate(body)}");
            }
        }

        /// End-to-end smoke test: upload random bytes, fetch them back
        /// anonymously through B2DownloadBaseUrl, byte-compare, delete.
        /// Throws on the first failing step (with the step named).
        public async Task RunSmokeTestAsync(CancellationToken cancellationToken)
        {
            var payload = RandomNumberGenerator.GetBytes(4096);
            var tempPath = Path.Combine(Path.GetTempPath(), $"b2-smoke-{Guid.NewGuid():N}.bin");
            await File.WriteAllBytesAsync(tempPath, payload, cancellationToken);
            try
            {
                var key = BuildObjectKey("smoke", "test", 0, "bin");
                Logger.Log($"b2-smoke: uploading 4096 random bytes as {key}");
                var fileId = await UploadFileAsync(tempPath, key, "application/octet-stream", cancellationToken);
                Logger.Log($"b2-smoke: upload OK (fileId {fileId}); fetching anonymously");

                var url = DownloadUrlFor(key);
                var fetched = await _http.GetByteArrayAsync(url, cancellationToken);
                if (!fetched.SequenceEqual(payload))
                {
                    throw new InvalidOperationException($"b2-smoke: downloaded bytes differ from uploaded bytes at {url}");
                }
                Logger.Log($"b2-smoke: anonymous fetch + byte compare OK: {url}");

                var wrongKeyUrl = DownloadUrlFor(BuildObjectKey("smoke", "test", 0, "bin"));
                using (var wrongResponse = await _http.GetAsync(wrongKeyUrl, cancellationToken))
                {
                    if (wrongResponse.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"b2-smoke: a never-uploaded random key unexpectedly returned HTTP {(int)wrongResponse.StatusCode} — bucket may be listable or misconfigured.");
                    }
                    Logger.Log($"b2-smoke: unguessability OK (random key -> HTTP {(int)wrongResponse.StatusCode})");
                }

                await DeleteFileAsync(key, fileId, cancellationToken);
                Logger.Log("b2-smoke: delete OK — all steps passed.");
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* temp cleanup only */ }
            }
        }

        private async Task<(string apiUrl, string authToken)> GetAuthAsync(CancellationToken cancellationToken)
        {
            if (_authToken != null && DateTime.UtcNow - _authAcquiredUtc < AuthMaxAge)
            {
                return (_apiUrl, _authToken);
            }

            await _authLock.WaitAsync(cancellationToken);
            try
            {
                if (_authToken != null && DateTime.UtcNow - _authAcquiredUtc < AuthMaxAge)
                {
                    return (_apiUrl, _authToken);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl);
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.B2KeyId}:{_settings.B2ApplicationKey}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                using var response = await _http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"b2_authorize_account failed: HTTP {(int)response.StatusCode} {Truncate(body)} — check B2KeyId/B2ApplicationKey.");
                }

                using var doc = JsonDocument.Parse(body);
                var token = doc.RootElement.GetProperty("authorizationToken").GetString();
                var storageApi = doc.RootElement.GetProperty("apiInfo").GetProperty("storageApi");
                var apiUrl = storageApi.GetProperty("apiUrl").GetString();
                var downloadUrl = storageApi.GetProperty("downloadUrl").GetString();

                // The settings-pinned public base must equal the account's
                // live download endpoint + bucket. Persisted event URLs last
                // forever, so a mismatch is a hard configuration error, never
                // something to silently correct.
                var expectedBase = $"{downloadUrl?.TrimEnd('/')}/file/{_settings.B2BucketName}";
                if (!string.Equals(_settings.B2DownloadBaseUrl, expectedBase, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"settings.json B2DownloadBaseUrl='{_settings.B2DownloadBaseUrl}' does not match the account's live endpoint '{expectedBase}'. Fix settings.json (persisted URLs must be correct forever).");
                }

                _authToken = token;
                _apiUrl = apiUrl;
                _authAcquiredUtc = DateTime.UtcNow;
                return (_apiUrl, _authToken);
            }
            finally
            {
                _authLock.Release();
            }
        }

        private void InvalidateAuth()
        {
            _authToken = null;
        }

        private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var sha1 = SHA1.Create();
            var hash = await sha1.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// B2 wants the file name URL-encoded in X-Bz-File-Name with '/'
        /// separators preserved; the same form is correct in download URLs.
        private static string EscapeKeyForUrl(string objectKey)
        {
            return string.Join("/", objectKey.Split('/').Select(Uri.EscapeDataString));
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 500 ? s : s.Substring(0, 500) + "...";
        }
    }
}
