using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed class GrokVisionDescriber : ILocalVisionModel
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        private readonly string _apiKey;
        private readonly string _model;

        public GrokVisionDescriber(string apiKey, string model = "grok-4.3")
        {
            _apiKey = apiKey;
            _model = model;
        }

        public string GetModelName() => _model;

        public async Task<string> DescribeImageAsync(byte[] imageBytes, string prompt, int maxTokens = 512, float temperature = 0.8f)
        {
            var dataUri = "data:image/png;base64," + Convert.ToBase64String(imageBytes);
            var payload = new
            {
                model = _model,
                input = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "input_image", image_url = dataUri, detail = "high" },
                            new { type = "input_text", text = prompt },
                        },
                    },
                },
                max_output_tokens = maxTokens,
                temperature,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.ai/v1/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await HttpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"xAI vision describe returned {(int)response.StatusCode}: {body}");
            }

            return ExtractText(body);
        }

        private static string ExtractText(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            {
                return outputText.GetString() ?? "";
            }

            var chunks = new List<string>();
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            chunks.Add(text.GetString() ?? "");
                        }
                        else if (part.TryGetProperty("output_text", out var outputPart) && outputPart.ValueKind == JsonValueKind.String)
                        {
                            chunks.Add(outputPart.GetString() ?? "");
                        }
                    }
                }
            }

            return string.Join(Environment.NewLine, chunks).Trim();
        }
    }
}
