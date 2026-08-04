using Newtonsoft.Json;

using MultiImageClient;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BFLAPIClient
{
    public class BFLClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        // Global load-balanced endpoint. BFL migrated from api.bfl.ml -> api.bfl.ai
        // in 2025. Regional variants (api.us.bfl.ai / api.eu.bfl.ai) also exist; we
        // use the global one and rely on the polling_url BFL returns in each
        // response so we always poll the specific cluster that took the job.
        private const string BaseUrl = "https://api.bfl.ai";

        public int DefaultPollingIntervalMs { get; set; } = 2000;
        public TimeSpan PollingTimeout { get; set; } = TimeSpan.FromMinutes(5);

        public BFLClient(string apiKey, int defaultPollingIntervalMs = 2000)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("x-key", _apiKey);
            DefaultPollingIntervalMs = defaultPollingIntervalMs;
        }

        /// Polls the exact cluster URL BFL returned for this request.
        private async Task<GenerationResponse> GetResultAsync(
            string pollingUrl,
            string id,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(pollingUrl))
            {
                throw new InvalidOperationException(
                    $"BFL generation {id} did not provide the required polling_url.");
            }
            var url = pollingUrl;
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage response = null;
            string responseContent = null;
            Exception error = null;
            try
            {
                response = await _httpClient.GetAsync(url, cancellationToken);
                responseContent = await response.Content.ReadAsStringAsync();
                response.EnsureSuccessStatusCode();
                return JsonConvert.DeserializeObject<GenerationResponse>(responseContent);
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "bfl",
                    "http",
                    "GET",
                    url,
                    startedAtUtc,
                    request: new { id },
                    response: responseContent,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: error,
                    metadata: new { operation = "poll-generation" });
            }
        }

        private async Task<GenerationResponse> GenerateAndWaitForResultAsync<TRequest>(string endpoint, TRequest request)
        {
            var webhookUrl = typeof(TRequest).GetProperty("WebhookUrl")?.GetValue(request) as string;
            if (!string.IsNullOrWhiteSpace(webhookUrl))
            {
                throw new NotSupportedException(
                    $"BFL endpoint {endpoint}: webhook_url cannot be used with a generate-and-wait method.");
            }
            var initial = await GenerateAsync(endpoint, request);
            if (initial == null || string.IsNullOrWhiteSpace(initial.Id))
            {
                throw new HttpRequestException("BFL submit response did not contain a generation id.");
            }
            var id = initial.Id;
            var pollingUrl = initial.PollingUrl;
            if (string.IsNullOrWhiteSpace(pollingUrl))
            {
                throw new HttpRequestException(
                    $"BFL submit response for generation {id} did not contain the required polling_url. "
                    + "Webhook submissions are not supported by the generate-and-wait methods.");
            }
            using var timeoutCts = new CancellationTokenSource(PollingTimeout);

            try
            {
                var current = await GetResultAsync(pollingUrl, id, timeoutCts.Token);
                while (IsInProgressStatus(current?.Status))
                {
                    await Task.Delay(DefaultPollingIntervalMs, timeoutCts.Token);
                    current = await GetResultAsync(pollingUrl, id, timeoutCts.Token);
                }

                if (current == null)
                {
                    throw new HttpRequestException($"BFL returned an empty polling response for generation {id}.");
                }
                if (string.IsNullOrWhiteSpace(current.Status))
                {
                    throw new HttpRequestException($"BFL polling response for generation {id} did not contain a status.");
                }
                return current;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"BFL generation {id} remained pending for {PollingTimeout.TotalMinutes:0.#} minutes.");
            }
        }

        private static bool IsInProgressStatus(string status)
        {
            return status is not null
                && (status.Equals("Pending", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("Reasoning", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("Generating", StringComparison.OrdinalIgnoreCase));
        }

        private async Task<GenerationResponse> GenerateAsync<TRequest>(string endpoint, TRequest request)
        {
            var serialized = JsonConvert.SerializeObject(request, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            var content = new StringContent(serialized, System.Text.Encoding.UTF8, "application/json");
            var url = $"{BaseUrl}/v1/{endpoint}";
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage response = null;
            string responseContent = null;
            Exception error = null;
            try
            {
                response = await _httpClient.PostAsync(url, content);
                responseContent = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
                {
                    throw new HttpRequestException($"422 Unprocessable Entity: {responseContent}", null, System.Net.HttpStatusCode.UnprocessableEntity);
                }
                if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
                {
                    throw new HttpRequestException($"402 Payment Required: {responseContent}", null, System.Net.HttpStatusCode.PaymentRequired);
                }

                response.EnsureSuccessStatusCode();
                return JsonConvert.DeserializeObject<GenerationResponse>(responseContent);
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "bfl",
                    "http",
                    "POST",
                    url,
                    startedAtUtc,
                    request: serialized,
                    response: responseContent,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: error,
                    metadata: new { operation = "submit-generation", modelEndpoint = endpoint });
            }
        }

        // ---------- FLUX 1.1 (legacy but still supported) ----------

        public Task<GenerationResponse> GenerateFluxPro11Async(FluxPro11Request request)
        {
            return GenerateAndWaitForResultAsync("flux-pro-1.1", request);
        }

        public Task<GenerationResponse> GenerateFluxPro11UltraAsync(FluxPro11UltraRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-pro-1.1-ultra", request);
        }

        public Task<GenerationResponse> GenerateFluxProAsync(FluxProRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-pro", request);
        }

        public Task<GenerationResponse> GenerateFluxDevAsync(FluxDevRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-dev", request);
        }

        // ---------- FLUX.2 (current generation) ----------
        // All FLUX.2 variants share the same request/response shape (Flux2Request).
        // flex is the exception: it accepts extra steps/guidance fields, which live
        // on Flux2Request as nullables and are simply omitted for non-flex calls.

        /// Production-grade text-to-image. Megapixel-priced from $0.03/MP.
        public Task<GenerationResponse> GenerateFlux2ProAsync(Flux2Request request)
        {
            return GenerateAndWaitForResultAsync("flux-2-pro", request);
        }

        /// flux-2-pro-preview — where BFL lands the latest pro improvements first
        /// (currently: ~2x speed upgrade at no quality cost). Drop-in for flux-2-pro.
        public Task<GenerationResponse> GenerateFlux2ProPreviewAsync(Flux2Request request)
        {
            return GenerateAndWaitForResultAsync("flux-2-pro-preview", request);
        }

        /// Highest quality model; supports grounding search and multi-reference edits.
        /// From $0.07/MP.
        public Task<GenerationResponse> GenerateFlux2MaxAsync(Flux2Request request)
        {
            return GenerateAndWaitForResultAsync("flux-2-max", request);
        }

        /// Typography specialist with adjustable steps (up to 50) and guidance (1.5-10).
        /// Megapixel-priced from $0.05.
        public Task<GenerationResponse> GenerateFlux2FlexAsync(Flux2FlexRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-2-flex", request);
        }

        /// Fastest / cheapest. 4B variant. Sub-second inference, $0.014/image.
        public Task<GenerationResponse> GenerateFlux2Klein4bAsync(Flux2KleinRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-2-klein-4b", request);
        }

        /// Balanced klein. 9B variant, $0.015/image.
        public Task<GenerationResponse> GenerateFlux2Klein9bAsync(Flux2KleinRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-2-klein-9b", request);
        }

        /// Latest klein 9B improvements, including KV-cached inference.
        public Task<GenerationResponse> GenerateFlux2Klein9bPreviewAsync(Flux2KleinRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-2-klein-9b-preview", request);
        }

        // ---------- FLUX.1 Kontext (text + image editing) ----------
        // Separate shape because Kontext is edit-oriented: prompt + input_image +
        // aspect_ratio, no raw width/height.

        public Task<GenerationResponse> GenerateFluxKontextProAsync(FluxKontextRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-kontext-pro", request);
        }

        public Task<GenerationResponse> GenerateFluxKontextMaxAsync(FluxKontextRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-kontext-max", request);
        }
    }

    public class GenerationResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("result")]
        public GenerationResult Result { get; set; }

        /// URL the global/regional endpoints tell us to poll for this specific
        /// job. Required when submitting to api.bfl.ai; absent from legacy paths.
        [JsonProperty("polling_url")]
        public string PollingUrl { get; set; }
    }

    public class GenerationResult
    {
        /// The url pointing to the image
        [JsonProperty("sample")]
        public string Sample { get; set; }

        /// The revised prompt (?)
        [JsonProperty("prompt")]
        public string Prompt { get; set; }
    }
}
