
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class LocalQwenClient : ILocalVisionModel
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly string _baseUrl;
        private readonly string _modelName;
        private readonly float _temperature;

        public LocalQwenClient(
            string baseUrl = "http://127.0.0.1:11435",
            string modelName = "qwen2-vl:latest",
            float temperature = 0.7f)
        {
            _baseUrl = baseUrl;
            _modelName = modelName;
            _temperature = temperature;
        }

        public string GetModelName() => _modelName;

        public async Task<string> DescribeImageAsync(byte[] imageBytes, string prompt, int maxTokens = 512, float temperature = 0.7f)
        {
            try
            {
                string base64Image = Convert.ToBase64String(imageBytes);

                var requestBody = new OllamaChatRequest
                {
                    Model = _modelName,
                    Stream = false,
                    KeepAlive = "5m",
                    Options = new OllamaOptions
                    {
                        Temperature = temperature,
                        NumPredict = maxTokens
                    },
                    Messages = new List<OllamaMessage>
                    {
                        new OllamaMessage
                        {
                            Role = "user",
                            Content = prompt,
                            Images = new List<string> { base64Image }
                        }
                    }
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                string jsonBody = JsonSerializer.Serialize(requestBody, jsonOptions);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync($"{_baseUrl}/api/chat", content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"Ollama endpoint returned error {response.StatusCode}: {responseString}");
                    return string.Empty;
                }

                var visionResponse = JsonSerializer.Deserialize<OllamaResponse>(responseString, jsonOptions);

                return visionResponse?.Message?.Content ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error describing image with Ollama: {ex.Message}\r\n{ex}");
                return string.Empty;
            }
        }

        private class OllamaChatRequest
        {
            [JsonPropertyName("model")]
            public required string Model { get; set; }

            [JsonPropertyName("stream")]
            public required bool Stream { get; set; }

            [JsonPropertyName("keep_alive")]
            public required string KeepAlive { get; set; }

            [JsonPropertyName("options")]
            public OllamaOptions? Options { get; set; }

            [JsonPropertyName("messages")]
            public required List<OllamaMessage> Messages { get; set; } = new();
        }

        private class OllamaMessage
        {
            [JsonPropertyName("role")]
            public required string Role { get; set; }

            [JsonPropertyName("content")]
            public required string Content { get; set; }

            [JsonPropertyName("images")]
            public required List<string> Images { get; set; } = new();
        }

        private class OllamaResponse
        {
            [JsonPropertyName("model")]
            public required string Model { get; set; }

            [JsonPropertyName("created_at")]
            public required DateTime CreatedAt { get; set; }

            [JsonPropertyName("message")]
            public required OllamaResponseMessage Message { get; set; } = new() { Role = string.Empty, Content = string.Empty };

            [JsonPropertyName("done")]
            public required bool Done { get; set; }
        }

        private class OllamaResponseMessage
        {
            [JsonPropertyName("role")]
            public required string Role { get; set; }

            [JsonPropertyName("content")]
            public required string Content { get; set; } = string.Empty;
        }

        private class OllamaOptions
        {
            [JsonPropertyName("temperature")]
            public float? Temperature { get; set; }

            [JsonPropertyName("num_predict")]
            public int? NumPredict { get; set; }

            [JsonPropertyName("top_p")]
            public float? TopP { get; set; }

            [JsonPropertyName("top_k")]
            public int? TopK { get; set; }
        }
    }
}


