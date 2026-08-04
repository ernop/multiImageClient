using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public enum Krea2Variant
    {
        MediumTurbo,
        Medium,
        Large,
    }

    public sealed class Krea2Generator : IImageGenerator
    {
        private const string BaseUrl = "https://api.krea.ai";
        private const string Resolution = "1K";
        private const string Creativity = "low";
        private const double StyleReferenceStrength = 0.6;
        private const long MaxAssetBytes = 75L * 1024 * 1024;

        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly Krea2Variant _variant;
        private readonly string _aspectRatio;
        private readonly string _inputImagePath;
        private readonly string _name;

        public Krea2Generator(
            string apiKey,
            int maxConcurrency,
            Krea2Variant variant,
            string aspectRatio,
            MultiClientRunStats stats,
            string name = "",
            string inputImagePath = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("Krea API key is required.", nameof(apiKey));
            }
            if (maxConcurrency < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
            }
            if (!SupportedAspectRatios.Contains(aspectRatio, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unsupported Krea 2 aspect ratio '{aspectRatio}'.", nameof(aspectRatio));
            }

            _variant = variant;
            _aspectRatio = aspectRatio;
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _name = name ?? "";
            _inputImagePath = inputImagePath;
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(6),
            };
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        public ImageGeneratorApiType ApiType => _variant switch
        {
            Krea2Variant.MediumTurbo => ImageGeneratorApiType.Krea2MediumTurbo,
            Krea2Variant.Medium => ImageGeneratorApiType.Krea2Medium,
            Krea2Variant.Large => ImageGeneratorApiType.Krea2Large,
            _ => throw new InvalidOperationException($"Unsupported Krea 2 variant {_variant}."),
        };

        public string GetFilenamePart(PromptDetails pd)
            => $"{ApiType}_{_aspectRatio.Replace(':', '-')}";

        public List<string> GetRightParts()
            => new() { GetGeneratorSpecPart(), _aspectRatio, Resolution };

        public string GetGeneratorSpecPart()
            => string.IsNullOrWhiteSpace(_name) ? VariantLabel : _name;

        public decimal GetCost()
        {
            var usesStyleReference = !string.IsNullOrWhiteSpace(_inputImagePath);
            return _variant switch
            {
                Krea2Variant.MediumTurbo => usesStyleReference ? 0.0175m : 0.015m,
                Krea2Variant.Medium => usesStyleReference ? 0.035m : 0.03m,
                Krea2Variant.Large => usesStyleReference ? 0.065m : 0.06m,
                _ => throw new InvalidOperationException($"No Krea 2 price for {_variant}."),
            };
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(
            IImageGenerator generator,
            PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            _stats.KreaImageGenerationRequestCount++;
            Guid? uploadedAssetId = null;
            try
            {
                var request = new Krea2Request
                {
                    Prompt = promptDetails.Prompt,
                    AspectRatio = _aspectRatio,
                    Resolution = Resolution,
                    Creativity = Creativity,
                };
                if (!string.IsNullOrWhiteSpace(_inputImagePath))
                {
                    var asset = await UploadAssetAsync(_inputImagePath);
                    uploadedAssetId = asset.Id;
                    request.ImageStyleReferences = new List<Krea2StyleReference>
                    {
                        new()
                        {
                            Url = asset.ImageUrl,
                            Strength = StyleReferenceStrength,
                        },
                    };
                    promptDetails.RuntimeMeta["input_image"] = Path.GetFileName(_inputImagePath);
                    promptDetails.RuntimeMeta["input_image_function"] = "style reference";
                    promptDetails.RuntimeMeta["style_reference_strength"] =
                        StyleReferenceStrength.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                promptDetails.RuntimeMeta["endpoint"] = Endpoint;
                promptDetails.RuntimeMeta["aspect_ratio"] = _aspectRatio;
                promptDetails.RuntimeMeta["resolution"] = Resolution;
                promptDetails.RuntimeMeta["creativity"] = Creativity;

                var completed = await SubmitAndWaitAsync(request);
                if (!string.Equals(completed.Status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Krea generation {completed.JobId} ended with status '{completed.Status}': "
                        + FormatError(completed.Error));
                }
                var urls = completed.Result?.Urls?
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (urls == null || urls.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Krea generation {completed.JobId} completed without a result URL.");
                }
                if (urls.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"Krea generation {completed.JobId} returned {urls.Count} result URLs; exactly one was expected.");
                }
                if (!Uri.TryCreate(urls[0], UriKind.Absolute, out var resultUri)
                    || resultUri.Scheme is not ("https" or "http"))
                {
                    throw new InvalidOperationException(
                        $"Krea generation {completed.JobId} returned an invalid result URL.");
                }

                _stats.KreaImageGenerationSuccessCount++;
                Logger.Log($"{promptDetails} Krea image generated by {VariantLabel}: {urls[0]}");
                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Url = urls[0],
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                };
            }
            catch (Exception ex)
            {
                _stats.KreaImageGenerationErrorCount++;
                Logger.Log($"{promptDetails} Krea error: {ex.Message}");
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    ImageGenerator = ApiType,
                };
            }
            finally
            {
                if (uploadedAssetId.HasValue)
                {
                    try
                    {
                        await DeleteAssetAsync(uploadedAssetId.Value);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(
                            $"Krea generated source asset {uploadedAssetId.Value:D} could not be deleted: {ex.Message}");
                    }
                }
                _semaphore.Release();
            }
        }

        private async Task<Krea2JobResponse> SubmitAndWaitAsync(Krea2Request request)
        {
            var submitted = await SendAsync(HttpMethod.Post, $"{BaseUrl}{Endpoint}", request, "submit-generation");
            if (submitted == null || submitted.JobId == Guid.Empty)
            {
                throw new HttpRequestException("Krea submit response did not contain a valid job_id.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var jobId = submitted.JobId;
            try
            {
                while (IsPending(submitted.Status))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                    submitted = await SendAsync(
                        HttpMethod.Get,
                        $"{BaseUrl}/jobs/{submitted.JobId:D}",
                        request: null,
                        operation: "poll-generation",
                        cancellationToken: timeout.Token);
                    if (submitted == null)
                    {
                        throw new HttpRequestException(
                            $"Krea returned an empty polling response for generation {jobId}.");
                    }
                    if (submitted.JobId == Guid.Empty)
                    {
                        throw new HttpRequestException(
                            $"Krea polling response for generation {jobId} did not contain job_id.");
                    }
                    if (submitted.JobId != jobId)
                    {
                        throw new HttpRequestException(
                            $"Krea polling response identity mismatch: requested {jobId}, received {submitted.JobId}.");
                    }
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Krea generation {submitted.JobId} remained pending for 5 minutes.");
            }

            if (string.IsNullOrWhiteSpace(submitted.Status))
            {
                throw new HttpRequestException(
                    $"Krea polling response for generation {submitted.JobId} did not contain a status.");
            }
            if (!string.Equals(submitted.Status, "completed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(submitted.Status, "failed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(submitted.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException(
                    $"Krea generation {submitted.JobId} returned unknown terminal status '{submitted.Status}'.");
            }
            return submitted;
        }

        private async Task<KreaAssetResponse> UploadAssetAsync(string path)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("Krea style reference image was not found.", path);
            }
            if (fileInfo.Length > MaxAssetBytes)
            {
                throw new InvalidOperationException(
                    $"Krea style reference image is {fileInfo.Length} bytes; the asset API limit is {MaxAssetBytes} bytes.");
            }
            var bytes = await File.ReadAllBytesAsync(path);
            var contentType = DetectImageContentType(bytes, path);
            using var form = new MultipartFormDataContent();
            using var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "file", Path.GetFileName(path));

            var url = $"{BaseUrl}/assets";
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage response = null;
            string responseContent = null;
            Exception error = null;
            try
            {
                response = await _httpClient.PostAsync(url, form);
                responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Krea asset upload returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseContent}",
                        null,
                        response.StatusCode);
                }
                var asset = JsonConvert.DeserializeObject<KreaAssetResponse>(responseContent);
                if (asset == null || asset.Id == Guid.Empty || string.IsNullOrWhiteSpace(asset.ImageUrl))
                {
                    throw new HttpRequestException(
                        "Krea asset upload did not return both a valid id and image_url.");
                }
                if (!Uri.TryCreate(asset.ImageUrl, UriKind.Absolute, out var assetUri)
                    || assetUri.Scheme is not ("https" or "http"))
                {
                    throw new HttpRequestException("Krea asset upload returned an invalid image_url.");
                }
                return asset;
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "krea",
                    "http",
                    "POST",
                    url,
                    startedAtUtc,
                    request: new { fileName = Path.GetFileName(path), contentType, sizeBytes = bytes.Length },
                    response: responseContent,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: error,
                    metadata: new { operation = "upload-style-reference" });
                response?.Dispose();
            }
        }

        private async Task DeleteAssetAsync(Guid assetId)
        {
            var url = $"{BaseUrl}/assets/{assetId:D}";
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage response = null;
            string responseContent = null;
            Exception error = null;
            try
            {
                response = await _httpClient.DeleteAsync(url);
                responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Krea asset deletion returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseContent}",
                        null,
                        response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "krea",
                    "http",
                    "DELETE",
                    url,
                    startedAtUtc,
                    request: new { assetId },
                    response: responseContent,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: error,
                    metadata: new { operation = "delete-style-reference" });
                response?.Dispose();
            }
        }

        private async Task<Krea2JobResponse> SendAsync(
            HttpMethod method,
            string url,
            Krea2Request request,
            string operation,
            CancellationToken cancellationToken = default)
        {
            var serialized = request == null
                ? null
                : JsonConvert.SerializeObject(request, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                });
            using var message = new HttpRequestMessage(method, url);
            if (serialized != null)
            {
                message.Content = new StringContent(serialized, Encoding.UTF8, "application/json");
            }

            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage response = null;
            string responseContent = null;
            Exception error = null;
            try
            {
                response = await _httpClient.SendAsync(message, cancellationToken);
                responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Krea API returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseContent}",
                        null,
                        response.StatusCode);
                }
                var parsed = JsonConvert.DeserializeObject<Krea2JobResponse>(responseContent);
                return parsed ?? throw new HttpRequestException("Krea API returned an empty JSON response.");
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "krea",
                    "http",
                    method.Method,
                    url,
                    startedAtUtc,
                    request: serialized,
                    response: responseContent,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: error,
                    metadata: new { operation, variant = _variant.ToString() });
                response?.Dispose();
            }
        }

        private string Endpoint => _variant switch
        {
            Krea2Variant.MediumTurbo => "/generate/image/krea/krea-2/medium-turbo",
            Krea2Variant.Medium => "/generate/image/krea/krea-2/medium",
            Krea2Variant.Large => "/generate/image/krea/krea-2/large",
            _ => throw new InvalidOperationException($"Unsupported Krea 2 variant {_variant}."),
        };

        private string VariantLabel => _variant switch
        {
            Krea2Variant.MediumTurbo => "Krea 2 Medium Turbo",
            Krea2Variant.Medium => "Krea 2 Medium",
            Krea2Variant.Large => "Krea 2 Large",
            _ => throw new InvalidOperationException($"Unsupported Krea 2 variant {_variant}."),
        };

        private static readonly string[] SupportedAspectRatios =
        {
            "1:1", "4:3", "3:2", "16:9", "2.35:1", "4:5", "2:3", "9:16",
        };

        private static bool IsPending(string status)
            => status is not null
                && (status.Equals("backlogged", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("queued", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("scheduled", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("processing", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("sampling", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("intermediate-complete", StringComparison.OrdinalIgnoreCase));

        private static string FormatError(Krea2Error error)
        {
            if (error == null)
            {
                return "the provider supplied no error details";
            }
            return string.IsNullOrWhiteSpace(error.Message)
                ? error.Code ?? "the provider supplied no error details"
                : $"{error.Code}: {error.Message}";
        }

        private static string DetectImageContentType(byte[] bytes, string path)
        {
            if (bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }
            if (bytes.Length >= 12
                && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
                && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            {
                return "image/webp";
            }
            throw new InvalidOperationException(
                $"Krea style reference images must be PNG, JPEG, or WEBP; '{path}' is none of those.");
        }

        private sealed class Krea2Request
        {
            [JsonProperty("prompt")]
            public string Prompt { get; set; }

            [JsonProperty("aspect_ratio")]
            public string AspectRatio { get; set; }

            [JsonProperty("resolution")]
            public string Resolution { get; set; }

            [JsonProperty("creativity")]
            public string Creativity { get; set; }

            [JsonProperty("image_style_references")]
            public List<Krea2StyleReference> ImageStyleReferences { get; set; }
        }

        private sealed class Krea2StyleReference
        {
            [JsonProperty("url")]
            public string Url { get; set; }

            [JsonProperty("strength")]
            public double Strength { get; set; }
        }

        private sealed class Krea2JobResponse
        {
            [JsonProperty("job_id")]
            public Guid JobId { get; set; }

            [JsonProperty("status")]
            public string Status { get; set; }

            [JsonProperty("result")]
            public Krea2Result Result { get; set; }

            [JsonProperty("error")]
            public Krea2Error Error { get; set; }
        }

        private sealed class Krea2Result
        {
            [JsonProperty("urls")]
            public List<string> Urls { get; set; }
        }

        private sealed class Krea2Error
        {
            [JsonProperty("code")]
            public string Code { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; }
        }

        private sealed class KreaAssetResponse
        {
            [JsonProperty("id")]
            public Guid Id { get; set; }

            [JsonProperty("image_url")]
            public string ImageUrl { get; set; }
        }
    }
}
