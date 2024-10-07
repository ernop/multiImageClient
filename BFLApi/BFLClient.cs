using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BFLAPIClient
{
    public class BFLClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string BaseUrl = "https://api.bfl.ml";
        public int DefaultPollingIntervalMs { get; set; } = 2000;

        public BFLClient(string apiKey, int defaultPollingIntervalMs = 2000)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("x-key", _apiKey);
            DefaultPollingIntervalMs = defaultPollingIntervalMs;
        }

        public async Task<GenerationResponse> GetResultAsync(string id)
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/v1/get_result?id={id}");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<GenerationResponse>(content);
        }

        private async Task<GenerationResponse> GenerateAndWaitForResultAsync<TRequest>(string endpoint, TRequest request)
        {
            var generationResponse = await GenerateAsync(endpoint, request);
            var id = generationResponse.Id;

            generationResponse = await GetResultAsync(id);

            while (generationResponse.Status == "Pending")
            {
                await Task.Delay(DefaultPollingIntervalMs);
                generationResponse = await GetResultAsync(id);
            }

            return generationResponse;
        }

        /// private since it's only called by the more convenient e.g. GenerateFluxPro11Async methods
        private async Task<GenerationResponse> GenerateAsync<TRequest>(string endpoint, TRequest request)
        {
            var serialized = JsonConvert.SerializeObject(request);
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
            var response2 = JsonConvert.DeserializeObject<GenerationResponse>(responseContent);
            return response2;
        }

        public Task<GenerationResponse> GenerateFluxPro11Async(FluxPro11Request request)
        {
            return GenerateAndWaitForResultAsync("flux-pro-1.1", request);
        }

        public Task<GenerationResponse> GenerateFluxProAsync(FluxProRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-pro", request);
        }

        public Task<GenerationResponse> GenerateFluxDevAsync(FluxDevRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-dev", request);
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
