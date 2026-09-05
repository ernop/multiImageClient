#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public static class DirectedPromptRewrite
    {
        public const string FableModel = "claude-fable-5-1";
        public const string OpenAiModel = "gpt-6-astra";
        public const string Instruction =
            "Carefully consider the user's intent and flesh it out very well. "
            + "The replacement text will be sent to intelligent image-generation endpoints. "
            + "Develop one coherent, specific image prompt with rich, purposeful visual details. "
            + "Explore subject, action, setting, composition, spatial relationships, materials, color, lighting, and expressive details where relevant. "
            + "Preserve explicit constraints, names, counts, requested wording, style, and the central idea. "
            + "Resolve underspecified details creatively without contradicting the source or replacing its concept. "
            + "Use clear natural language suited to intelligent image models; avoid keyword piles and generic quality slogans. "
            + "Default to bright, clear normal daytime lighting unless the source explicitly requests darkness or another lighting condition. "
            + "Keep the composition readable and visually organized. "
            + "Return only the complete expanded prompt, without commentary or alternative proposals.";

        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };
        private static readonly SemaphoreSlim Slots = new(2, 2);

        public static string? AvailabilityProblem(string model, Settings settings) => model switch
        {
            FableModel => ProviderKeyValidator.DescribeTextKeyProblem(nameof(settings.AnthropicApiKey), settings.AnthropicApiKey),
            OpenAiModel => ProviderKeyValidator.DescribeTextKeyProblem(nameof(settings.OpenAIApiKey), settings.OpenAIApiKey),
            _ => "Unknown rewrite model.",
        };

        public static async Task<ClaudePromptAdviceResult> RewriteAsync(string model, string prompt, Settings settings, CancellationToken ct)
        {
            var problem = AvailabilityProblem(model, settings);
            if (problem != null) throw new InvalidOperationException(problem);
            if (!await Slots.WaitAsync(0, ct)) throw new InvalidOperationException("Two prompt rewrites are already running. Try again after one finishes.");
            var wire = ClaudeService.BuildPromptAdviceWirePrompt(Instruction, prompt);
            var raw = "";
            try
            {
                var anthropic = model == FableModel;
                using var request = new HttpRequestMessage(HttpMethod.Post, anthropic
                    ? "https://api.anthropic.com/v1/messages" : "https://api.openai.com/v1/responses");
                if (anthropic)
                {
                    request.Headers.Add("x-api-key", settings.AnthropicApiKey);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                }
                else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAIApiKey);
                object payload = anthropic
                    ? new { model, max_tokens = 16000, system = ClaudeService.PromptAdviceSystemPrompt,
                        messages = new[] { new { role = "user", content = wire } } }
                    : new { model, max_output_tokens = 16000, instructions = ClaudeService.PromptAdviceSystemPrompt,
                        input = wire, reasoning = new { effort = "high" }, store = false };
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                // Bound provider output before buffering JSON in the resident server.
                await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, ct);
                raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"{model} rejected the rewrite (HTTP {(int)response.StatusCode}).");
                var replacement = ParseReplacement(model, raw);
                return new ClaudePromptAdviceResult { Model = model, WirePrompt = wire, RawResponse = raw, ResultPrompt = replacement };
            }
            catch (Exception ex)
            {
                return new ClaudePromptAdviceResult { Model = model, WirePrompt = wire, RawResponse = raw, Error = ex.Message };
            }
            finally { Slots.Release(); }
        }

        public static string ParseReplacement(string model, string raw)
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.GetProperty("model").GetString() != model)
                throw new InvalidDataException("The provider returned a different rewrite model.");
            var parts = new List<string>();
            if (model == FableModel)
            {
                if (root.GetProperty("stop_reason").GetString() != "end_turn")
                    throw new InvalidDataException(AnthropicMessagesManagerClient.DescribeStop(root) ?? "The rewrite did not finish.");
                foreach (var part in root.GetProperty("content").EnumerateArray())
                {
                    var type = part.GetProperty("type").GetString();
                    if (type == "text") parts.Add(part.GetProperty("text").GetString()!);
                    else if (type != "thinking" && type != "redacted_thinking")
                        throw new InvalidDataException("The rewrite contains unexpected provider content.");
                }
            }
            else if (model == OpenAiModel)
            {
                if (root.GetProperty("status").GetString() != "completed")
                    throw new InvalidDataException(ResponsesApiManagerClient.DescribeStop(root) ?? "The rewrite did not finish.");
                foreach (var item in root.GetProperty("output").EnumerateArray())
                {
                    var type = item.GetProperty("type").GetString();
                    if (type == "reasoning") continue;
                    if (type != "message" || item.GetProperty("role").GetString() != "assistant"
                        || item.GetProperty("status").GetString() != "completed")
                        throw new InvalidDataException("The rewrite contains an incomplete or unexpected message.");
                    foreach (var part in item.GetProperty("content").EnumerateArray())
                    {
                        if (part.GetProperty("type").GetString() != "output_text")
                            throw new InvalidDataException("The provider refused or returned unexpected rewrite content.");
                        parts.Add(part.GetProperty("text").GetString()!);
                    }
                }
            }
            else throw new InvalidDataException("Unknown rewrite model.");
            var text = string.Join("\n", parts);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000)
                throw new InvalidDataException("The replacement must contain between 1 and 100,000 characters.");
            return text;
        }
    }
}
