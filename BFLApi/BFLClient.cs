using Newtonsoft.Json;

using System.Net.Http;
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

        public BFLClient(string apiKey, int defaultPollingIntervalMs = 2000)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("x-key", _apiKey);
            DefaultPollingIntervalMs = defaultPollingIntervalMs;
        }

        /// Polls whichever URL BFL gave us back in the submit response.
        /// Falls back to the legacy /v1/get_result?id= path only if no polling_url
        /// was supplied (older endpoints / cached responses).
        private async Task<GenerationResponse> GetResultAsync(string pollingUrl, string id)
        {
            var url = !string.IsNullOrEmpty(pollingUrl)
                ? pollingUrl
                : $"{BaseUrl}/v1/get_result?id={id}";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<GenerationResponse>(content);
        }

        private async Task<GenerationResponse> GenerateAndWaitForResultAsync<TRequest>(string endpoint, TRequest request)
        {
            var initial = await GenerateAsync(endpoint, request);
            var id = initial.Id;
            var pollingUrl = initial.PollingUrl;

            var current = await GetResultAsync(pollingUrl, id);

            while (current.Status == "Pending")
            {
                await Task.Delay(DefaultPollingIntervalMs);
                current = await GetResultAsync(pollingUrl, id);
            }

            return current;
        }

        private async Task<GenerationResponse> GenerateAsync<TRequest>(string endpoint, TRequest request)
        {
            var serialized = JsonConvert.SerializeObject(request, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            var content = new StringContent(serialized, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{BaseUrl}/v1/{endpoint}", content);

            if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"422 Unprocessable Entity: {errorContent}", null, System.Net.HttpStatusCode.UnprocessableEntity);
            }
            if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"402 Payment Required: {errorContent}", null, System.Net.HttpStatusCode.PaymentRequired);
            }

            response.EnsureSuccessStatusCode();
            var responseContent = await response.Content.ReadAsStringAsync();
            var parsed = JsonConvert.DeserializeObject<GenerationResponse>(responseContent);
            return parsed;
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
        /// Fixed $0.06/image regardless of resolution.
        public Task<GenerationResponse> GenerateFlux2FlexAsync(Flux2Request request)
        {
            return GenerateAndWaitForResultAsync("flux-2-flex", request);
        }

        /// Fastest / cheapest. 4B variant. Sub-second inference, $0.014/image.
        public Task<GenerationResponse> GenerateFlux2Klein4bAsync(Flux2Request request)
        {
            return GenerateAndWaitForResultAsync("flux-2-klein-4b", request);
        }

        /// Balanced klein. 9B variant, $0.015/image.
        public Task<GenerationResponse> GenerateFlux2Klein9bAsync(Flux2Request request)
        {
            return GenerateAndWaitForResultAsync("flux-2-klein-9b", request);
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
