using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiImageClient;

public sealed class B2UploadStallTests : IDisposable
{
    private const string Key = "ui/job/gen/0-0123456789abcdef0123456789abcdef.png";
    private readonly string _root = Directory.CreateTempSubdirectory("mic-b2-stall-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task UploadWithoutProgressFailsAfterTheStallWindow()
    {
        using var http = new HttpClient(new FakeB2(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }));
        var client = new B2StorageClient(Settings(), http, TimeSpan.FromMilliseconds(300));

        var started = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.UploadFileAsync(WriteFile(4096), Key, "image/png", "", CancellationToken.None));

        Assert.Contains("made no progress for", error.Message);
        Assert.InRange(started.ElapsedMilliseconds, 250, 10_000);
    }

    [Fact]
    public async Task SlowUploadThatKeepsReadingCompletes()
    {
        using var http = new HttpClient(new FakeB2(async (request, ct) =>
        {
            await using var body = await request.Content!.ReadAsStreamAsync(ct);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var buffer = new byte[1024];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                sha1.AppendData(buffer, 0, read);
                await Task.Delay(150, ct);
            }
            return Json(new
            {
                fileName = Key,
                contentSha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
                fileId = "file-1",
            });
        }));
        var client = new B2StorageClient(Settings(), http, TimeSpan.FromMilliseconds(400));

        var started = Stopwatch.StartNew();
        var fileId = await client.UploadFileAsync(WriteFile(4096), Key, "image/png", "", CancellationToken.None);

        Assert.Equal("file-1", fileId);
        Assert.True(started.ElapsedMilliseconds > 400, "The upload must outlast one stall window.");
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAStall()
    {
        using var http = new HttpClient(new FakeB2(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }));
        var client = new B2StorageClient(Settings(), http, TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.UploadFileAsync(WriteFile(4096), Key, "image/png", "", caller.Token));

        Assert.IsNotType<TimeoutException>(error);
    }

    private string WriteFile(int length)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(length));
        return path;
    }

    private static Settings Settings() => new()
    {
        EnableB2ImageHosting = true,
        B2KeyId = "key",
        B2ApplicationKey = "application-key",
        B2BucketId = "bucket-id",
        B2BucketName = "bucket",
        B2DownloadBaseUrl = "https://f000.b2.test/file/bucket",
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private sealed class FakeB2(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> upload)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return request.RequestUri!.ToString() switch
            {
                "https://api.backblazeb2.com/b2api/v3/b2_authorize_account" => Task.FromResult(Json(new
                {
                    authorizationToken = "auth",
                    apiInfo = new { storageApi = new { apiUrl = "https://api.b2.test", downloadUrl = "https://f000.b2.test" } },
                })),
                "https://api.b2.test/b2api/v3/b2_get_upload_url" => Task.FromResult(Json(new
                {
                    uploadUrl = "https://upload.b2.test/u",
                    authorizationToken = "upload",
                })),
                "https://upload.b2.test/u" => upload(request, cancellationToken),
                var url => throw new InvalidOperationException($"Unexpected request {url}"),
            };
        }
    }
}
