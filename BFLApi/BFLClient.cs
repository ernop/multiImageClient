using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace BFLAPIClient
{
    public class BFLClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string BaseUrl = "https://api.bfl.ml";

        public BFLClient(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("x-key", _apiKey);
        }

        public async Task<GenerationResult> GetResultAsync(string id)
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/v1/get_result?id={id}");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<GenerationResult>(content);
        }

        private async Task<string> GenerateAsync<TRequest>(string endpoint, TRequest request)
        {
            var content = new StringContent(JsonConvert.SerializeObject(request), System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{BaseUrl}/v1/{endpoint}", content);
            response.EnsureSuccessStatusCode();
            var responseContent = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<GenerationResponse>(responseContent);
            return result.Id;
        }

        /// Generates an image using the Flux Pro 1.1 model.
        /// <param name="request">The request parameters for image generation.</param>
        /// <returns>The ID of the generated image request.</returns>
        public Task<string> GenerateFluxPro11Async(FluxPro11Request request) =>
            GenerateAsync("flux-pro-1.1", request);

        /// Generates an image using the Flux Pro model.
        /// <param name="request">The request parameters for image generation.</param>
        /// <returns>The ID of the generated image request.</returns>
        public Task<string> GenerateFluxProAsync(FluxProRequest request) =>
            GenerateAsync("flux-pro", request);

        /// Generates an image using the Flux Dev model.
        /// <param name="request">The request parameters for image generation.</param>
        /// <returns>The ID of the generated image request.</returns>
        public Task<string> GenerateFluxDevAsync(FluxDevRequest request) =>
            GenerateAsync("flux-dev", request);
    }

    public class GenerationResult
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public object Result { get; set; }
    }

    public class GenerationResponse
    {
        public string Id { get; set; }
    }

    /// Request parameters for the Flux Pro 1.1 model.
    public class FluxPro11Request
    {
        /// Text prompt for image generation.
        public string Prompt { get; set; }

        /// Width of the generated image in pixels. Must be a multiple of 32 and between 256 and 1440.
        public int Width { get; set; }

        /// Height of the generated image in pixels. Must be a multiple of 32 and between 256 and 1440.
        public int Height { get; set; }

        /// Whether to perform upsampling on the prompt.
        public bool PromptUpsampling { get; set; }

        /// Optional seed for reproducibility.
        public int? Seed { get; set; }

        /// Tolerance level for input and output moderation. Between 0 and 6, 0 being most strict, 6 being least strict.
        public int SafetyTolerance { get; set; }
    }

    /// Request parameters for the Flux Pro model.
    public class FluxProRequest
    {
        /// Text prompt for image generation.
        public string Prompt { get; set; }

        /// Width of the generated image in pixels. Must be a multiple of 32 and between 256 and 1440.
        public int Width { get; set; }

        /// Height of the generated image in pixels. Must be a multiple of 32 and between 256 and 1440.
        public int Height { get; set; }

        /// Number of steps for the image generation process. Must be between 1 and 50.
        public int? NumSteps { get; set; }

        /// Whether to perform upsampling on the prompt.
        public bool PromptUpsampling { get; set; }

        /// Optional seed for reproducibility.
        public int? Seed { get; set; }

        /// Guidance scale for image generation. Must be between 1.5 and 5.0.
        public float? Guidance { get; set; }

        /// Interval for image generation. Must be between 1.0 and 4.0.
        public float? Interval { get; set; }

        /// Tolerance level for input and output moderation. Between 0 and 6, 0 being most strict, 6 being least strict.
        public int SafetyTolerance { get; set; }
    }

    /// Request parameters for the Flux Dev model.
    public class FluxDevRequest
    {
        /// Text prompt for image generation.
        public string Prompt { get; set; }

        /// Width of the generated image in pixels. Must be a multiple of 32 and between 256 and 1440.
        public int Width { get; set; }

        /// Height of the generated image in pixels. Must be a multiple of 32 and between 256 and 1440.
        public int Height { get; set; }

        /// Number of steps for the image generation process. Must be between 1 and 50.
        public int? NumSteps { get; set; }

        /// Whether to perform upsampling on the prompt.
        public bool PromptUpsampling { get; set; }

        /// Optional seed for reproducibility.
        public int? Seed { get; set; }

        /// Guidance scale for image generation. Must be between 1.5 and 5.0.
        public float? Guidance { get; set; }

        /// Tolerance level for input and output moderation. Between 0 and 6, 0 being most strict, 6 being least strict.
        public int SafetyTolerance { get; set; }
    }
}
