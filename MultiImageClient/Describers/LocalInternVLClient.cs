
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class LocalInternVLClient : ILocalVisionModel
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly string _baseUrl;
        private readonly float _temperature;
        private readonly float _topP;
        private readonly int _topK;
        private readonly float _repetitionPenalty;
        private readonly bool _doSample;

        public LocalInternVLClient(
            string baseUrl = "http://127.0.0.1:11415",
            float temperature = 0.8f,
            float topP = 0.9f,
            int topK = 50,
            float repetitionPenalty = 1.1f,
            bool doSample = true)
        {
            _baseUrl = baseUrl;
            _temperature = temperature;
            _topP = topP;
            _topK = topK;
            _repetitionPenalty = repetitionPenalty;
            _doSample = doSample;
        }

        public string GetModelName() => "InternVL3-1B-Pretrained";

        public async Task<string> DescribeImageAsync(byte[] imageBytes, string prompt, int maxTokens = 512, float temperature = 0.8f)
        {
            try
            {
                string base64Image = Convert.ToBase64String(imageBytes);

                var requestBody = new InternVLGenerateRequest
                {
                    Image = $"data:image/png;base64,{base64Image}",
                    Prompt = prompt,
                    MaxTokens = maxTokens,
                    Temperature = temperature,
                    TopP = _topP,
                    TopK = _topK,
                    RepetitionPenalty = _repetitionPenalty,
                    DoSample = _doSample
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                string jsonBody = JsonSerializer.Serialize(requestBody, jsonOptions);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync($"{_baseUrl}/generate", content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"InternVL Flask endpoint returned error {response.StatusCode}: {responseString}");
                    return string.Empty;
                }

                var visionResponse = JsonSerializer.Deserialize<InternVLGenerateResponse>(responseString, jsonOptions);

                return visionResponse?.Response ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error describing image with InternVL: {ex.Message}\r\n{ex}");
                return string.Empty;
            }
        }

        private class InternVLGenerateRequest
        {
            [JsonPropertyName("image")]
            public required string Image { get; set; }

            [JsonPropertyName("prompt")]
            public required string Prompt { get; set; }

            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }

            [JsonPropertyName("temperature")]
            public float Temperature { get; set; } = 0.8f;

            [JsonPropertyName("top_p")]
            public float TopP { get; set; } = 0.9f;

            [JsonPropertyName("top_k")]
            public int TopK { get; set; } = 50;

            [JsonPropertyName("repetition_penalty")]
            public float RepetitionPenalty { get; set; } = 1.1f;

            [JsonPropertyName("do_sample")]
            public bool DoSample { get; set; } = true;
        }

        private class InternVLGenerateResponse
        {
            [JsonPropertyName("response")]
            public string Response { get; set; } = string.Empty;

            [JsonPropertyName("error")]
            public string Error { get; set; }
        }
    }
}


