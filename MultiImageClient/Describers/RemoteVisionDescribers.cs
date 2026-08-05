using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using IdeogramAPIClient;

namespace MultiImageClient
{
    // Remote image→text describers for the UI's describe targets. Request
    // shapes mirror the proven Python harness in tools/describe-eval
    // (describe_openai / describe_claude / describe_gemini / describe_ideogram);
    // GrokVisionDescriber already covers the xAI /v1/responses shape.
    // All of these throw on HTTP failure and return the extracted text as-is;
    // the caller treats blank text as a hard failure (fail closed — a
    // successful describe with no description is not a success).

    // Claude and Gemini validate the declared media type against the actual
    // bytes, so the data-URI/mime label must be the file's true type, never a
    // guessed "image/png". UI inputs are conformed to exactly these three
    // formats at upload time; anything else is a hard error here.
    internal static class DescriberImageFormat
    {
        public static string DetectMime(byte[] bytes)
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
                "Describe input must be PNG, JPEG, or WEBP; the provided bytes are none of those.");
        }
    }

    /// OpenAI Responses API vision describe (same request shape as the xAI
    /// /v1/responses transport in GrokVisionDescriber).
    public sealed class OpenAIVisionDescriber : ILocalVisionModel
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        private readonly string _apiKey;
        private readonly string _model;

        public OpenAIVisionDescriber(string apiKey, string model = "gpt-4.1")
        {
            _apiKey = apiKey;
            _model = model;
        }

        public string GetModelName() => _model;

        public async Task<string> DescribeImageAsync(byte[] imageBytes, string prompt, int maxTokens = 512, float temperature = 0.8f)
        {
            var mime = DescriberImageFormat.DetectMime(imageBytes);
            var dataUri = $"data:{mime};base64," + Convert.ToBase64String(imageBytes);
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
                            new { type = "input_text", text = prompt },
                            new { type = "input_image", image_url = dataUri, detail = "high" },
                        },
                    },
                },
                max_output_tokens = maxTokens,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await HttpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenAI vision describe returned {(int)response.StatusCode}: {body}");
            }
            return ExtractResponsesText(body);
        }

        // Shared with the xAI Responses transport: prefer output_text, else
        // concatenate every text part in the output array.
        internal static string ExtractResponsesText(string body)
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
                    }
                }
            }
            return string.Join(Environment.NewLine, chunks).Trim();
        }
    }

    /// Anthropic Messages API vision describe.
    public sealed class ClaudeVisionDescriber : ILocalVisionModel
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        private readonly string _apiKey;
        private readonly string _model;

        public ClaudeVisionDescriber(string apiKey, string model = "claude-sonnet-4-5")
        {
            _apiKey = apiKey;
            _model = model;
        }

        public string GetModelName() => _model;

        public async Task<string> DescribeImageAsync(byte[] imageBytes, string prompt, int maxTokens = 512, float temperature = 0.8f)
        {
            var mime = DescriberImageFormat.DetectMime(imageBytes);
            var payload = new
            {
                model = _model,
                max_tokens = maxTokens,
                temperature,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "image",
                                source = new { type = "base64", media_type = mime, data = Convert.ToBase64String(imageBytes) },
                            },
                            new { type = "text", text = prompt },
                        },
                    },
                },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            using var response = await HttpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Claude vision describe returned {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var chunks = new List<string>();
            if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        chunks.Add(text.GetString() ?? "");
                    }
                }
            }
            return string.Join(Environment.NewLine, chunks).Trim();
        }
    }

    /// Google Generative Language generateContent vision describe.
    public sealed class GeminiVisionDescriber : ILocalVisionModel
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        private readonly string _apiKey;
        private readonly string _model;

        public GeminiVisionDescriber(string apiKey, string model = "gemini-2.5-pro")
        {
            _apiKey = apiKey;
            _model = model;
        }

        public string GetModelName() => _model;

        public async Task<string> DescribeImageAsync(byte[] imageBytes, string prompt, int maxTokens = 512, float temperature = 0.8f)
        {
            var mime = DescriberImageFormat.DetectMime(imageBytes);
            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = prompt },
                            new { inline_data = new { mime_type = mime, data = Convert.ToBase64String(imageBytes) } },
                        },
                    },
                },
                generationConfig = new
                {
                    maxOutputTokens = maxTokens,
                    temperature,
                    thinkingConfig = new { thinkingBudget = 0 },
                },
            };

            var url = "https://generativelanguage.googleapis.com/v1beta/models/"
                + Uri.EscapeDataString(_model)
                + ":generateContent?key=" + Uri.EscapeDataString(_apiKey);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };

            using var response = await HttpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Gemini vision describe returned {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var chunks = new List<string>();
            if (doc.RootElement.TryGetProperty("candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array
                && candidates.GetArrayLength() > 0
                && candidates[0].TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        chunks.Add(text.GetString() ?? "");
                    }
                }
            }
            return string.Join("\n", chunks).Trim();
        }
    }

    /// Ideogram /describe. Takes NO instruction: the endpoint accepts only the
    /// image (plus an optional describe_model_version we leave at the API
    /// default), so the caller's prompt is deliberately unused — the UI labels
    /// this target as fixed-instruction.
    public sealed class IdeogramDescriber : ILocalVisionModel
    {
        private readonly IdeogramClient _client;

        public IdeogramDescriber(string apiKey)
        {
            _client = new IdeogramClient(apiKey);
        }

        public string GetModelName() => "ideogram-describe";

        public async Task<string> DescribeImageAsync(byte[] imageBytes, string prompt, int maxTokens = 512, float temperature = 0.8f)
        {
            var response = await _client.DescribeImageAsync(new IdeogramDescribeRequest
            {
                ImageFile = imageBytes,
            });
            if (response.Descriptions == null || response.Descriptions.Count == 0)
            {
                throw new InvalidDataException("Ideogram describe returned no descriptions.");
            }
            var chunks = new List<string>();
            foreach (var d in response.Descriptions)
            {
                if (!string.IsNullOrWhiteSpace(d.Text))
                {
                    chunks.Add(d.Text.Trim());
                }
            }
            return string.Join("\n", chunks).Trim();
        }
    }
}
