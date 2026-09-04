#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // Multi-turn text+image chat against the frontier text models used as the
    // goal loop "manager". The conversation is client-held: every call sends
    // the complete history (system prompt, user turns with images, assistant
    // turns as plain text). Assistant turns are replayed as TEXT ONLY — never
    // provider thinking/reasoning blocks — so a forked or edited history is
    // always a valid request on every provider (Claude 5 rejects thinking
    // blocks whose prefix changed). Every provider failure throws; nothing here
    // substitutes or retries with a different model.
    //
    // Images travel at the renderer's full resolution (owner requirement
    // 2026-09-04). Because the whole history is resent on every call, a
    // 30-turn loop can carry 30 multi-megabyte originals in one request. To
    // keep the shared-site process within its memory budget, image bytes are
    // loaded one at a time while the JSON body is streamed to a temp file,
    // and the request is sent from that file.

    // One image payload, produced on demand by the loader so that at most one
    // image's bytes are resident while the request body is written.
    public sealed class ManagerChatImagePayload
    {
        public required byte[] Bytes { get; init; }
        public required string Mime { get; init; }
        // Human label written into the redacted wire copy instead of the
        // base64: identity, dimensions, and exactly what conformance (if any)
        // was applied for this call.
        public required string Label { get; init; }
    }

    public sealed class ManagerChatImage
    {
        public required Func<CancellationToken, Task<ManagerChatImagePayload>> Load { get; init; }
    }

    public sealed class ManagerChatMessage
    {
        // "user" or "assistant".
        public required string Role { get; init; }
        public required string Text { get; init; }
        public IReadOnlyList<ManagerChatImage> Images { get; init; } = Array.Empty<ManagerChatImage>();
    }

    public sealed class ManagerChatReply
    {
        public required string Text { get; init; }
        // Provider-native reasoning where the API returns it (OpenAI reasoning
        // summaries, Claude thinking blocks, Gemini thought parts). Empty when
        // the provider returned none.
        public required string ProviderReasoning { get; init; }
        public required string RawResponse { get; init; }
        // Exact request JSON with every base64 image payload replaced by a
        // short placeholder — what the UI shows as "everything sent".
        public required string RedactedRequest { get; init; }
        // Exact byte length of the request body that was sent.
        public required long RequestBytes { get; init; }
        // The provider's own statement that this reply is not a normal
        // completion: a refusal, a safety block, or a length cut-off. Null on
        // an ordinary end-of-turn. The caller treats a non-null value as the
        // step's error and never parses Text as a design.
        public string? ProviderStop { get; init; }
        public int? InputTokens { get; init; }
        public int? OutputTokens { get; init; }
        public required long Ms { get; init; }
        public required string Model { get; init; }
    }

    public interface IManagerChatClient
    {
        string Model { get; }
        Task<ManagerChatReply> CompleteAsync(
            string systemPrompt,
            IReadOnlyList<ManagerChatMessage> history,
            CancellationToken ct);
    }

    // The provider's PUBLISHED hard limits on image input. These are the only
    // reasons an image is ever changed before it reaches the manager; there is
    // no app-side resolution preference. Null = the provider publishes no such
    // limit. Sources are cited per definition below (all read 2026-09-04).
    public sealed class ManagerImageLimits
    {
        // Per-image cap on the base64-encoded size (Anthropic states its cap
        // this way and rejects with the encoded byte count).
        public long? MaxImageBase64Bytes { get; init; }
        // Per-image cap on the raw file size.
        public long? MaxImageRawBytes { get; init; }
        // Per-image cap on either pixel dimension.
        public int? MaxEdgePx { get; init; }
        // Stricter per-image pixel cap that applies when the request carries
        // more than ManyImagesThreshold images (Anthropic: 2000 px above 20).
        public int? MaxEdgePxWhenMany { get; init; }
        public int? ManyImagesThreshold { get; init; }
        // Per-image cap on 32x32 patches after the provider's own resizing
        // (OpenAI rejects, rather than resizes, above this).
        public int? MaxPatches32 { get; init; }
        // Cap on the whole request body.
        public long? MaxRequestBytes { get; init; }
        // Image MIME types the provider accepts.
        public required IReadOnlyList<string> AcceptedMimes { get; init; }
        public required string Source { get; init; }

        public static readonly IReadOnlyList<string> PngJpegWebp = new[] { "image/png", "image/jpeg", "image/webp" };
        public static readonly IReadOnlyList<string> PngJpeg = new[] { "image/png", "image/jpeg" };

        public int? EffectiveMaxEdgePx(int imagesInRequest)
        {
            if (MaxEdgePxWhenMany != null && ManyImagesThreshold != null && imagesInRequest > ManyImagesThreshold.Value)
            {
                return MaxEdgePx == null ? MaxEdgePxWhenMany : Math.Min(MaxEdgePx.Value, MaxEdgePxWhenMany.Value);
            }
            return MaxEdgePx;
        }

        public static long Base64Length(long rawBytes) => (rawBytes + 2) / 3 * 4;

        public static int Patches32(int width, int height)
            => (int)Math.Ceiling(width / 32.0) * (int)Math.Ceiling(height / 32.0);
    }

    public sealed class ManagerDefinition
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required string Provider { get; init; }
        public required string Model { get; init; }
        public required string SettingsKeyName { get; init; }
        // Published USD per million tokens (input, output). Null = no verified
        // price on file; the UI then shows token counts without a dollar figure.
        public decimal? InputUsdPerMTok { get; init; }
        public decimal? OutputUsdPerMTok { get; init; }
        public required string Detail { get; init; }
        public required ManagerImageLimits ImageLimits { get; init; }
    }

    public static class ManagerCatalog
    {
        public const string KeyGpt56Sol = "manager-gpt-5.6-sol";
        public const string KeyClaudeFable51 = "manager-claude-fable-5-1";
        public const string KeyClaudeOpus5 = "manager-claude-opus-5";
        public const string KeyClaudeSonnet5 = "manager-claude-sonnet-5";
        public const string KeyGemini35Flash = "manager-gemini-3.5-flash";
        public const string KeyGrok46 = "manager-grok-4.6";

        // developers.openai.com/api/docs/guides/images-vision (2026-09-04):
        // 512 MB total payload, 1,500 images, 30,000 patches per image after
        // the detail level's resizing; detail "original" preserves dimensions
        // for gpt-5.6-*. Live-verified same day: an 8.4 MB 2496x1664 PNG at
        // "original" = 6,010 image tokens (2,924 at "high").
        public static readonly ManagerImageLimits OpenAiLimits = new()
        {
            MaxPatches32 = 30_000,
            MaxRequestBytes = 512L * 1024 * 1024,
            AcceptedMimes = ManagerImageLimits.PngJpegWebp,
            Source = "OpenAI images-vision guide: 512 MB request, 30,000 patches/image, detail original",
        };

        // platform.claude.com/docs/en/build-with-claude/vision (2026-09-04):
        // 10 MB base64-encoded per image on the direct API, 8000x8000 px,
        // 2000 px per side when a request carries more than 20 images, 32 MB
        // per request. Live-verified same day: 11,174,516 base64 bytes
        // rejected with "image exceeds 10 MB maximum ... > 10485760 bytes".
        public static readonly ManagerImageLimits AnthropicLimits = new()
        {
            MaxImageBase64Bytes = 10L * 1024 * 1024,
            MaxEdgePx = 8000,
            MaxEdgePxWhenMany = 2000,
            ManyImagesThreshold = 20,
            MaxRequestBytes = 32L * 1024 * 1024,
            AcceptedMimes = ManagerImageLimits.PngJpegWebp,
            Source = "Anthropic vision docs: 10 MB base64/image, 8000 px (2000 px above 20 images), 32 MB request",
        };

        // ai.google.dev/gemini-api/docs/file-input-methods (2026-09-04):
        // inline data 100 MB per request (raised from 20 MB); png/jpeg/webp/
        // bmp. Per-part mediaResolution MEDIA_RESOLUTION_ULTRA_HIGH is the
        // highest vision budget (2,240 tokens); live-verified on v1beta.
        public static readonly ManagerImageLimits GeminiLimits = new()
        {
            MaxRequestBytes = 100L * 1024 * 1024,
            AcceptedMimes = ManagerImageLimits.PngJpegWebp,
            Source = "Gemini file-input-methods: 100 MB inline request; ULTRA_HIGH media resolution",
        };

        // docs.x.ai/developers/models (2026-09-04): 20 MiB per image, no image
        // count limit, jpg/jpeg or png only. Live-verified same day: an 11 MB
        // image fails with "Response is too large to store" unless
        // store=false; the detail field has no effect on token count.
        public static readonly ManagerImageLimits XaiLimits = new()
        {
            MaxImageRawBytes = 20L * 1024 * 1024,
            AcceptedMimes = ManagerImageLimits.PngJpeg,
            Source = "xAI models page: 20 MiB/image, jpg/png only; store=false required for large images",
        };

        // Model IDs verified 2026-09-04: Anthropic models overview lists
        // claude-fable-5-1 / claude-opus-5 / claude-sonnet-5 with $10/$50,
        // $5/$25, $2/$10 per MTok. gpt-5.6-sol, gemini-3.5-flash, and grok-4.6
        // are the describe-endpoint models already live in this app. No public
        // gemini-3.5-pro exists (partner testing only), so it is not offered.
        public static readonly IReadOnlyList<ManagerDefinition> All = new[]
        {
            new ManagerDefinition
            {
                Key = KeyGpt56Sol, Label = "GPT-5.6 Sol (OpenAI)", Provider = "openai", Model = "gpt-5.6-sol",
                SettingsKeyName = nameof(Settings.OpenAIApiKey),
                Detail = "OpenAI Responses API with reasoning summaries; JSON object output mode; images at detail original (full resolution).",
                ImageLimits = OpenAiLimits,
            },
            new ManagerDefinition
            {
                Key = KeyClaudeFable51, Label = "Claude Fable 5.1 (Anthropic)", Provider = "anthropic", Model = "claude-fable-5-1",
                SettingsKeyName = nameof(Settings.AnthropicApiKey),
                InputUsdPerMTok = 10m, OutputUsdPerMTok = 50m,
                Detail = "Anthropic Messages API. Adaptive thinking is always on; thinking blocks are shown as provider reasoning.",
                ImageLimits = AnthropicLimits,
            },
            new ManagerDefinition
            {
                Key = KeyClaudeOpus5, Label = "Claude Opus 5 (Anthropic)", Provider = "anthropic", Model = "claude-opus-5",
                SettingsKeyName = nameof(Settings.AnthropicApiKey),
                InputUsdPerMTok = 5m, OutputUsdPerMTok = 25m,
                Detail = "Anthropic Messages API with adaptive thinking.",
                ImageLimits = AnthropicLimits,
            },
            new ManagerDefinition
            {
                Key = KeyClaudeSonnet5, Label = "Claude Sonnet 5 (Anthropic)", Provider = "anthropic", Model = "claude-sonnet-5",
                SettingsKeyName = nameof(Settings.AnthropicApiKey),
                InputUsdPerMTok = 2m, OutputUsdPerMTok = 10m,
                Detail = "Anthropic Messages API with adaptive thinking.",
                ImageLimits = AnthropicLimits,
            },
            new ManagerDefinition
            {
                Key = KeyGemini35Flash, Label = "Gemini 3.5 Flash (Google)", Provider = "google", Model = "gemini-3.5-flash",
                SettingsKeyName = nameof(Settings.GoogleGeminiApiKey),
                Detail = "Google generateContent with high thinking level and thought parts returned; JSON MIME output mode; images at MEDIA_RESOLUTION_ULTRA_HIGH.",
                ImageLimits = GeminiLimits,
            },
            new ManagerDefinition
            {
                Key = KeyGrok46, Label = "Grok 4.6 (xAI)", Provider = "xai", Model = "grok-4.6",
                SettingsKeyName = nameof(Settings.XAIGrokApiKey),
                Detail = "xAI Responses API (api.x.ai), store=false so large images are accepted.",
                ImageLimits = XaiLimits,
            },
        };

        public static ManagerDefinition? Find(string key)
            => All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));

        public static string? DescribeAvailabilityProblem(ManagerDefinition definition, Settings settings)
        {
            var value = definition.SettingsKeyName switch
            {
                nameof(Settings.OpenAIApiKey) => settings.OpenAIApiKey,
                nameof(Settings.AnthropicApiKey) => settings.AnthropicApiKey,
                nameof(Settings.GoogleGeminiApiKey) => settings.GoogleGeminiApiKey,
                nameof(Settings.XAIGrokApiKey) => settings.XAIGrokApiKey,
                _ => throw new InvalidOperationException($"Manager {definition.Key} names an unknown settings key {definition.SettingsKeyName}."),
            };
            return ProviderKeyValidator.DescribeTextKeyProblem(definition.SettingsKeyName, value);
        }

        public static IManagerChatClient Build(ManagerDefinition definition, Settings settings)
        {
            var problem = DescribeAvailabilityProblem(definition, settings);
            if (problem != null)
            {
                throw new InvalidOperationException($"Manager {definition.Label} is not available: {problem}");
            }
            return definition.Provider switch
            {
                "openai" => new ResponsesApiManagerClient(
                    "https://api.openai.com/v1/responses", settings.OpenAIApiKey, definition, includeReasoningSummary: true),
                "xai" => new ResponsesApiManagerClient(
                    "https://api.x.ai/v1/responses", settings.XAIGrokApiKey, definition, includeReasoningSummary: false),
                "anthropic" => new AnthropicMessagesManagerClient(settings.AnthropicApiKey, definition),
                "google" => new GeminiManagerClient(settings.GoogleGeminiApiKey, definition),
                _ => throw new InvalidOperationException($"Manager {definition.Key} has unknown provider {definition.Provider}."),
            };
        }

        public static decimal? EstimateCostUsd(ManagerDefinition definition, int? inputTokens, int? outputTokens)
        {
            if (definition.InputUsdPerMTok == null || definition.OutputUsdPerMTok == null
                || inputTokens == null || outputTokens == null)
            {
                return null;
            }
            return (inputTokens.Value * definition.InputUsdPerMTok.Value
                + outputTokens.Value * definition.OutputUsdPerMTok.Value) / 1_000_000m;
        }
    }

    internal static class ManagerChatHttp
    {
        public static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromMinutes(15),
        };

        public const int MaxOutputTokens = 16000;

        // Relaxed escaping keeps prompts and placeholders readable in the
        // stored copy (no \u003C for "<", no escaped non-ASCII); the body
        // remains valid JSON for every provider.
        private static readonly JsonWriterOptions WriterOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        };

        public static string Placeholder(ManagerChatImagePayload image, int ordinal)
            => $"<image {ordinal}: {image.Label}, {image.Mime}, {image.Bytes.Length:N0} bytes — base64 omitted from this copy>";

        // Writes the request body twice through the provider's writer: once
        // for real into a temp file (images loaded one at a time, base64
        // streamed by Utf8JsonWriter), once redacted into memory using the
        // labels collected during the real pass. Enforces the provider's
        // published request-size cap on the exact file length, then sends
        // the file. Nothing larger than one image plus the writer buffer is
        // resident at any point.
        public static async Task<ManagerChatReply> SendAsync(
            HttpRequestMessage request,
            string providerName,
            ManagerDefinition definition,
            Func<Utf8JsonWriter, Func<int, ManagerChatImage, Task<ManagerChatImagePayload>>, bool, Task> writePayload,
            Func<string, JsonElement, ManagerChatReply> parse,
            CancellationToken ct)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"mic-manager-{Guid.NewGuid():N}.json");
            var labels = new List<ManagerChatImagePayload>();
            try
            {
                long requestBytes;
                await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await using (var writer = new Utf8JsonWriter(file, WriterOptions))
                {
                    await writePayload(writer, async (ordinal, image) =>
                    {
                        var payload = await image.Load(ct);
                        labels.Add(payload);
                        return payload;
                    }, false);
                    await writer.FlushAsync(ct);
                    requestBytes = file.Length;
                }

                var cap = definition.ImageLimits.MaxRequestBytes;
                if (cap != null && requestBytes > cap.Value)
                {
                    throw new InvalidOperationException(
                        $"{providerName}: the request body would be {requestBytes:N0} bytes with {labels.Count} image(s) at full resolution, "
                        + $"over the provider's published {cap.Value:N0}-byte request cap ({definition.ImageLimits.Source}). "
                        + "The loop stops rather than shrink or drop images; fork from an earlier entry to continue with fewer images.");
                }

                string redacted;
                using (var ms = new MemoryStream())
                {
                    await using (var writer = new Utf8JsonWriter(ms, WriterOptions))
                    {
                        await writePayload(writer, (ordinal, image) =>
                        {
                            var payload = labels[ordinal - 1];
                            return Task.FromResult(new ManagerChatImagePayload
                            {
                                Bytes = Encoding.UTF8.GetBytes(Placeholder(payload, ordinal)),
                                Mime = payload.Mime,
                                Label = payload.Label,
                            });
                        }, true);
                        await writer.FlushAsync(ct);
                    }
                    redacted = Encoding.UTF8.GetString(ms.ToArray());
                }

                var sw = Stopwatch.StartNew();
                string body;
                await using (var send = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                {
                    request.Content = new StreamContent(send);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                    request.Content.Headers.ContentLength = requestBytes;
                    using var response = await Client.SendAsync(request, ct);
                    body = await response.Content.ReadAsStringAsync(ct);
                    sw.Stop();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"{providerName} returned HTTP {(int)response.StatusCode}: {body}");
                    }
                }

                using var doc = JsonDocument.Parse(body);
                var reply = parse(body, doc.RootElement);
                return new ManagerChatReply
                {
                    Text = reply.Text,
                    ProviderReasoning = reply.ProviderReasoning,
                    RawResponse = body,
                    RedactedRequest = redacted,
                    RequestBytes = requestBytes,
                    ProviderStop = reply.ProviderStop,
                    InputTokens = reply.InputTokens,
                    OutputTokens = reply.OutputTokens,
                    Ms = sw.ElapsedMilliseconds,
                    Model = reply.Model,
                };
            }
            finally
            {
                try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // Writes a data URI string value: "data:<mime>;base64,<...>". The
        // base64 string for one image is transient; nothing else is retained.
        public static void WriteDataUri(Utf8JsonWriter writer, string propertyName, ManagerChatImagePayload payload, bool redacted)
        {
            if (redacted)
            {
                writer.WriteString(propertyName, Encoding.UTF8.GetString(payload.Bytes));
                return;
            }
            writer.WriteString(propertyName, "data:" + payload.Mime + ";base64," + Convert.ToBase64String(payload.Bytes));
        }

        public static void WriteBase64OrPlaceholder(Utf8JsonWriter writer, string propertyName, ManagerChatImagePayload payload, bool redacted)
        {
            if (redacted)
            {
                writer.WriteString(propertyName, Encoding.UTF8.GetString(payload.Bytes));
                return;
            }
            writer.WriteBase64String(propertyName, payload.Bytes);
        }

        public static string? ReadString(JsonElement parent, string name)
        {
            if (parent.ValueKind == JsonValueKind.Object
                && parent.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
            return null;
        }

        public static int? ReadInt(JsonElement parent, string name)
        {
            if (parent.ValueKind == JsonValueKind.Object
                && parent.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var i))
            {
                return i;
            }
            return null;
        }

        public static ManagerChatReply Partial(string model, string text, string reasoning, string? stop, JsonElement usage, string inputKey, string outputKey)
            => new()
            {
                Text = text,
                ProviderReasoning = reasoning,
                RawResponse = "",
                RedactedRequest = "",
                RequestBytes = 0,
                ProviderStop = stop,
                InputTokens = ReadInt(usage, inputKey),
                OutputTokens = ReadInt(usage, outputKey),
                Ms = 0,
                Model = model,
            };
    }

    /// OpenAI-compatible Responses API (api.openai.com and api.x.ai). System
    /// prompt travels as `instructions`; images as data-URI `input_image`
    /// parts; assistant turns as `output_text` parts. OpenAI gpt-5.6 gets
    /// `detail: "original"` (full resolution). xAI gets no detail field (no
    /// effect, verified) and `store: false` (required for large images).
    public sealed class ResponsesApiManagerClient : IManagerChatClient
    {
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly ManagerDefinition _definition;
        private readonly bool _includeReasoningSummary;

        public string Model => _definition.Model;

        public ResponsesApiManagerClient(string endpoint, string apiKey, ManagerDefinition definition, bool includeReasoningSummary)
        {
            _endpoint = endpoint;
            _apiKey = apiKey;
            _definition = definition;
            _includeReasoningSummary = includeReasoningSummary;
        }

        private bool IsXai => _definition.Provider == "xai";

        public async Task<ManagerChatReply> CompleteAsync(
            string systemPrompt, IReadOnlyList<ManagerChatMessage> history, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return await ManagerChatHttp.SendAsync(
                request,
                $"{_endpoint} ({Model})",
                _definition,
                (writer, load, redacted) => WritePayloadAsync(writer, systemPrompt, history, load, redacted),
                (body, root) => Parse(root),
                ct);
        }

        private async Task WritePayloadAsync(
            Utf8JsonWriter w, string systemPrompt, IReadOnlyList<ManagerChatMessage> history,
            Func<int, ManagerChatImage, Task<ManagerChatImagePayload>> load, bool redacted)
        {
            w.WriteStartObject();
            w.WriteString("model", Model);
            w.WriteString("instructions", systemPrompt);
            w.WritePropertyName("input");
            w.WriteStartArray();
            var ordinal = 0;
            foreach (var message in history)
            {
                w.WriteStartObject();
                if (message.Role == "assistant")
                {
                    w.WriteString("role", "assistant");
                    w.WritePropertyName("content");
                    w.WriteStartArray();
                    w.WriteStartObject();
                    w.WriteString("type", "output_text");
                    w.WriteString("text", message.Text);
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                    continue;
                }
                w.WriteString("role", "user");
                w.WritePropertyName("content");
                w.WriteStartArray();
                w.WriteStartObject();
                w.WriteString("type", "input_text");
                w.WriteString("text", message.Text);
                w.WriteEndObject();
                foreach (var image in message.Images)
                {
                    ordinal++;
                    var payload = await load(ordinal, image);
                    w.WriteStartObject();
                    w.WriteString("type", "input_image");
                    ManagerChatHttp.WriteDataUri(w, "image_url", payload, redacted);
                    if (!IsXai)
                    {
                        w.WriteString("detail", "original");
                    }
                    w.WriteEndObject();
                    await w.FlushAsync();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("max_output_tokens", ManagerChatHttp.MaxOutputTokens);
            w.WritePropertyName("text");
            w.WriteStartObject();
            w.WritePropertyName("format");
            w.WriteStartObject();
            w.WriteString("type", "json_object");
            w.WriteEndObject();
            w.WriteEndObject();
            if (Model.StartsWith("gpt-5", StringComparison.Ordinal))
            {
                w.WritePropertyName("reasoning");
                w.WriteStartObject();
                w.WriteString("effort", "medium");
                if (_includeReasoningSummary)
                {
                    w.WriteString("summary", "auto");
                }
                w.WriteEndObject();
            }
            else if (Model.StartsWith("grok-4", StringComparison.Ordinal))
            {
                w.WritePropertyName("reasoning");
                w.WriteStartObject();
                w.WriteString("effort", "medium");
                w.WriteEndObject();
            }
            if (IsXai)
            {
                w.WriteBoolean("store", false);
            }
            w.WriteEndObject();
        }

        private ManagerChatReply Parse(JsonElement root)
        {
            var text = new List<string>();
            var reasoning = new List<string>();
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "reasoning" && item.TryGetProperty("summary", out var summary)
                        && summary.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in summary.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var st) && st.ValueKind == JsonValueKind.String)
                            {
                                reasoning.Add(st.GetString() ?? "");
                            }
                        }
                        continue;
                    }
                    if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String)
                        {
                            text.Add(pt.GetString() ?? "");
                        }
                    }
                }
            }
            var usage = root.TryGetProperty("usage", out var u) ? u : default;
            return ManagerChatHttp.Partial(
                Model,
                string.Join(Environment.NewLine, text).Trim(),
                string.Join(Environment.NewLine + Environment.NewLine, reasoning).Trim(),
                DescribeStop(root),
                usage, "input_tokens", "output_tokens");
        }

        // Responses API: a non-"completed" status (incomplete with
        // incomplete_details.reason such as max_output_tokens or
        // content_filter, or failed with an error object) or a "refusal"
        // content part means the model did not answer.
        public static string? DescribeStop(JsonElement root)
        {
            var status = ManagerChatHttp.ReadString(root, "status");
            if (status != null && status != "completed")
            {
                var reason = root.TryGetProperty("incomplete_details", out var inc)
                    ? ManagerChatHttp.ReadString(inc, "reason")
                    : null;
                var error = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                    ? ManagerChatHttp.ReadString(err, "message")
                    : null;
                return $"provider status \"{status}\""
                    + (reason != null ? $" ({reason})" : "")
                    + (error != null ? $": {error}" : "");
            }
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
                        if (ManagerChatHttp.ReadString(part, "type") == "refusal")
                        {
                            var refusal = ManagerChatHttp.ReadString(part, "refusal");
                            return "provider refusal" + (string.IsNullOrWhiteSpace(refusal) ? "" : $": {refusal}");
                        }
                    }
                }
            }
            return null;
        }
    }

    /// Anthropic Messages API. Claude 5 models take no `thinking` field
    /// (adaptive thinking is always on; both enabled and disabled return 400
    /// on Fable 5.x). Thinking blocks in the response are captured as
    /// provider reasoning and are never sent back.
    public sealed class AnthropicMessagesManagerClient : IManagerChatClient
    {
        private readonly string _apiKey;
        private readonly ManagerDefinition _definition;

        public string Model => _definition.Model;

        public AnthropicMessagesManagerClient(string apiKey, ManagerDefinition definition)
        {
            _apiKey = apiKey;
            _definition = definition;
        }

        public async Task<ManagerChatReply> CompleteAsync(
            string systemPrompt, IReadOnlyList<ManagerChatMessage> history, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            return await ManagerChatHttp.SendAsync(
                request,
                $"Anthropic ({Model})",
                _definition,
                (writer, load, redacted) => WritePayloadAsync(writer, systemPrompt, history, load, redacted),
                (body, root) => Parse(root),
                ct);
        }

        private async Task WritePayloadAsync(
            Utf8JsonWriter w, string systemPrompt, IReadOnlyList<ManagerChatMessage> history,
            Func<int, ManagerChatImage, Task<ManagerChatImagePayload>> load, bool redacted)
        {
            w.WriteStartObject();
            w.WriteString("model", Model);
            w.WriteNumber("max_tokens", ManagerChatHttp.MaxOutputTokens);
            w.WriteString("system", systemPrompt);
            w.WritePropertyName("messages");
            w.WriteStartArray();
            var ordinal = 0;
            foreach (var message in history)
            {
                w.WriteStartObject();
                if (message.Role == "assistant")
                {
                    w.WriteString("role", "assistant");
                    w.WritePropertyName("content");
                    w.WriteStartArray();
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", message.Text);
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                    continue;
                }
                w.WriteString("role", "user");
                w.WritePropertyName("content");
                w.WriteStartArray();
                foreach (var image in message.Images)
                {
                    ordinal++;
                    var payload = await load(ordinal, image);
                    w.WriteStartObject();
                    w.WriteString("type", "image");
                    w.WritePropertyName("source");
                    w.WriteStartObject();
                    w.WriteString("type", "base64");
                    w.WriteString("media_type", payload.Mime);
                    ManagerChatHttp.WriteBase64OrPlaceholder(w, "data", payload, redacted);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    await w.FlushAsync();
                }
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", message.Text);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        private ManagerChatReply Parse(JsonElement root)
        {
            var text = new List<string>();
            var thinking = new List<string>();
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "text" && part.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String)
                    {
                        text.Add(pt.GetString() ?? "");
                    }
                    else if (type == "thinking" && part.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String)
                    {
                        thinking.Add(th.GetString() ?? "");
                    }
                }
            }
            var usage = root.TryGetProperty("usage", out var u) ? u : default;
            return ManagerChatHttp.Partial(
                Model,
                string.Join(Environment.NewLine, text).Trim(),
                string.Join(Environment.NewLine + Environment.NewLine, thinking).Trim(),
                DescribeStop(root),
                usage, "input_tokens", "output_tokens");
        }

        // Messages API: stop_reason "end_turn" (or "stop_sequence") is a
        // normal completion. "refusal" arrives with an empty content array
        // and stop_details {category, explanation} — observed live on Fable
        // 5.1 as category "reasoning_extraction" (2026-09-04). "max_tokens"
        // means the JSON reply was cut off.
        public static string? DescribeStop(JsonElement root)
        {
            var stop = ManagerChatHttp.ReadString(root, "stop_reason");
            if (stop == null || stop == "end_turn" || stop == "stop_sequence")
            {
                return null;
            }
            var sb = new StringBuilder("provider stop_reason \"" + stop + "\"");
            if (root.TryGetProperty("stop_details", out var details) && details.ValueKind == JsonValueKind.Object)
            {
                var category = ManagerChatHttp.ReadString(details, "category");
                var explanation = ManagerChatHttp.ReadString(details, "explanation");
                if (category != null)
                {
                    sb.Append(" (").Append(category).Append(')');
                }
                if (!string.IsNullOrWhiteSpace(explanation))
                {
                    sb.Append(": ").Append(explanation.Trim());
                }
            }
            return sb.ToString();
        }
    }

    /// Google generateContent. Roles are user/model; images are inline_data
    /// parts at MEDIA_RESOLUTION_ULTRA_HIGH (the highest vision budget, per
    /// part only); thought parts (thought: true) are captured as provider
    /// reasoning and excluded from the reply text.
    public sealed class GeminiManagerClient : IManagerChatClient
    {
        private readonly string _apiKey;
        private readonly ManagerDefinition _definition;

        public string Model => _definition.Model;

        public GeminiManagerClient(string apiKey, ManagerDefinition definition)
        {
            _apiKey = apiKey;
            _definition = definition;
        }

        public async Task<ManagerChatReply> CompleteAsync(
            string systemPrompt, IReadOnlyList<ManagerChatMessage> history, CancellationToken ct)
        {
            var url = "https://generativelanguage.googleapis.com/v1beta/models/"
                + Uri.EscapeDataString(Model)
                + ":generateContent?key=" + Uri.EscapeDataString(_apiKey);
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            return await ManagerChatHttp.SendAsync(
                request,
                $"Gemini ({Model})",
                _definition,
                (writer, load, redacted) => WritePayloadAsync(writer, systemPrompt, history, load, redacted),
                (body, root) => Parse(root),
                ct);
        }

        private async Task WritePayloadAsync(
            Utf8JsonWriter w, string systemPrompt, IReadOnlyList<ManagerChatMessage> history,
            Func<int, ManagerChatImage, Task<ManagerChatImagePayload>> load, bool redacted)
        {
            w.WriteStartObject();
            w.WritePropertyName("systemInstruction");
            w.WriteStartObject();
            w.WritePropertyName("parts");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("text", systemPrompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();

            w.WritePropertyName("contents");
            w.WriteStartArray();
            var ordinal = 0;
            foreach (var message in history)
            {
                w.WriteStartObject();
                w.WriteString("role", message.Role == "assistant" ? "model" : "user");
                w.WritePropertyName("parts");
                w.WriteStartArray();
                w.WriteStartObject();
                w.WriteString("text", message.Text);
                w.WriteEndObject();
                if (message.Role != "assistant")
                {
                    foreach (var image in message.Images)
                    {
                        ordinal++;
                        var payload = await load(ordinal, image);
                        w.WriteStartObject();
                        w.WritePropertyName("inline_data");
                        w.WriteStartObject();
                        w.WriteString("mime_type", payload.Mime);
                        ManagerChatHttp.WriteBase64OrPlaceholder(w, "data", payload, redacted);
                        w.WriteEndObject();
                        w.WritePropertyName("mediaResolution");
                        w.WriteStartObject();
                        w.WriteString("level", "MEDIA_RESOLUTION_ULTRA_HIGH");
                        w.WriteEndObject();
                        w.WriteEndObject();
                        await w.FlushAsync();
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WritePropertyName("generationConfig");
            w.WriteStartObject();
            w.WriteNumber("maxOutputTokens", ManagerChatHttp.MaxOutputTokens);
            w.WriteString("responseMimeType", "application/json");
            w.WritePropertyName("thinkingConfig");
            w.WriteStartObject();
            w.WriteString("thinkingLevel", "high");
            w.WriteBoolean("includeThoughts", true);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        private ManagerChatReply Parse(JsonElement root)
        {
            var text = new List<string>();
            var thoughts = new List<string>();
            if (root.TryGetProperty("candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array
                && candidates.GetArrayLength() > 0
                && candidates[0].TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("text", out var pt) || pt.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                    {
                        thoughts.Add(pt.GetString() ?? "");
                    }
                    else
                    {
                        text.Add(pt.GetString() ?? "");
                    }
                }
            }
            var usage = root.TryGetProperty("usageMetadata", out var u) ? u : default;
            return ManagerChatHttp.Partial(
                Model,
                string.Join("\n", text).Trim(),
                string.Join("\n\n", thoughts).Trim(),
                DescribeStop(root),
                usage, "promptTokenCount", "candidatesTokenCount");
        }

        // generateContent: a blocked prompt carries promptFeedback.blockReason
        // and no candidates; a candidate that ended for any reason other than
        // STOP (MAX_TOKENS, SAFETY, RECITATION, PROHIBITED_CONTENT, ...) did
        // not produce a normal answer.
        public static string? DescribeStop(JsonElement root)
        {
            if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object)
            {
                var block = ManagerChatHttp.ReadString(feedback, "blockReason");
                if (block != null)
                {
                    var message = ManagerChatHttp.ReadString(feedback, "blockReasonMessage");
                    return $"provider blocked the prompt ({block})" + (string.IsNullOrWhiteSpace(message) ? "" : $": {message}");
                }
            }
            if (!root.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
            {
                return "provider returned no candidates";
            }
            var finish = ManagerChatHttp.ReadString(candidates[0], "finishReason");
            if (finish == null || finish == "STOP")
            {
                return null;
            }
            var finishMessage = ManagerChatHttp.ReadString(candidates[0], "finishMessage");
            return $"provider finishReason \"{finish}\"" + (string.IsNullOrWhiteSpace(finishMessage) ? "" : $": {finishMessage}");
        }
    }
}
