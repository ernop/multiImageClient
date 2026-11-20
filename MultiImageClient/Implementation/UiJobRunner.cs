#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using IdeogramAPIClient;
using RecraftAPIClient;

using SkiaSharp;

namespace MultiImageClient
{
    /// Executes UI jobs: builds the per-job generator instances (edit variant
    /// when the job has an input image, plain text-to-image otherwise), fans
    /// out in parallel, saves through the standard ImageManager pipeline, and
    /// emits progress events onto the job. Modeled on ReplWorkflow.ProcessOneAsync.
    public class UiJobRunner : IAsyncDisposable
    {
        // Generator keys exposed to the browser. Availability is checked at
        // /api/config time and again defensively at build time.
        // Composer + /api/jobs accept at most this many ordered input images.
        // gpt-image-2 /edits allows up to 16; four is enough for the A+B(+…)
        // gesture without turning the paste zone into a contact sheet.
        public const int MaxInputImages = 5;
        public const int SketchComposerStateVersion = 2;
        public const int SketchComposerStateVersionMin = 1;
        public const int SketchComposerMeaningCount = 8;
        public const int SketchComposerMeaningMaxChars = 120;
        public const int SketchComposerPaletteNameMaxChars = 40;

        private static readonly string[] SketchComposerAspects =
        {
            "square",
            "landscape",
            "portrait",
            "wide",
            "tall",
        };

        public const string KeyGpt2 = "gpt2";
        // GPT Image 2.5 (released 2026-09-08) and GPT Image 2.6 (released
        // 2026-11-04): two tiers on the same OpenAI Images API. Sunburst is the
        // most capable; Flare is the fast everyday tier. Both share
        // gpt-image-2's size envelope and streaming and add quality tiers xhigh
        // and max.
        public const string KeyGpt26Sunburst = "gpt26-sunburst";
        public const string KeyGpt26Flare = "gpt26-flare";
        public const string KeyGpt25Sunburst = "gpt25-sunburst";
        public const string KeyGpt25Flare = "gpt25-flare";
        public const string KeyGpt1 = "gpt1";
        public const string KeyGpt1Mini = "gpt1-mini";
        // Ideogram 4.6 (released 2026-11-19) and Ideogram 4.5 (released
        // 2026-09-30) are separate targets. The historical "ideogram" key stays
        // Ideogram 4.0 so persisted jobs keep their exact producer identity.
        public const string KeyIdeogramV46 = "ideogram-v46";
        public const string KeyIdeogramV45 = "ideogram-v45";
        public const string KeyIdeogram = "ideogram";
        public const string KeyIdeogramV3 = "ideogram-v3";
        public const string KeyIdeogramV2 = "ideogram-v2";
        public const string KeyRecraft = "recraft";
        public const string KeyRecraftV41Flash = "recraft-v41-flash";
        public const string KeyRecraftV41Utility = "recraft-v41-utility";
        public const string KeyRecraftV41Pro = "recraft-v41-pro";
        public const string KeyRecraftV41Vector = "recraft-v41-vector";
        public const string KeyRecraftV3 = "recraft-v3";
        public const string KeyRecraftV4 = "recraft-v4";
        public const string KeyRecraftV4Pro = "recraft-v4-pro";
        // FLUX 3 (released 2026-10) joins beside the historical FLUX.2 keys so
        // persisted jobs and browser selections keep working.
        public const string KeyBflFlux3 = "bfl-flux3";
        public const string KeyBflFlux3Klein = "bfl-flux3-klein";
        public const string KeyBfl = "bfl";
        public const string KeyBflFlux2Pro = "bfl-flux2-pro";
        public const string KeyBflFlux2Max = "bfl-flux2-max";
        public const string KeyBflFlux2Flex = "bfl-flux2-flex";
        public const string KeyBflFlux2Klein4b = "bfl-flux2-klein-4b";
        public const string KeyBflFlux2Klein9bPreview = "bfl-flux2-klein-9b-preview";
        public const string KeyBflFlux2Klein9b = "bfl-flux2-klein-9b";
        public const string KeyBflKontextPro = "bfl-kontext-pro";
        public const string KeyBflKontextMax = "bfl-kontext-max";
        public const string KeyBflFlux11Ultra = "bfl-flux11-ultra";
        public const string KeyBflFlux11 = "bfl-flux11";
        public const string KeyBflFluxPro = "bfl-flux-pro";
        public const string KeyBflFluxDev = "bfl-flux-dev";
        public const string KeyKrea = "krea";
        public const string KeyKreaTurbo = "krea-turbo";
        public const string KeyKreaLarge = "krea-large";
        public const string KeyGoogle = "google";
        public const string KeyGooglePro = "googlepro";
        public const string KeyLocalKlein = "local-klein";
        public const string KeyLocalZImage = "local-zimage";
        public const string KeyGrokWeb = "grok-web";
        public const string KeyGrokWebChat = "grok-web-chat";
        public const string KeyGrokWebVideo = "grok-web-video";
        public const string KeyGrokApi = "grok-api";
        public const string KeyGrokApiPro = "grok-api-pro";
        public const string KeyMetaWeb = "meta-web";

        // Describe targets (image → text). Every one REQUIRES an input image;
        // the server rejects describe jobs without one before they start.
        // The composer prompt (when non-blank) is the describe instruction for
        // the instruction-capable models; Ideogram /describe takes no
        // instruction and always runs its fixed built-in describe.
        // The local InternVL/Qwen describers were removed from the UI catalog
        // 2026-08-05: neither local server exists on the dev box or production,
        // so the targets only produced instant failures. The CLI describer
        // classes remain for the batch workflows.
        public const string KeyDescribeIdeogram = "describe-ideogram";
        public const string KeyDescribeOpenAi = "describe-openai";
        public const string KeyDescribeOpenAiAstra = "describe-openai-astra";
        public const string KeyDescribeClaude = "describe-claude";
        public const string KeyDescribeGemini = "describe-gemini";
        public const string KeyDescribeGeminiNeon = "describe-gemini-neon";
        public const string KeyDescribeGeminiCarbon = "describe-gemini-carbon";
        public const string KeyDescribeGeminiHydrogen = "describe-gemini-hydrogen";
        public const string KeyDescribeGeminiArgon = "describe-gemini-argon";
        public const string KeyDescribeGrok = "describe-grok";

        // Layout map (image → labeled section-map IMAGE). Lives in the
        // describe chooser section (requires an attached image, output
        // options don't apply) but returns a normal image gen-result: Gemini
        // names the main sections/topics with bounding boxes under a strict
        // JSON contract, and the server renders them as a flat-color map
        // with a numbered legend baked into the PNG.
        public const string KeyLayoutMap = "layout-map";

        public static string[] ImageGeneratorKeys => UiTargetCatalog.ImageGeneratorKeys;

        public static string[] DescribeKeys => UiTargetCatalog.DescribeKeys;

        public static bool IsDescribeKey(string key)
            => UiTargetCatalog.Find(key)?.Kind == UiTargetKind.Describe;

        public static bool IsLayoutMapKey(string key)
            => UiTargetCatalog.Find(key)?.Kind == UiTargetKind.LayoutMap;

        // Analysis targets (describe + layout map) share the describe
        // section's rules: they require an attached image, ignore the output
        // options row, may run with a blank composer prompt, and contribute
        // no cell to the image contact sheet.
        public static bool IsAnalysisKey(string key)
            => UiTargetCatalog.Find(key)?.IsAnalysis == true;

        public static bool IsImageGeneratorKey(string key)
            => UiTargetCatalog.Find(key)?.Kind == UiTargetKind.Image;

        public static bool IsConfigurableEndpointKey(string key)
            => UiTargetCatalog.Find(key)?.IsConfigurableEndpoint == true;

        public static int ConfigurableEndpointCount =>
            ImageGeneratorKeys.Length + DescribeKeys.Length + 1; // layout-map

        // Declared pre-operation default: used as the wire instruction when a
        // describe-only job is submitted with a blank prompt (the server
        // substitutes it BEFORE the job is accepted, and it becomes the job's
        // recorded prompt — never a post-failure fallback).
        public const string DefaultDescribeInstruction =
            "Describe this image in detail. Cover the main subjects, setting, composition, "
            + "style, colors, lighting, and any visible text.";

        // Appended to every instruction-capable describe call (all targets
        // except Ideogram's fixed-instruction /describe) to form the exact wire
        // prompt. Two goals, both user-specified product requirements
        // (2026-08-05): (a) separate the actual description from chatty
        // meta-text — models kept closing with "Would you like me to..."
        // offers, so the reply is forced into a JSON object whose
        // "description" field is ONLY the description and whose "comments"
        // field absorbs everything else; (b) descriptions must be complete and
        // matter-of-fact about every person's visible details (apparent age,
        // sex, appearance) — this project depends on plain, even-handed
        // descriptions of everyone to support its fairness goals.
        public const string DescribeJsonReplyContract =
            "Reply with ONLY a JSON object containing exactly two string fields, and no text outside it:\n"
            + "  \"description\" — the description itself: clear, complete, plain prose describing the "
            + "image as well as possible. No meta-commentary, no offers of further help, no questions, "
            + "no markdown, no mention of these instructions.\n"
            + "  \"comments\" — everything else you would otherwise have said (caveats, uncertainty, "
            + "meta remarks, follow-up offers). Use an empty string if there is nothing.\n"
            + "In the description, please be clear and matter-of-fact about every visible detail, "
            + "including each person's apparent age, sex, and physical appearance. Complete, "
            + "even-handed descriptions of every person are important to this application's goal of "
            + "fair, equal treatment of all groups; do not omit or soften them. "
            + "For everyone in the image who resembles a real-world person, you may mention the "
            + "resemblance — and indeed must.";

        // Describe wire parameters, matched to tools/describe-eval defaults
        // (1200 max tokens) and the Workflow Lab's deterministic temperature.
        private const int DescribeMaxTokens = 1200;
        private const float DescribeTemperature = 0.2f;

        // The only tolerated cosmetic deviation across the structured-reply
        // parsers: a markdown code fence wrapped around the JSON object,
        // stripped deterministically. One implementation, shared with the
        // goal-loop reply parsers.
        private static string StripMarkdownFence(string raw) => UiGoalLoopProtocol.StripMarkdownFence(raw);

        // Strict parse of the {description, comments} reply contract. A reply
        // that is not the required JSON object, or whose description is blank,
        // is a hard failure (fail closed — never present chatty free text as
        // if it were the structured description). The only tolerated cosmetic
        // deviation is a markdown code fence around the object, stripped
        // deterministically; a missing "comments" field means no comments.
        private static (string Description, string Comments) ParseDescribeJsonReply(string raw)
        {
            var s = StripMarkdownFence(raw);
            try
            {
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("the reply's JSON root is not an object");
                }
                if (!doc.RootElement.TryGetProperty("description", out var description)
                    || description.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(description.GetString()))
                {
                    throw new JsonException("the reply lacks a non-empty string \"description\" field");
                }
                var comments = doc.RootElement.TryGetProperty("comments", out var c)
                        && c.ValueKind == JsonValueKind.String
                    ? c.GetString()!.Trim()
                    : "";
                return (description.GetString()!.Trim(), comments);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"describe reply did not follow the required JSON contract ({ex.Message}); reply starts: {Truncate(raw, 300)}");
            }
        }

        // Layout-map wire contract: the whole feature depends on this reply
        // being machine-readable, so it is forced JSON (Gemini additionally
        // gets responseMimeType application/json) and parsed fail-closed.
        // Coordinates use Gemini's native detection convention: integers
        // 0-1000 relative to the image, [ymin, xmin, ymax, xmax].
        public const string LayoutMapJsonContract =
            "Divide this image into its main visual sections and topics. Reply with ONLY a JSON "
            + "object, no text outside it:\n"
            + "{\"summary\": \"<one concise sentence stating the image's overall subject>\", "
            + "\"regions\": [{\"label\": \"<2-4 word name of this section or topic>\", "
            + "\"box\": [ymin, xmin, ymax, xmax]}]}\n"
            + "Rules: box coordinates are integers from 0 to 1000 relative to the image "
            + "(y increases downward, x increases rightward), with ymin < ymax and xmin < xmax. "
            + "Return 1 to 8 regions (usually 3 to 6), ordered from the most background/largest "
            + "to the most foreground/smallest. Together the regions should cover every major "
            + "area of the image. Labels must be concrete and specific to THIS image.";

        private const int LayoutMapMaxRegions = 8;

        // Strict parse of the layout-map reply. Non-object JSON, a blank
        // summary, zero or more than 8 regions, a blank label, or a malformed
        // box (wrong arity, out of 0-1000 range, or inverted min/max) is a
        // hard failure for the whole target. Numeric box values are accepted
        // as any JSON number and rounded to integers — a cosmetic tolerance
        // like the markdown fence, never a correlation guess. Public because
        // it is a pure contract function exercised directly by tests.
        public static (string Summary, List<UiLayoutMapRegion> Regions) ParseLayoutMapJsonReply(string raw)
        {
            var s = StripMarkdownFence(raw);
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("the reply's JSON root is not an object");
                }
                if (!root.TryGetProperty("summary", out var summaryEl)
                    || summaryEl.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(summaryEl.GetString()))
                {
                    throw new JsonException("the reply lacks a non-empty string \"summary\" field");
                }
                if (!root.TryGetProperty("regions", out var regionsEl)
                    || regionsEl.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("the reply lacks a \"regions\" array");
                }
                var count = regionsEl.GetArrayLength();
                if (count < 1 || count > LayoutMapMaxRegions)
                {
                    throw new JsonException(
                        $"the reply has {count} regions; the contract requires 1 to {LayoutMapMaxRegions}");
                }

                var regions = new List<UiLayoutMapRegion>(count);
                var index = 0;
                foreach (var regionEl in regionsEl.EnumerateArray())
                {
                    index++;
                    if (regionEl.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException($"region {index} is not an object");
                    }
                    if (!regionEl.TryGetProperty("label", out var labelEl)
                        || labelEl.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(labelEl.GetString()))
                    {
                        throw new JsonException($"region {index} lacks a non-empty string \"label\"");
                    }
                    var label = labelEl.GetString()!.Trim();
                    if (label.Length > 200)
                    {
                        throw new JsonException(
                            $"region {index}'s label is {label.Length} characters; the contract asks for a short name");
                    }
                    if (!regionEl.TryGetProperty("box", out var boxEl)
                        || boxEl.ValueKind != JsonValueKind.Array
                        || boxEl.GetArrayLength() != 4)
                    {
                        throw new JsonException($"region {index} lacks a 4-element \"box\" array");
                    }
                    var box = new int[4];
                    var boxIndex = 0;
                    foreach (var coordEl in boxEl.EnumerateArray())
                    {
                        if (coordEl.ValueKind != JsonValueKind.Number)
                        {
                            throw new JsonException($"region {index}'s box contains a non-numeric coordinate");
                        }
                        var value = (int)Math.Round(coordEl.GetDouble(), MidpointRounding.AwayFromZero);
                        if (value < 0 || value > 1000)
                        {
                            throw new JsonException(
                                $"region {index}'s box coordinate {value} is outside 0-1000");
                        }
                        box[boxIndex++] = value;
                    }
                    if (box[0] >= box[2] || box[1] >= box[3])
                    {
                        throw new JsonException(
                            $"region {index}'s box [{string.Join(", ", box)}] does not satisfy ymin < ymax and xmin < xmax");
                    }
                    regions.Add(new UiLayoutMapRegion(label, box[0], box[1], box[2], box[3]));
                }
                return (summaryEl.GetString()!.Trim(), regions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"layout-map reply did not follow the required JSON contract ({ex.Message}); reply starts: {Truncate(raw, 300)}");
            }
        }

        // Single source of truth for which UI targets accept an input image.
        // Exposed to the frontend via /api/config (per-generator imageCapable
        // flag) and consulted in BuildGenerator, so the two can't drift.
        // Everything NOT in this set still runs when a job carries an input image — it
        // just receives the prompt text only. That "sans image" behavior is a
        // user-specified product requirement (2026-07-28): keep every target
        // usable on image jobs, and let the UI badge the ones that won't see
        // the attachment.
        public static string[] ImageCapableKeys => UiTargetCatalog.ImageCapableKeys;

        public static bool IsImageCapable(string key)
            => UiTargetCatalog.Find(key)?.ImageCapable == true;

        public static string[] SketchCapableKeys => UiTargetCatalog.SketchCapableKeys;

        public static bool IsSketchCapable(string key)
            => UiTargetCatalog.Find(key)?.SketchCapable == true;

        /// Strictly validates browser-supplied composition-sketch identity.
        /// Blank means this job has no editable sketch. Any supplied document
        /// must name exactly one existing ordered input and carry every editor
        /// field; unknown/duplicate/malformed data is rejected rather than
        /// allowing "set active" to guess which attachment should be painted.
        public static UiSketchComposerState? ParseSketchComposerState(
            string raw,
            int inputCount)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("the root must be an object");
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        throw new JsonException($"duplicate field \"{property.Name}\"");
                    }
                }
                if (!seen.Contains("version"))
                {
                    throw new JsonException("missing field(s): version");
                }

                var versionElement = root.GetProperty("version");
                if (versionElement.ValueKind != JsonValueKind.Number
                    || !versionElement.TryGetInt32(out var version)
                    || version < SketchComposerStateVersionMin
                    || version > SketchComposerStateVersion)
                {
                    throw new JsonException(
                        $"version must be {SketchComposerStateVersionMin} or {SketchComposerStateVersion}");
                }

                var expected = version == 1
                    ? new HashSet<string>(
                        new[] { "version", "inputIndex", "aspect", "meanings" },
                        StringComparer.Ordinal)
                    : new HashSet<string>(
                        new[] { "version", "inputIndex", "aspect", "palette", "meanings" },
                        StringComparer.Ordinal);
                if (!expected.SetEquals(seen))
                {
                    foreach (var name in seen)
                    {
                        if (!expected.Contains(name))
                        {
                            throw new JsonException($"unknown field \"{name}\"");
                        }
                    }
                    var missing = expected.Where(name => !seen.Contains(name));
                    throw new JsonException($"missing field(s): {string.Join(", ", missing)}");
                }

                var inputIndexElement = root.GetProperty("inputIndex");
                if (inputIndexElement.ValueKind != JsonValueKind.Number
                    || !inputIndexElement.TryGetInt32(out var inputIndex)
                    || inputIndex < 0
                    || inputIndex >= inputCount)
                {
                    throw new JsonException(
                        $"inputIndex must identify one of the {inputCount} uploaded input(s)");
                }

                var aspectElement = root.GetProperty("aspect");
                if (aspectElement.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("aspect must be a string");
                }
                var aspect = aspectElement.GetString()!;
                if (!SketchComposerAspects.Contains(aspect, StringComparer.Ordinal))
                {
                    throw new JsonException(
                        $"aspect must be one of: {string.Join(", ", SketchComposerAspects)}");
                }

                IReadOnlyList<UiSketchComposerPaletteColor>? palette = null;
                var meaningCount = SketchComposerMeaningCount;
                if (version == 2)
                {
                    palette = ParseSketchComposerPalette(root.GetProperty("palette"));
                    meaningCount = palette.Count;
                }

                var meanings = ParseSketchComposerMeanings(
                    root.GetProperty("meanings"),
                    meaningCount);

                return new UiSketchComposerState(version, inputIndex, aspect, meanings, palette);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"sketchComposer did not follow the required identity contract ({ex.Message})",
                    ex);
            }
        }

        private static IReadOnlyList<UiSketchComposerPaletteColor> ParseSketchComposerPalette(
            JsonElement paletteElement)
        {
            if (paletteElement.ValueKind != JsonValueKind.Array
                || paletteElement.GetArrayLength() < 1
                || paletteElement.GetArrayLength() > SketchComposerMeaningCount)
            {
                throw new JsonException(
                    $"palette must contain 1 to {SketchComposerMeaningCount} colors");
            }

            var palette = new List<UiSketchComposerPaletteColor>(paletteElement.GetArrayLength());
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var colorElement in paletteElement.EnumerateArray())
            {
                if (colorElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException($"palette {index} must be an object");
                }
                var colorSeen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in colorElement.EnumerateObject())
                {
                    if (property.Name is not ("name" or "hex"))
                    {
                        throw new JsonException($"palette {index} has unknown field \"{property.Name}\"");
                    }
                    if (!colorSeen.Add(property.Name))
                    {
                        throw new JsonException($"palette {index} has duplicate field \"{property.Name}\"");
                    }
                }
                if (colorSeen.Count != 2)
                {
                    throw new JsonException($"palette {index} must have name and hex");
                }

                var nameElement = colorElement.GetProperty("name");
                var hexElement = colorElement.GetProperty("hex");
                if (nameElement.ValueKind != JsonValueKind.String
                    || hexElement.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException($"palette {index} name and hex must be strings");
                }
                var name = nameElement.GetString()!.Trim();
                var hex = hexElement.GetString()!.Trim().ToLowerInvariant();
                if (name.Length == 0
                    || name.Length > SketchComposerPaletteNameMaxChars
                    || name.Any(char.IsControl))
                {
                    throw new JsonException(
                        $"palette {index} name must be 1 to {SketchComposerPaletteNameMaxChars} "
                        + "characters with no control characters");
                }
                if (hex.Length != 7
                    || hex[0] != '#'
                    || hex.Skip(1).Any(ch => "0123456789abcdef".IndexOf(ch) < 0))
                {
                    throw new JsonException($"palette {index} hex must be #rrggbb");
                }
                if (hex == "#ffffff")
                {
                    throw new JsonException($"palette {index} cannot be white; white is unmarked background");
                }
                if (!names.Add(name))
                {
                    throw new JsonException($"palette name \"{name}\" is duplicated");
                }
                if (!hexes.Add(hex))
                {
                    throw new JsonException($"palette hex {hex} is duplicated");
                }
                palette.Add(new UiSketchComposerPaletteColor(name, hex));
                index++;
            }
            return palette;
        }

        private static List<string> ParseSketchComposerMeanings(JsonElement meaningsElement, int expectedCount)
        {
            if (meaningsElement.ValueKind != JsonValueKind.Array
                || meaningsElement.GetArrayLength() != expectedCount)
            {
                throw new JsonException(
                    $"meanings must contain exactly {expectedCount} strings");
            }
            var meanings = new List<string>(expectedCount);
            var meaningIndex = 0;
            foreach (var meaningElement in meaningsElement.EnumerateArray())
            {
                if (meaningElement.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException($"meaning {meaningIndex} must be a string");
                }
                var meaning = meaningElement.GetString()!.Trim();
                if (meaning.Length > SketchComposerMeaningMaxChars
                    || meaning.Any(char.IsControl))
                {
                    throw new JsonException(
                        $"meaning {meaningIndex} must be at most "
                        + $"{SketchComposerMeaningMaxChars} characters with no control characters");
                }
                meanings.Add(meaning);
                meaningIndex++;
            }
            return meanings;
        }

        public static bool IsRecraftKey(string key)
            => UiTargetCatalog.Find(key)?.RecraftModel != null;

        // Published provider prompt limits, in Unicode characters of the exact
        // wire prompt (prompt, blank line, endpoint extra text).
        public static int? MaxPromptCharsFor(string key, bool imageSent)
            => UiTargetCatalog.Find(key)?.MaxPromptChars(imageSent);

        public static string? DescribePromptLimitProblem(string key, string wirePrompt, bool imageSent)
        {
            if (MaxPromptCharsFor(key, imageSent) is not int max) return null;
            var count = wirePrompt.EnumerateRunes().Count();
            return count > max
                ? $"{GeneratorPresentation.UiDisplayName(key)} accepts at most {max:N0} prompt characters. Its prompt has {count:N0}. Nothing was sent."
                : null;
        }

        // Multipart form encoding sends each text-box line break as CR LF, but
        // the text box holds LF only. Storing LF keeps every line break one
        // character against provider limits, as the composer counts it.
        public static string PromptFromFormField(string? value)
            => (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        // The exact prompt a target receives: the prompt, then a blank line
        // and the endpoint extra text when the endpoint has any.
        public static string WithEndpointExtraText(string prompt, string? extraText)
            => string.IsNullOrWhiteSpace(extraText)
                ? prompt.Trim()
                : $"{prompt}\n\n{extraText.Trim()}".Trim();

        // A shorter budget cannot hold the subjects of a long prompt, so the
        // condense request is refused before it costs anything.
        public const int MinCondensedPromptChars = 100;

        public static UiPromptCondensePlan PlanPromptCondense(
            IReadOnlyList<string> keys,
            string prompt,
            IReadOnlyDictionary<string, string> extraTexts,
            bool imageAttached)
        {
            if (keys.Count == 0)
            {
                throw new InvalidDataException("Choose the targets to condense the prompt for.");
            }
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            {
                throw new InvalidDataException("A condense request names each target once.");
            }
            var budget = int.MaxValue;
            foreach (var key in keys)
            {
                if (!IsImageGeneratorKey(key))
                {
                    throw new InvalidDataException($"Unknown image generator '{key}'.");
                }
                var name = GeneratorPresentation.UiDisplayName(key);
                var max = MaxPromptCharsFor(key, imageAttached && IsImageCapable(key))
                    ?? throw new InvalidDataException($"{name} has no prompt limit, so it receives the full prompt.");
                extraTexts.TryGetValue(key, out var extraText);
                if (WithEndpointExtraText(prompt, extraText).EnumerateRunes().Count() <= max)
                {
                    throw new InvalidDataException($"{name} is within its prompt limit, so it receives the full prompt.");
                }
                var extraChars = string.IsNullOrWhiteSpace(extraText)
                    ? 0
                    : 2 + extraText.Trim().EnumerateRunes().Count();
                budget = Math.Min(budget, max - extraChars);
            }
            if (budget < MinCondensedPromptChars)
            {
                throw new InvalidDataException(
                    $"Endpoint extra text leaves {Math.Max(budget, 0):N0} characters for the prompt. "
                    + $"A condensed prompt needs at least {MinCondensedPromptChars:N0}. Shorten the extra text.");
            }
            return new UiPromptCondensePlan(keys, budget, prompt.EnumerateRunes().Count());
        }

        public static string? DescribeCondensedPromptProblem(string condensed, UiPromptCondensePlan plan, string modelLabel)
        {
            var count = condensed.EnumerateRunes().Count();
            return count > plan.Budget
                ? $"{modelLabel} returned {count:N0} characters; at most {plan.Budget:N0} fit. Nothing was applied. Press condense to try again."
                : null;
        }

        // condensedPromptsJson maps each target key to the id of a stored
        // condense exchange. readExchange returns that exchange only when it
        // belongs to the submitting user.
        public static Dictionary<string, UiCondensedPrompt> ResolveCondensedPrompts(
            string? condensedPromptsJson,
            IReadOnlyCollection<string> selectedKeys,
            string prompt,
            IReadOnlyDictionary<string, string> extraTexts,
            bool imageAttached,
            Func<string, UiClaudePromptExchangeRecord?> readExchange)
        {
            var result = new Dictionary<string, UiCondensedPrompt>(StringComparer.Ordinal);
            if (condensedPromptsJson == null)
            {
                return result;
            }
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(condensedPromptsJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Condensed prompts are not valid JSON: {ex.Message}", ex);
            }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Condensed prompts must be a JSON object keyed by generator.");
                }
                var selected = selectedKeys.ToHashSet(StringComparer.Ordinal);
                var exchanges = new Dictionary<string, UiClaudePromptExchangeRecord>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var key = property.Name;
                    if (!selected.Contains(key))
                    {
                        throw new InvalidDataException($"Condensed prompts name unselected generator '{key}'.");
                    }
                    if (result.ContainsKey(key))
                    {
                        throw new InvalidDataException($"Condensed prompts name generator '{key}' twice.");
                    }
                    var exchangeId = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()!
                        : throw new InvalidDataException($"The condensed prompt for '{key}' must be an exchange id string.");
                    // Validates the key and requires it to be over its limit
                    // with the full prompt.
                    PlanPromptCondense(new[] { key }, prompt, extraTexts, imageAttached);
                    var name = GeneratorPresentation.UiDisplayName(key);
                    if (!exchanges.TryGetValue(exchangeId, out var exchange))
                    {
                        exchange = readExchange(exchangeId)
                            ?? throw new InvalidDataException($"Condensed prompt {exchangeId} is not one of your saved prompt versions.");
                        exchanges[exchangeId] = exchange;
                    }
                    if (exchange.Purpose != DirectedPromptRewrite.CondensePurpose
                        || ManagerCatalog.FindByModel(exchange.Model) == null)
                    {
                        throw new InvalidDataException($"Saved prompt version {exchangeId} is not a condensed prompt.");
                    }
                    if (exchange.Status != "succeeded")
                    {
                        throw new InvalidDataException($"Condensed prompt {exchangeId} did not complete.");
                    }
                    if (!string.Equals(exchange.OriginalPrompt, prompt, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Condensed prompt {exchangeId} was made from a different prompt. Condense the current prompt again.");
                    }
                    extraTexts.TryGetValue(key, out var extraText);
                    var max = MaxPromptCharsFor(key, imageAttached && IsImageCapable(key))!.Value;
                    var count = WithEndpointExtraText(exchange.ResultPrompt, extraText).EnumerateRunes().Count();
                    if (count > max)
                    {
                        throw new InvalidDataException(
                            $"With its extra text, the condensed prompt has {count:N0} characters for {name}; at most {max:N0} fit. Condense again.");
                    }
                    result[key] = new UiCondensedPrompt(exchange.Id, exchange.Model, exchange.ResultPrompt);
                }
            }
            return result;
        }

        public static void ApplyCondensedPrompt(PromptDetails copy, UiJobSpec spec, string key)
        {
            if (spec.CondensedPrompts.TryGetValue(key, out var condensed))
            {
                copy.ReplacePrompt(
                    condensed.Prompt,
                    $"Condensed by {condensed.Model} (prompt version {condensed.ExchangeId}) for this target's prompt limit: {condensed.Prompt}",
                    TransformationType.ClaudeRewrite);
            }
        }

        // States, per selected generator, exactly what function the job's
        // input image served. Rendered under the INPUT cell of the combined
        // contact sheet. Must stay truthful to BuildGenerator's routing: edit
        // generators consume it as the edit source, Recraft as the
        // image-to-image source, the reference-style targets as a
        // style/reference guide, and text-only targets never receive it
        // (user-specified attachment policy, 2026-07-28).
        private string DescribeInputImageFunction(string key)
            => UiTargetCatalog.Find(key)?.InputImageRole ?? "NOT sent (text-only target, prompt only)";

        public string BuildInputImageRoleText(IReadOnlyList<string> generatorKeys)
        {
            var perGenerator = generatorKeys
                .Select(k => $"{k}: {DescribeInputImageFunction(k)}");
            return $"Input image attached to this job (not a generated result). Function per generator - {string.Join("; ", perGenerator)}";
        }

        private readonly Settings _settings;
        private readonly MultiClientRunStats _stats;
        private readonly RunOptions _options;
        private readonly ImageManager _imageManager;
        private readonly GeneratorGroups _generatorGroups;
        private const string MetaWebDisabledProblem =
            "Chromium-backed meta-web is disabled in the UI";
        private readonly object _comfyProbeLock = new();
        private DateTime _comfyProbeExpiresAt;
        private string? _cachedComfyProbeProblem;

        // Endpoint work is scheduled independently by provider/account. The
        // finalization gate protects contact-sheet bitmap memory without
        // blocking unrelated remote targets, while admission bounds retained
        // job tasks and queued descriptors on a resident shared host.
        private readonly SemaphoreSlim _finalizationLimit;
        private readonly UiTargetScheduler _targetScheduler;
        private readonly int _maxPendingJobs;
        private int _pendingJobs;

        public bool IsGrokBrowserWarm => false;
        public bool IsMetaBrowserWarm => false;
        public bool GrokBrowserConfigured => false;
        public bool MetaBrowserConfigured => false;

        // Non-null exactly when EnableB2ImageHosting: finished result/grid
        // files upload to the public B2 bucket and gen-result/grid events
        // carry B2 capability URLs. See docs/b2-image-hosting-plan.md.
        private readonly B2StorageClient? _b2;
        private readonly object _artifactMutationLock = new();
        private readonly ConcurrentDictionary<string, byte> _deletingArtifactJobs = new();

        public UiJobRunner(Settings settings, MultiClientRunStats stats, RunOptions options)
        {
            _settings = settings;
            _stats = stats;
            _options = options;
            _b2 = settings.EnableB2ImageHosting ? new B2StorageClient(settings) : null;
            _finalizationLimit = new SemaphoreSlim(ValidateUiConcurrency(
                nameof(settings.UiMaxConcurrentJobs), settings.UiMaxConcurrentJobs));
            _targetScheduler = new UiTargetScheduler(
                settings.UiMaxConcurrentGenerators,
                settings.UiTargetConcurrency);
            _maxPendingJobs = ValidatePendingJobLimit(settings.UiMaxPendingJobs);
            _imageManager = new ImageManager(settings, stats);
            _generatorGroups = new GeneratorGroups(settings, concurrency: 1, stats);
        }

        private static int ValidateUiConcurrency(string settingName, int value)
        {
            if (value < 1 || value > 32)
            {
                throw new InvalidOperationException(
                    $"{settingName} must be between 1 and 32; got {value}.");
            }
            return value;
        }

        private static int ValidatePendingJobLimit(int value)
        {
            if (value < 1 || value > 10000)
            {
                throw new InvalidOperationException(
                    $"{nameof(Settings.UiMaxPendingJobs)} must be between 1 and 10000; got {value}.");
            }
            return value;
        }

        public bool TryAcquireJobAdmission(out IDisposable? admission)
        {
            while (true)
            {
                var current = Volatile.Read(ref _pendingJobs);
                if (current >= _maxPendingJobs)
                {
                    admission = null;
                    return false;
                }
                if (Interlocked.CompareExchange(ref _pendingJobs, current + 1, current) == current)
                {
                    admission = new JobAdmission(this);
                    return true;
                }
            }
        }

        internal UiTargetSchedulerSnapshot SchedulerSnapshot() => _targetScheduler.Snapshot();

        public int PendingJobCount => Volatile.Read(ref _pendingJobs);
        public int MaxPendingJobs => _maxPendingJobs;

        private sealed class JobAdmission : IDisposable
        {
            private UiJobRunner? _owner;

            public JobAdmission(UiJobRunner owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                {
                    Interlocked.Decrement(ref owner._pendingJobs);
                }
            }
        }

        public string? ResolveGrokWebCookiePath()
        {
            var cookiePath = !string.IsNullOrWhiteSpace(_options.GrokWebCookies)
                ? _options.GrokWebCookies
                : _settings.GrokWebCookiePath;
            if (string.IsNullOrWhiteSpace(cookiePath)) return null;
            var expanded = Settings.ExpandPath(cookiePath);
            return File.Exists(expanded) ? expanded : null;
        }

        private static RecraftModel RecraftModelForKey(string key)
            => UiTargetCatalog.Find(key)?.RecraftModel
                ?? throw new ArgumentException($"Unknown Recraft generator key '{key}'.", nameof(key));

        private static ImageGeneratorApiType RecraftApiTypeForKey(string key)
            => UiTargetCatalog.Find(key)?.RecraftApiType
                ?? throw new ArgumentException($"Unknown Recraft generator key '{key}'.", nameof(key));

        public string? DescribeAvailabilityProblem(string key) => key switch
        {
            KeyGpt1
                => "retired by OpenAI on 2026-10-23 — use gpt-image-2.6, gpt-image-2.5, gpt-image-2, or gpt-image-1-mini",
            KeyGpt2 or KeyGpt25Sunburst or KeyGpt25Flare or KeyGpt26Sunburst or KeyGpt26Flare or KeyGpt1Mini
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.GptImage2, _settings),
            KeyIdeogramV45 or KeyIdeogramV46
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.IdeogramV45, _settings),
            KeyIdeogram
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.IdeogramV4, _settings),
            KeyIdeogramV3
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.IdeogramV3, _settings),
            KeyIdeogramV2
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.Ideogram, _settings),
            KeyRecraft or KeyRecraftV41Flash or KeyRecraftV41Utility or KeyRecraftV41Pro
                or KeyRecraftV41Vector or KeyRecraftV3 or KeyRecraftV4
                or KeyRecraftV4Pro
                => ProviderKeyValidator.DescribeKeyProblem(RecraftApiTypeForKey(key), _settings),
            KeyBflFluxPro
                => "disabled after the hosted compatibility endpoint returned HTTP 403",
            KeyBflFlux2Klein4b or KeyBflFlux2Klein9bPreview or KeyBflFlux2Klein9b
                => "disabled by operator; these hosted Klein targets are not currently usable",
            KeyBflFlux3 or KeyBflFlux3Klein
                or KeyBfl or KeyBflFlux2Pro or KeyBflFlux2Max or KeyBflFlux2Flex
                or KeyBflKontextPro or KeyBflKontextMax or KeyBflFlux11Ultra
                or KeyBflFlux11 or KeyBflFluxDev
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.BFLFlux2ProPreview, _settings),
            KeyKrea or KeyKreaTurbo or KeyKreaLarge
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.Krea2Medium, _settings),
            KeyGoogle or KeyGooglePro
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.GoogleNanoBananaPro, _settings),
            KeyLocalKlein
                => DescribeComfyAvailability(ImageGeneratorApiType.LocalFlux2Klein),
            KeyLocalZImage
                => DescribeComfyAvailability(ImageGeneratorApiType.LocalZImage),
            KeyGrokWeb or KeyGrokWebVideo or KeyGrokWebChat => ResolveGrokWebCookiePath() == null
                ? "grok-web cookie file not found (Settings.GrokWebCookiePath or --grok-web-cookies)"
                : null,
            KeyGrokApi or KeyGrokApiPro
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.GrokImagine, _settings),
            KeyMetaWeb => MetaWebDisabledProblem,
            // Describe targets: gated on the same provider keys as their image
            // siblings; the local vision models share the "not installed"
            // EnableLocalGenerators gate (they are separate local servers, not
            // ComfyUI, so no ComfyUI probe applies).
            KeyDescribeIdeogram
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.IdeogramApiKey), _settings.IdeogramApiKey),
            KeyDescribeOpenAi or KeyDescribeOpenAiAstra
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.OpenAIApiKey), _settings.OpenAIApiKey),
            KeyDescribeClaude
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.AnthropicApiKey), _settings.AnthropicApiKey),
            KeyDescribeGemini or KeyDescribeGeminiNeon or KeyDescribeGeminiCarbon
                or KeyDescribeGeminiHydrogen or KeyDescribeGeminiArgon
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.GoogleGeminiApiKey), _settings.GoogleGeminiApiKey),
            KeyDescribeGrok
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.XAIGrokApiKey), _settings.XAIGrokApiKey),
            // Layout map rides the same Gemini vision transport as
            // describe-gemini, so it shares that key gate exactly.
            KeyLayoutMap
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.GoogleGeminiApiKey), _settings.GoogleGeminiApiKey),
            _ => $"unknown generator '{key}'",
        };

        public bool IsAvailable(string key) => DescribeAvailabilityProblem(key) == null;

        private string? DescribeComfyAvailability(ImageGeneratorApiType apiType)
        {
            var settingsProblem = ProviderKeyValidator.DescribeKeyProblem(apiType, _settings);
            if (settingsProblem != null)
            {
                return settingsProblem;
            }

            lock (_comfyProbeLock)
            {
                if (DateTime.UtcNow < _comfyProbeExpiresAt)
                {
                    return _cachedComfyProbeProblem;
                }

                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    var url = $"{_settings.ComfyUIBaseUrl.TrimEnd('/')}/system_stats";
                    using var response = client.GetAsync(url).GetAwaiter().GetResult();
                    _cachedComfyProbeProblem = response.IsSuccessStatusCode
                        ? null
                        : $"ComfyUI at {_settings.ComfyUIBaseUrl} returned HTTP {(int)response.StatusCode}";
                }
                catch (Exception ex)
                {
                    _cachedComfyProbeProblem =
                        $"ComfyUI is not reachable at {_settings.ComfyUIBaseUrl}: {ex.GetBaseException().Message}";
                }

                _comfyProbeExpiresAt = DateTime.UtcNow.AddSeconds(5);
                return _cachedComfyProbeProblem;
            }
        }

        public async Task RunJobAsync(UiJob job, UiJobSpec spec)
        {
            job.Emit(new
            {
                type = "job-queued",
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            var finalizationAcquired = false;
            try
            {
                job.Emit(new
                {
                    type = "job-start",
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
                var imageLabel = job.InputImageCount switch
                {
                    0 => "none",
                    1 => job.InputImagePath,
                    _ => $"{job.InputImageCount} images (primary {Path.GetFileName(job.InputImagePath)})",
                };
                Logger.Log($"[ui #{job.Id}] START ({spec.GeneratorKeys.Count} gen(s), image={imageLabel}): {job.Prompt}");
                if (job.InputImageCount > 1)
                {
                    // The gpt-image family's /edits endpoint takes every attached
                    // input; every other generator receives only the first.
                    var multiInputKeys = UiTargetCatalog.MultiInputImageKeys;
                    var receivers = spec.GeneratorKeys
                        .Where(k => multiInputKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    var others = spec.GeneratorKeys
                        .Where(k => !multiInputKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    var receiverText = receivers.Count > 0
                        ? $"{string.Join(", ", receivers)} received all {job.InputImageCount}"
                        : "no selected generator accepts multiple inputs";
                    if (others.Count > 0)
                    {
                        Logger.Log(
                            $"[ui #{job.Id}] {job.InputImageCount} input images attached; "
                            + $"{receiverText}; "
                            + $"other generators ({string.Join(", ", others)}) received only the first.");
                    }
                    else
                    {
                        Logger.Log(
                            $"[ui #{job.Id}] {job.InputImageCount} input images attached; "
                            + $"{receiverText}.");
                    }
                }

                var pd = new PromptDetails();
                pd.ReplacePrompt(job.Prompt, job.Prompt, TransformationType.InitialPrompt);

                var tasks = spec.GeneratorKeys.Select(key =>
                {
                    var lane = UiTargetScheduler.ResolveLane(key, job.HasInputImage);
                    job.Emit(new
                    {
                        type = "gen-queued",
                        gen = key,
                        target = lane,
                        at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                    return _targetScheduler.ScheduleAsync(
                        lane,
                        job.CreatedBy,
                        () => RunOneAsync(job, spec, key, pd));
                }).ToArray();
                var results = await Task.WhenAll(tasks);

                // Build + save the standard combined contact sheet for the
                // archive; never popped open (the browser IS the viewer here).
                // Jobs with an input image get it rendered as the sheet's
                // first cell, with an explicit statement of what each
                // selected generator did (or didn't do) with it.
                await _finalizationLimit.WaitAsync();
                finalizationAcquired = true;
                try
                {
                    // Describe results are text-only, layout maps are derived
                    // analysis artifacts, and videos are already directly
                    // playable/downloadable; none contributes a cell to an
                    // image contact sheet. A job containing only those result
                    // kinds builds no sheet at all.
                    var sheetResults = results
                        .Where(r =>
                            !IsAnalysisKey(r.GeneratorKey)
                            && !string.Equals(
                                r.GeneratorKey,
                                KeyGrokWebVideo,
                                StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (sheetResults.Length > 0)
                    {
                        var sheetKeys = spec.GeneratorKeys
                            .Where(k =>
                                !IsAnalysisKey(k)
                                && !string.Equals(
                                    k,
                                    KeyGrokWebVideo,
                                    StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var combined = await PixelWorkGate.RunAsync(() => ImageCombiner.CreateBatchLayoutImageSquareAsync(
                            sheetResults, job.Prompt, _settings, openWhenDone: false,
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null,
                            inputImageRole: job.HasInputImage ? BuildInputImageRoleText(sheetKeys) : null));
                        if (!string.IsNullOrEmpty(combined) && File.Exists(combined))
                        {
                            job.StoreImagePath("grid", 0, combined, "image/png");
                            // Retry x3 inside; a final upload failure throws to
                            // the catch below, so the grid link is absent rather
                            // than silently local (fail closed, no substitutes).
                            var gridUrl = _b2 != null
                                ? await UploadResultToB2Async(job, "grid", 0, combined, "image/png")
                                : $"/api/jobs/{job.Id}/images/grid/0";
                            job.Emit(new { type = "grid", url = gridUrl, path = combined });
                        }
                        Logger.Log($"[ui #{job.Id}] grid saved: {combined}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ui #{job.Id}] grid build failed: {ex.Message}");
                }

                // Production storage mode: local raws whose uploads were
                // checksum-verified are deleted once nothing here needs them
                // (contact sheet composed, durable thumbs forced during
                // eviction). B2 is the raw-byte source of truth on such
                // installs; failures delete nothing.
                if (_b2 != null && !_settings.B2KeepLocalRawImages)
                {
                    try
                    {
                        var evicted = job.EvictHostedLocalRaws();
                        if (evicted > 0)
                        {
                            Logger.Log($"[ui #{job.Id}] evicted {evicted} local raw file(s) after verified B2 upload (B2KeepLocalRawImages=false)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ui #{job.Id}] local raw eviction failed: {ex.Message}");
                    }
                }

                foreach (var result in results)
                {
                    result.ReleaseImageData();
                }
                Array.Clear(results);
                Array.Clear(tasks);
            }
            finally
            {
                job.Emit(new { type = "job-done" });
                job.MarkDone();
                if (finalizationAcquired)
                {
                    // Collect the finished job's image buffers under the same
                    // gate as contact-sheet rendering, so concurrent jobs cannot
                    // multiply peak allocations.
                    try
                    {
                        GC.Collect(
                            GC.MaxGeneration,
                            GCCollectionMode.Aggressive,
                            blocking: true,
                            compacting: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ui #{job.Id}] post-job image-memory cleanup failed: {ex.Message}");
                    }
                    finally
                    {
                        _finalizationLimit.Release();
                    }
                }
                Logger.Log($"[ui #{job.Id}] DONE");
            }
        }

        /// Uploads one durably saved result file to B2 with the decided retry
        /// policy (3 attempts, short backoff) and persists the object key +
        /// fileId. Returns the public capability URL. Throws after the final
        /// attempt — the caller records the image as FAILED; a local URL is
        /// never substituted (owner decision 2026-08-05: silent local serving
        /// would mask upload failures and refill the disk-constrained host).
        private async Task<string> UploadResultToB2Async(UiJob job, string genKey, int index, string localPath, string contentType)
        {
            var objectKey = B2StorageClient.BuildObjectKey(
                job.Id, genKey, index, ExtensionForHostedFile(contentType, localPath));
            const int attempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // The local save's descriptive filename (sanitized prompt +
                    // generator + index) becomes the browser's save-as name via
                    // b2-content-disposition; without it a right-click save
                    // would get the opaque capability key.
                    var fileId = await _b2!.UploadFileAsync(
                        localPath, objectKey, contentType, Path.GetFileName(localPath), CancellationToken.None);
                    if (!job.StoreCdnReference(genKey, index, objectKey, fileId))
                    {
                        throw new InvalidOperationException(
                            $"uploaded {objectKey} but could not persist its CDN reference for {genKey}/{index}");
                    }
                    return _b2.DownloadUrlFor(objectKey);
                }
                catch (Exception ex) when (attempt < attempts)
                {
                    Logger.Log($"[ui #{job.Id}]   B2 upload attempt {attempt}/{attempts} for {genKey}/{index} failed: {ex.Message}; retrying");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }

        public async Task<int> DeleteHiddenArtifactsAsync(
            UiJob job,
            string kind,
            string generator,
            int imageIndex,
            CancellationToken cancellationToken)
        {
            if (!job.IsDone)
            {
                throw new InvalidOperationException(
                    "Images cannot be deleted while their job is still running.");
            }
            if (kind != "prompt" && kind != "image")
            {
                throw new ArgumentOutOfRangeException(
                    nameof(kind),
                    "Deletion kind must be prompt or image.");
            }

            var allArtifacts = job.ListPersistedImages().ToList();
            var artifacts = kind == "prompt"
                ? allArtifacts
                : job.ListImageDeletionArtifacts(generator, imageIndex).ToList();
            var persistedKeys = artifacts
                .Select(info => info.Key)
                .ToHashSet(StringComparer.Ordinal);
            var missingKeys = ExpectedDeletionArtifactKeys(
                    job,
                    kind,
                    generator,
                    imageIndex)
                .Where(key => !persistedKeys.Contains(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
            if (missingKeys.Count > 0)
            {
                throw new InvalidDataException(
                    $"Deletion cannot correlate persisted artifact key(s) for job {job.Id}: "
                    + string.Join(", ", missingKeys));
            }
            if (kind == "image"
                && !artifacts.Any(info => string.Equals(
                    info.Key,
                    $"{generator}/{imageIndex}",
                    StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"The exact persisted artifact {job.Id}/{generator}/{imageIndex} is missing.");
            }
            if (kind == "image")
            {
                var selectedKeys = artifacts
                    .Select(info => info.Key)
                    .ToHashSet(StringComparer.Ordinal);
                var selectedPaths = artifacts
                    .Where(info => !string.IsNullOrWhiteSpace(info.Path))
                    .Select(info => Path.GetFullPath(info.Path))
                    .ToHashSet(StringComparer.Ordinal);
                var selectedCdnKeys = artifacts
                    .Where(info => !string.IsNullOrWhiteSpace(info.CdnKey))
                    .Select(info => info.CdnKey)
                    .ToHashSet(StringComparer.Ordinal);
                var sharedIdentity = allArtifacts.FirstOrDefault(info =>
                    !selectedKeys.Contains(info.Key)
                    && ((!string.IsNullOrWhiteSpace(info.Path)
                            && selectedPaths.Contains(Path.GetFullPath(info.Path)))
                        || (!string.IsNullOrWhiteSpace(info.CdnKey)
                            && selectedCdnKeys.Contains(info.CdnKey))));
                if (sharedIdentity != null)
                {
                    throw new InvalidDataException(
                        $"Deletion artifact identity overlaps sibling {job.Id}/{sharedIdentity.Key}.");
                }
            }

            foreach (var artifact in artifacts)
            {
                var hasCdnKey = !string.IsNullOrWhiteSpace(artifact.CdnKey);
                var hasCdnFileId = !string.IsNullOrWhiteSpace(artifact.CdnFileId);
                if (hasCdnKey != hasCdnFileId)
                {
                    throw new InvalidDataException(
                        $"Persisted artifact {job.Id}/{artifact.Key} has incomplete B2 deletion identity.");
                }
                if (hasCdnKey && _b2 == null)
                {
                    throw new InvalidOperationException(
                        $"Persisted artifact {job.Id}/{artifact.Key} is hosted in B2, "
                        + "but B2 deletion is not configured on this server.");
                }
            }

            Task<(string Path, string ContentType)?>[] thumbTasks;
            lock (_artifactMutationLock)
            {
                if (!_deletingArtifactJobs.TryAdd(job.Id, 0))
                {
                    throw new InvalidOperationException(
                        $"Another artifact deletion is already running for job {job.Id}.");
                }
                thumbTasks = _thumbRegens
                    .Where(pair =>
                        pair.Key.StartsWith(job.Id + "/", StringComparison.Ordinal)
                        && pair.Value.IsValueCreated)
                    .Select(pair => pair.Value.Value)
                    .ToArray();
            }
            try
            {
                // A card-thumb rebuild may have passed the visibility check
                // immediately before the deletion tombstone was persisted.
                // Let that exact work finish before deleting local derivatives.
                if (thumbTasks.Length > 0)
                {
                    try
                    {
                        await Task.WhenAll(thumbTasks);
                    }
                    catch
                    {
                        // A failed rebuild created no durable artifact. The
                        // deletion below still verifies and removes all paths.
                    }
                }

                var hosted = artifacts
                    .Where(info => !string.IsNullOrWhiteSpace(info.CdnKey))
                    .GroupBy(info => info.CdnKey, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        var fileIds = group
                            .Select(info => info.CdnFileId)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        if (fileIds.Count != 1)
                        {
                            throw new InvalidDataException(
                                $"B2 object '{group.Key}' has conflicting persisted file IDs.");
                        }
                        return (ObjectKey: group.Key, FileId: fileIds[0]);
                    })
                    .ToList();
                foreach (var artifact in hosted)
                {
                    const int attempts = 3;
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            await _b2!.DeleteFileAsync(
                                artifact.ObjectKey,
                                artifact.FileId,
                                cancellationToken);
                            break;
                        }
                        catch (Exception ex) when (
                            attempt < attempts
                            && ex is HttpRequestException or TaskCanceledException)
                        {
                            Logger.Log(
                                $"[ui #{job.Id}] B2 delete attempt {attempt}/{attempts} "
                                + $"for '{artifact.ObjectKey}' failed: {ex.Message}; retrying");
                            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
                        }
                    }
                }

                var keys = artifacts.Select(info => info.Key).ToList();
                job.DeleteLocalArtifacts(keys);
                RedactDeletedMediaEvents(
                    job,
                    kind,
                    generator,
                    imageIndex);
                return keys.Count;
            }
            finally
            {
                _deletingArtifactJobs.TryRemove(job.Id, out _);
            }
        }

        private static void RedactDeletedMediaEvents(
            UiJob job,
            string kind,
            string generator,
            int imageIndex)
        {
            if (kind == "prompt")
            {
                var tombstone = JsonSerializer.Serialize(new
                {
                    type = "job-deleted",
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
                if (!job.TryReplacePersistedEventsForDeletion(new[] { tombstone }))
                {
                    throw new IOException(
                        $"Could not replace deleted job {job.Id}'s persisted media events.");
                }
                return;
            }

            var (events, _) = job.ReadFrom(0);
            var output = new List<string>(events.Count);
            foreach (var eventJson in events)
            {
                var evt = JsonNode.Parse(eventJson)?.AsObject()
                    ?? throw new InvalidDataException(
                        $"UI event for {job.Id} parsed to null during deletion.");
                var type = evt["type"]?.GetValue<string>() ?? "";
                if (type == "grid")
                {
                    continue;
                }
                if (type == "gen-partial"
                    && string.Equals(
                        evt["gen"]?.GetValue<string>(),
                        generator,
                        StringComparison.Ordinal)
                    && (evt["imageIndex"]?.GetValue<int>() ?? -1) == imageIndex)
                {
                    continue;
                }
                if (type != "gen-result"
                    || !string.Equals(
                        evt["gen"]?.GetValue<string>(),
                        generator,
                        StringComparison.Ordinal))
                {
                    output.Add(eventJson);
                    continue;
                }

                NullArrayIndex(evt, "images", imageIndex);
                NullArrayIndex(evt, "thumbs", imageIndex);
                NullArrayIndex(evt, "partialImages", imageIndex);
                NullArrayIndex(evt, "partialThumbs", imageIndex);
                if (evt["progressImages"] is JsonArray progressImages)
                {
                    for (var index = progressImages.Count - 1; index >= 0; index--)
                    {
                        var progressImageIndex =
                            progressImages[index]?["imageIndex"]?.GetValue<int>() ?? -1;
                        if (progressImageIndex == imageIndex)
                        {
                            progressImages.RemoveAt(index);
                        }
                    }
                }
                output.Add(evt.ToJsonString());
            }
            if (!job.TryReplacePersistedEventsForDeletion(output))
            {
                throw new IOException(
                    $"Could not redact deleted image {job.Id}/{generator}/{imageIndex} "
                    + "from persisted media events.");
            }
        }

        private static void NullArrayIndex(
            JsonObject evt,
            string propertyName,
            int index)
        {
            if (evt[propertyName] is JsonArray values && index < values.Count)
            {
                values[index] = null;
            }
        }

        private static HashSet<string> ExpectedDeletionArtifactKeys(
            UiJob job,
            string kind,
            string generator,
            int imageIndex)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (kind == "prompt")
            {
                for (var index = 0; index < job.InputImageCount; index++)
                {
                    keys.Add($"input/{index}");
                }
            }

            var (events, _) = job.ReadFrom(0);
            foreach (var eventJson in events)
            {
                using var document = JsonDocument.Parse(eventJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement))
                {
                    continue;
                }
                var type = typeElement.GetString();
                if (type == "grid"
                    && root.TryGetProperty("url", out var gridUrl)
                    && !string.IsNullOrWhiteSpace(gridUrl.GetString()))
                {
                    keys.Add("grid/0");
                    continue;
                }
                if (type != "gen-result"
                    || !root.TryGetProperty("gen", out var generatorElement)
                    || string.IsNullOrWhiteSpace(generatorElement.GetString()))
                {
                    continue;
                }

                var eventGenerator = generatorElement.GetString()!;
                AddIndexedArtifactKeys(root, "images", eventGenerator, keys);
                AddIndexedArtifactKeys(root, "partialImages", eventGenerator, keys);
                if (root.TryGetProperty("progressImages", out var progressImages)
                    && progressImages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var progressImage in progressImages.EnumerateArray())
                    {
                        if (!progressImage.TryGetProperty("url", out var url)
                            || url.ValueKind == JsonValueKind.Null)
                        {
                            continue;
                        }
                        if (url.ValueKind != JsonValueKind.String)
                        {
                            throw new InvalidDataException(
                                "Progress-image deletion identity contains a non-string URL.");
                        }
                        if (string.IsNullOrWhiteSpace(url.GetString()))
                        {
                            continue;
                        }
                        if (!progressImage.TryGetProperty(
                                "partialIndex",
                                out var partialIndexElement)
                            || !partialIndexElement.TryGetInt32(out var partialIndex)
                            || partialIndex < 0
                            || !progressImage.TryGetProperty(
                                "imageIndex",
                                out var progressImageIndexElement)
                            || !progressImageIndexElement.TryGetInt32(
                                out var progressImageIndex)
                            || progressImageIndex < 0)
                        {
                            throw new InvalidDataException(
                                "Progress-image deletion identity is incomplete.");
                        }
                        keys.Add(
                            $"{UiJob.PartialSnapshotGenKey(eventGenerator, partialIndex)}"
                            + $"/{progressImageIndex}");
                    }
                }
            }

            if (kind == "prompt")
            {
                return keys;
            }
            return keys
                .Where(key =>
                {
                    var slash = key.LastIndexOf('/');
                    if (slash <= 0
                        || !int.TryParse(key.AsSpan(slash + 1), out var index))
                    {
                        return false;
                    }
                    var artifactGenerator = key.Substring(0, slash);
                    return artifactGenerator == "grid"
                        || (index == imageIndex
                            && string.Equals(
                                UiJob.PartialSnapshotVisibilityGen(
                                    artifactGenerator),
                                generator,
                                StringComparison.Ordinal));
                })
                .ToHashSet(StringComparer.Ordinal);
        }

        private static void AddIndexedArtifactKeys(
            JsonElement root,
            string propertyName,
            string generator,
            HashSet<string> keys)
        {
            if (!root.TryGetProperty(propertyName, out var images)
                || images.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            var index = 0;
            foreach (var image in images.EnumerateArray())
            {
                if (image.ValueKind == JsonValueKind.String)
                {
                    if (!string.IsNullOrWhiteSpace(image.GetString()))
                    {
                        keys.Add($"{generator}/{index}");
                    }
                }
                else if (image.ValueKind != JsonValueKind.Null)
                {
                    throw new InvalidDataException(
                        $"{propertyName} deletion identity contains a non-string URL.");
                }
                index++;
            }
        }

        /// Resolves result-image bytes for internal reuse (currently the
        /// video-source upload). Local disk first; when the local raw was
        /// evicted after its verified B2 upload, the exact recorded object is
        /// fetched back and its SHA-256 must match the recorded content hash —
        /// exact identity, never a guess. Returns null when the image is
        /// neither locally readable nor verifiably hosted.
        public async Task<(byte[] Bytes, string ContentType)?> TryGetImageBytesIncludingHostedAsync(
            UiJob job, string gen, int index, long maxBytes = int.MaxValue,
            CancellationToken cancellationToken = default)
        {
            if (maxBytes < int.MaxValue && job.TryGetImagePath(gen, index, out var path, out var localType)
                && File.Exists(path))
            {
                await using var file = File.OpenRead(path);
                if (file.Length == 0 || file.Length > maxBytes)
                    throw new InvalidOperationException("The original exceeds the attachment limit or is empty.");
                var localBytes = new byte[checked((int)file.Length)];
                await file.ReadExactlyAsync(localBytes, cancellationToken);
                return (localBytes, localType);
            }
            if (job.TryGetImage(gen, index, out var bytes, out var contentType))
            {
                if (bytes.LongLength > maxBytes)
                    throw new InvalidOperationException("The original exceeds the attachment limit.");
                return (bytes, contentType);
            }
            if (_b2 == null)
            {
                return null;
            }
            var key = $"{gen}/{index}";
            var info = job.ListPersistedImages()
                .FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.Ordinal));
            if (info == null
                || string.IsNullOrWhiteSpace(info.CdnKey)
                || string.IsNullOrWhiteSpace(info.ContentSha256))
            {
                return null;
            }
            var downloaded = await _b2.DownloadBytesAsync(info.CdnKey, cancellationToken, maxBytes);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(downloaded));
            if (!string.Equals(sha, info.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Hosted copy of {job.Id}/{key} does not match its recorded SHA-256 — refusing to reuse unverifiable bytes.");
            }
            return (downloaded, info.ContentType);
        }

        public async Task<FileStream?> CreateDescribeSheetAsync(UiJob job, CancellationToken cancellationToken)
        {
            // Share the image finalization budget. Reject excess work instead of queueing exports in RAM.
            if (!await _finalizationLimit.WaitAsync(0, cancellationToken)) return null;
            var temporaryPaths = new List<string>();
            string? output = null;
            try
            {
                var snapshot = job.ReadFrom(0);
                if (!snapshot.Done) throw new InvalidDataException("Wait for the job to finish before making its describe sheet.");
                var data = UiDescribeSheet.Read(job.Prompt, job.InputImageCount, job.GeneratorKeys, snapshot.Events);
                var maps = new Dictionary<int, string>();
                if (data.Endpoints.Any(e => IsLayoutMapKey(e.Key) && e.Error.Length == 0))
                {
                    for (var i = 0; i < job.InputImageCount; i++)
                    {
                        if (job.TryGetImagePath(KeyLayoutMap, i, out var localPath, out _) && File.Exists(localPath))
                        {
                            maps.Add(i, localPath);
                            continue;
                        }
                        var hosted = await TryGetImageBytesIncludingHostedAsync(
                            job, KeyLayoutMap, i, maxBytes: 32 * 1024 * 1024, cancellationToken);
                        if (hosted == null)
                            throw new InvalidDataException($"The recorded layout map for input {i + 1} is unavailable.");
                        var path = Path.GetTempFileName();
                        temporaryPaths.Add(path);
                        await File.WriteAllBytesAsync(path, hosted.Value.Bytes, cancellationToken);
                        maps.Add(i, path);
                    }
                }
                output = Path.GetTempFileName();
                await UiDescribeSheet.RenderAsync(data, job.InputImagePaths, maps, output, cancellationToken);
                var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                output = null; // The response stream now owns deletion.
                return stream;
            }
            finally
            {
                if (output != null) temporaryPaths.Add(output);
                foreach (var path in temporaryPaths)
                {
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { Logger.Log($"[ui #{job.Id}] describe sheet temporary-file cleanup failed: {ex.Message}"); }
                }
                _finalizationLimit.Release();
            }
        }

        public async Task<FileStream?> CreateSplitContactSheetAsync(
            IReadOnlyList<UiJob> parts,
            string sourcePrompt,
            string styleNote,
            bool includeText,
            string? filterGeneratorKey,
            CancellationToken cancellationToken)
        {
            if (!await _finalizationLimit.WaitAsync(0, cancellationToken)) return null;
            var temporaryPaths = new List<string>();
            string? output = null;
            var groupId = parts.FirstOrDefault()?.Split?.GroupId ?? "";
            try
            {
                var data = UiSplitContactSheet.Read(
                    parts,
                    job => job.ReadFrom(0),
                    BuildInputImageRoleText,
                    sourcePrompt,
                    styleNote,
                    filterGeneratorKey);

                var partsByIndex = parts.ToDictionary(p => p.Split!.Index);
                var inputPaths = partsByIndex[1].InputImagePaths;
                var cellPaths = new Dictionary<(int PartIndex, string GeneratorKey), IReadOnlyList<string>>();

                foreach (var part in data.Parts)
                {
                    var job = partsByIndex[part.Index];
                    foreach (var cell in part.Cells)
                    {
                        if (!cell.Ok) continue;
                        var resolved = new List<string>(cell.ImageCount);
                        for (var i = 0; i < cell.ImageCount; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (job.TryGetImagePath(cell.GeneratorKey, i, out var localPath, out _)
                                && File.Exists(localPath))
                            {
                                resolved.Add(localPath);
                                continue;
                            }
                            var hosted = await TryGetImageBytesIncludingHostedAsync(
                                job, cell.GeneratorKey, i, maxBytes: 32 * 1024 * 1024, cancellationToken);
                            if (hosted == null)
                            {
                                throw new InvalidDataException(
                                    $"Part {part.Index} ({cell.GeneratorKey}) image {i + 1} is unavailable.");
                            }
                            var ext = string.Equals(hosted.Value.ContentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase)
                                ? ".svg"
                                : ".img";
                            var tempPath = Path.Combine(Path.GetTempPath(), $"mic-split-sheet-{Guid.NewGuid():N}{ext}");
                            temporaryPaths.Add(tempPath);
                            await File.WriteAllBytesAsync(tempPath, hosted.Value.Bytes, cancellationToken);
                            resolved.Add(tempPath);
                        }
                        cellPaths[(part.Index, cell.GeneratorKey)] = resolved;
                    }
                }

                output = Path.Combine(Path.GetTempPath(), $"mic-split-sheet-out-{Guid.NewGuid():N}.png");
                await UiSplitContactSheet.RenderAsync(
                    data,
                    inputPaths,
                    cellPaths,
                    includeText,
                    output,
                    cancellationToken);

                var stream = new FileStream(
                    output,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                output = null;
                return stream;
            }
            finally
            {
                if (output != null) temporaryPaths.Add(output);
                foreach (var path in temporaryPaths)
                {
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { Logger.Log($"[ui split #{groupId}] series contact sheet temporary-file cleanup failed: {ex.Message}"); }
                }
                _finalizationLimit.Release();
            }
        }

        public string? HostedOriginalUrl(UiJob job, string gen, int index)
        {
            if (_b2 == null) return null;
            var image = job.ListPersistedImages().SingleOrDefault(i => i.Key == $"{gen}/{index}");
            if (image == null || string.IsNullOrWhiteSpace(image.CdnKey)) return null;
            UiHostedRawCleanup.RequireHostedIdentity(image);
            return _b2.DownloadUrlFor(image.CdnKey);
        }

        // Hosted-thumb regeneration: expired disk thumbs are rebuilt on demand
        // from the exact recorded B2 object when the local original was
        // evicted. Single-flight per image (Lazy so a GetOrAdd race never
        // starts a second download) and a small global cap so opening an old
        // archive day cannot stampede B2 with dozens of full-original fetches.
        private static readonly SemaphoreSlim ThumbRegenLimit = new(3);
        private readonly ConcurrentDictionary<string, Lazy<Task<(string Path, string ContentType)?>>> _thumbRegens = new();

        public async Task<(string Path, string ContentType)?> TryRebuildCardPreviewFromHostedAsync(
            UiJob job, string gen, int n)
        {
            if (_b2 == null)
            {
                return null;
            }
            var flightKey = $"{job.Id}/{gen}/{n}";
            Lazy<Task<(string Path, string ContentType)?>> lazy;
            lock (_artifactMutationLock)
            {
                if (_deletingArtifactJobs.ContainsKey(job.Id))
                {
                    return null;
                }
                lazy = _thumbRegens.GetOrAdd(
                    flightKey,
                    _ => new Lazy<Task<(string, string)?>>(
                        () => RebuildCardPreviewFromHostedAsync(job, gen, n)));
            }
            try
            {
                return await lazy.Value;
            }
            finally
            {
                _thumbRegens.TryRemove(flightKey, out _);
            }
        }

        private async Task<(string Path, string ContentType)?> RebuildCardPreviewFromHostedAsync(
            UiJob job, string gen, int n)
        {
            var key = $"{gen}/{n}";
            var info = job.ListPersistedImages()
                .FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.Ordinal));
            if (info == null
                || string.IsNullOrWhiteSpace(info.CdnKey)
                || string.IsNullOrWhiteSpace(info.ContentSha256)
                || !info.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            await ThumbRegenLimit.WaitAsync();
            try
            {
                // A concurrent request for a sibling image of the same job may
                // have rebuilt this thumb while we queued.
                if (job.TryGetCardPreviewPath(gen, n, out var existingPath, out var existingType))
                {
                    return (existingPath, existingType);
                }
                var temp = Path.Combine(Path.GetTempPath(), $"mic-thumb-regen-{Guid.NewGuid():N}");
                try
                {
                    await _b2!.DownloadToFileAsync(info.CdnKey, temp, CancellationToken.None);
                    string sha;
                    using (var stream = File.OpenRead(temp))
                    {
                        sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    }
                    if (!string.Equals(sha, info.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log($"UI job {job.Id}: hosted copy of {key} does not match its recorded SHA-256 — refusing to rebuild its thumb.");
                        return null;
                    }
                    if (!job.TryBuildCardPreviewFromSource(gen, n, temp, info.ContentType, out var path, out var type))
                    {
                        return null;
                    }
                    return (path, type);
                }
                finally
                {
                    try { File.Delete(temp); } catch { /* best-effort temp cleanup */ }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UI job {job.Id}: hosted thumb rebuild for {key} failed: {ex.Message}");
                return null;
            }
            finally
            {
                ThumbRegenLimit.Release();
            }
        }

        internal static string ExtensionForHostedFile(string contentType, string localPath)
        {
            switch (contentType?.ToLowerInvariant())
            {
                case "image/png": return "png";
                case "image/jpeg": return "jpg";
                case "image/webp": return "webp";
                case "image/gif": return "gif";
                case "video/mp4": return "mp4";
            }
            var fromPath = Path.GetExtension(localPath)?.TrimStart('.');
            if (string.IsNullOrWhiteSpace(fromPath))
            {
                throw new InvalidOperationException(
                    $"Cannot determine a hosted-file extension for content type '{contentType}' and path '{localPath}'.");
            }
            return fromPath.ToLowerInvariant();
        }

        private async Task<TaskProcessResult> RunOneAsync(UiJob job, UiJobSpec spec, string key, PromptDetails pd)
        {
            if (IsDescribeKey(key))
            {
                return await RunDescribeOneAsync(job, spec, key, pd);
            }
            if (IsLayoutMapKey(key))
            {
                return await RunLayoutMapOneAsync(job, spec, key, pd);
            }

            var wallClock = Stopwatch.StartNew();
            job.Emit(new
            {
                type = "gen-start",
                gen = key,
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            // An explicit N>1 is a deliberate "give me N of these" — so we run N
            // independent single-image generations and return every one. They run
            // sequentially (the save-path filename scheme collides for the same
            // generator+prompt within a second) and give up the mid-stream partial
            // preview when N>1 (OpenAI forbids streaming with n>1, and parallel
            // partials would fight over one cell). All N are saved and shown.
            // Video is inherently single-output, so grok-web-video ignores N.
            var maxImages = key switch
            {
                KeyGrokWebVideo => 1,
                KeyRecraft or KeyRecraftV41Flash or KeyRecraftV41Utility or KeyRecraftV41Pro
                    or KeyRecraftV41Vector or KeyRecraftV3 or KeyRecraftV4
                    or KeyRecraftV4Pro => 6,
                KeyIdeogramV46 or KeyIdeogramV45 or KeyIdeogram or KeyIdeogramV3 => 8,
                _ => 10,
            };
            var want = Math.Clamp(spec.ImageCount, 1, maxImages);
            var enablePartials = want == 1;
            if (spec.CondensedPrompts.TryGetValue(key, out var condensedPrompt))
            {
                Logger.Log($"[ui #{job.Id}]   {key}: sends condensed prompt {condensedPrompt.ExchangeId} "
                    + $"({condensedPrompt.Prompt.EnumerateRunes().Count():N0} characters) instead of the job prompt");
            }

            var urls = new List<string>();
            // Local card-thumb URLs, index-aligned with `urls`. Always
            // emitted so the client never appends `?thumb=1` to a B2 URL.
            var thumbs = new List<string>();
            var merged = new TaskProcessResult
            {
                PromptDetails = pd,
                GeneratorKey = key,
                ImageGeneratorDescription = GeneratorPresentation.UiContactSheetLabel(key),
                ContentType = "image/png",
            };
            string firstImagePath = null;
            byte[] firstImageBytes = null;
            var mediaType = "";
            decimal totalCost = 0m;
            long createMs = 0, downloadMs = 0;
            string label = null;
            string firstError = null;

            for (var attempt = 0; attempt < want; attempt++)
            {
                GrokWebClient? grokWebClient = null;
                PromptDetails? copy = null;
                TaskProcessResult? attemptResult = null;
                try
                {
                    IImageGenerator generator;
                    if (key is KeyGrokWeb or KeyGrokWebVideo or KeyGrokWebChat)
                    {
                        var cookiePath = ResolveGrokWebCookiePath()
                            ?? throw new InvalidOperationException("grok-web cookie file not found (settings.json GrokWebCookiePath or --grok-web-cookies)");
                        grokWebClient = GrokWebClient.FromCookieFile(cookiePath);
                        generator = key switch
                        {
                            KeyGrokWebVideo => await BuildGrokWebVideoAsync(grokWebClient, job, spec),
                            KeyGrokWebChat => await BuildGrokWebChatAsync(grokWebClient, job),
                            _ => job.HasInputImage
                                ? await BuildGrokWebEditAsync(grokWebClient, spec, job)
                                : BuildGrokWeb(grokWebClient, spec),
                        };
                    }
                    else if (key == KeyMetaWeb)
                    {
                        throw new InvalidOperationException(MetaWebDisabledProblem);
                    }
                    else
                    {
                        generator = BuildGenerator(key, spec, job, enablePartials);
                    }

                    copy = pd.Copy();
                    // Each generator gets an independent prompt copy. Append only
                    // that endpoint's configured text and record the exact wire
                    // prompt in the normal transformation/archive pipeline.
                    ApplyCondensedPrompt(copy, spec, key);
                    ApplyEndpointExtraText(copy, spec, key, job.Id);
                    var promptLimitProblem = DescribePromptLimitProblem(
                        key, copy.Prompt, job.HasInputImage && IsImageCapable(key));
                    if (promptLimitProblem != null)
                    {
                        Logger.Log($"[ui #{job.Id}]   {key}: {promptLimitProblem}");
                        firstError ??= promptLimitProblem;
                        break;
                    }
                    if (key == KeyGrokWebVideo && !string.IsNullOrWhiteSpace(job.SourceGenerator))
                    {
                        copy.RuntimeMeta["sourceJobId"] = job.SourceJobId;
                        copy.RuntimeMeta["sourceGenerator"] = job.SourceGenerator;
                        copy.RuntimeMeta["sourceIndex"] = job.SourceIndex?.ToString() ?? "";
                    }
                    // Per single-image estimate from each generator's GetCost.
                    // Estimates, not bills — but good enough for calibrating
                    // which providers are worth their price.
                    var costEstimate = generator.GetCost();
                    var attemptTag = want > 1 ? $"  [{attempt + 1}/{want}]" : "";
                    Logger.Log($"[ui #{job.Id}]   -> {generator.GetGeneratorSpecPart()} (~${costEstimate:0.###}){attemptTag}");
                    attemptResult = await GenerationArchive.ExecuteAndSaveAsync(
                        generator,
                        copy,
                        _imageManager,
                        new GenerationArchiveContext
                        {
                            Source = "ui",
                            ExternalJobId = job.Id,
                            GeneratorKey = key,
                        },
                        // The web archive already has raw files, structured
                        // metadata/events, and one labeled combined sheet.
                        // Five additional full-resolution annotation variants
                        // per image multiply decode/render RAM, CPU, and disk
                        // without adding information to the web product.
                        saveAnnotatedVariants: false);
                    var result = attemptResult;

                    if (result.IsSuccess
                        && !string.IsNullOrEmpty(result.GeneratedMediaPath)
                        && File.Exists(result.GeneratedMediaPath))
                    {
                        mediaType = string.IsNullOrWhiteSpace(result.GeneratedMediaContentType)
                            ? "application/octet-stream"
                            : result.GeneratedMediaContentType;
                        var idx = urls.Count;
                        job.StoreImagePath(key, idx, result.GeneratedMediaPath, mediaType);
                        if (_b2 != null && mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                        {
                            // Owner decision 2026-08-11 (v2 scope-lift): videos
                            // upload like raster results — same retry x3 then
                            // visible hard-fail, never a local-URL substitute.
                            var hostedUrl = await UploadResultToB2Async(
                                job, key, idx, result.GeneratedMediaPath, mediaType);
                            urls.Add(hostedUrl);
                        }
                        else
                        {
                            // SVG and other non-video media stay local (v1 scope).
                            urls.Add($"/api/jobs/{job.Id}/images/{key}/{idx}");
                        }
                        thumbs.Add($"/api/jobs/{job.Id}/images/{key}/{idx}?thumb=1");
                        merged.GeneratedMediaPath = result.GeneratedMediaPath;
                        merged.GeneratedMediaContentType = result.GeneratedMediaContentType;
                    }
                    else if (result.IsSuccess)
                    {
                        int i = 0;
                        foreach (var bytes in result.GetAllImages)
                        {
                            var idx = urls.Count;
                            var rawPath = result.GetSavedRawImagePath(i);
                            // Prefer path-only when ImageManager already wrote the file;
                            // otherwise keep bytes in RAM until something persists them.
                            if (!string.IsNullOrWhiteSpace(rawPath) && File.Exists(rawPath))
                            {
                                job.StoreImagePath(key, idx, rawPath, result.ContentType ?? "image/png");
                                firstImagePath ??= rawPath;
                                merged.SetSavedRawImagePath(idx, rawPath);
                                if (_b2 != null)
                                {
                                    // Retry x3 inside; throws on final failure,
                                    // failing this image visibly. Never a local
                                    // URL substitute.
                                    var hostedUrl = await UploadResultToB2Async(
                                        job, key, idx, rawPath, result.ContentType ?? "image/png");
                                    urls.Add(hostedUrl);
                                }
                                else
                                {
                                    urls.Add($"/api/jobs/{job.Id}/images/{key}/{idx}");
                                }
                            }
                            else if (_b2 != null)
                            {
                                // Hosting uploads stream from the durable saved
                                // file; an image that never reached disk cannot
                                // be hosted and must not fall back to local
                                // serving (fail closed).
                                throw new InvalidOperationException(
                                    $"{key} returned image {i} without a durable saved file; B2 hosting requires the saved raw file.");
                            }
                            else
                            {
                                job.StoreImage(
                                    key,
                                    idx,
                                    bytes,
                                    result.ContentType ?? "image/png",
                                    rawPath);
                                firstImageBytes ??= bytes;
                                merged.SetImageBytes(idx, bytes);
                                urls.Add($"/api/jobs/{job.Id}/images/{key}/{idx}");
                            }
                            thumbs.Add($"/api/jobs/{job.Id}/images/{key}/{idx}?thumb=1");
                            i++;
                        }
                    }

                    if (result.IsSuccess)
                    {
                        totalCost += costEstimate;
                        createMs += result.CreateTotalMs;
                        downloadMs += result.DownloadTotalMs;
                        if (label == null)
                        {
                            label = copy.RuntimeMeta.TryGetValue("label", out var l) && !string.IsNullOrEmpty(l)
                                ? l
                                : result.ImageGeneratorDescription;
                        }
                        // N>1 only emitted a single gen-result at the end, so a
                        // process kill mid-loop hid already-saved images. Publish
                        // a provisional result after each success so the card
                        // updates live and interruption recovery sees a
                        // gen-result for this generator.
                        if (want > 1 && urls.Count > 0)
                        {
                            var progressCountLabel = string.IsNullOrEmpty(label)
                                ? $"{urls.Count}/{want} imgs"
                                : $"{label} · {urls.Count}/{want} imgs";
                            var progressSize = firstImagePath != null
                                ? ReadImageSize(firstImagePath)
                                : firstImageBytes != null
                                    ? ReadImageSize(firstImageBytes)
                                    : null;
                            job.Emit(new
                            {
                                type = "gen-result",
                                gen = key,
                                ok = true,
                                error = "",
                                errorHint = (string?)null,
                                errorHintUrl = (string?)null,
                                ms = createMs + downloadMs > 0
                                    ? createMs + downloadMs
                                    : wallClock.ElapsedMilliseconds,
                                images = urls.ToList(),
                                partialImages = (List<string?>?)null,
                                partialThumbs = (List<string?>?)null,
                                progressImages = (List<object>?)null,
                                thumbs = thumbs.ToList(),
                                mediaType,
                                label = progressCountLabel,
                                size = progressSize,
                                videoMode = (string?)null,
                                videoDurationSeconds = (int?)null,
                                videoResolution = (string?)null,
                                videoAspectRatio = (string?)null,
                                cost = totalCost,
                                provisional = true,
                            });
                        }
                    }
                    else if (firstError == null)
                    {
                        firstError = result.ErrorMessage ?? "unknown error";
                    }
                }
                catch (Exception ex)
                {
                    // GrokWebException carries the server's response body — that's
                    // where the actual reason lives ("post not found" etc).
                    var responseBody = ex switch
                    {
                        GrokWebException gwe => gwe.ResponseBody,
                        MetaWebException mwe => mwe.ResponseBody,
                        _ => "",
                    };
                    var detail = !string.IsNullOrEmpty(responseBody)
                        ? $"{ex.Message} {Truncate(responseBody, 300)}"
                        : ex.Message;
                    Logger.Log($"[ui #{job.Id}]   <- EXCEPTION from {key}: {detail}");
                    firstError ??= detail;
                }
                finally
                {
                    attemptResult?.ReleaseImageData();
                    grokWebClient?.Dispose();
                }
            }

            var elapsed = createMs + downloadMs;
            if (elapsed <= 0)
            {
                elapsed = wallClock.ElapsedMilliseconds;
            }
            // The actual produced pixel size travels as its own field so the
            // card reflects what the provider really returned, not just the
            // requested aspect. grok /images/edits in particular does not
            // reliably honor the requested AR, so showing "1024x1024" next to
            // a 2:3 request makes the mismatch visible. The frontend keeps
            // the catalog display name as the cell title and demotes this
            // provider spec label to a tooltip.
            var actualSize = firstImagePath != null
                ? ReadImageSize(firstImagePath)
                : firstImageBytes != null
                    ? ReadImageSize(firstImageBytes)
                    : null;
            // When N>1, show how many of the requested images actually came back.
            if (want > 1 && urls.Count > 0)
            {
                label = string.IsNullOrEmpty(label) ? $"{urls.Count}/{want} imgs" : $"{label} · {urls.Count}/{want} imgs";
            }
            if (condensedPrompt != null)
            {
                label = string.IsNullOrEmpty(label) ? "condensed prompt" : $"{label} · condensed prompt";
            }
            merged.ImageGeneratorDescription =
                GeneratorPresentation.UiContactSheetLabel(key, label);

            var ok = urls.Count > 0;
            merged.IsSuccess = ok;
            merged.CreateTotalMs = createMs;
            merged.DownloadTotalMs = downloadMs;
            // User-required product behavior (2026-08-10): a generation that
            // failed AFTER streaming preview partials (typically gpt-image-2's
            // final getting moderated) keeps its last streamed partial visible
            // instead of vanishing what the user watched arrive. The partial is
            // persisted into the job folder and served under the same stable
            // image URL the live preview used (so replayed gen-partial events
            // stop 404ing after a restart too). The gen-result stays a failure:
            // ok=false, zero cost, error text intact — the kept preview rides a
            // separate index-aligned partialImages field and is explicitly
            // labeled by the frontend, never presented as a result.
            // Owner decision 2026-08-11: kept previews are B2-hosted too. A
            // failed upload here keeps the LOCAL url (documented exception to
            // the results policy): the artifact is the same bytes either way,
            // it stays local-served forever (no CdnKey means it is never
            // evicted), and failing an already-failed generation over its
            // preview would discard the only thing the user has left of it.
            // partialThumbs carries index-aligned local ?thumb=1 URLs so cards
            // never pull full hosted bytes.
            List<string?>? partialUrls = null;
            List<string?>? partialThumbs = null;
            if (!ok)
            {
                merged.ErrorMessage = firstError ?? "Generation completed without returning usable image or video media.";
                for (var partialIdx = 0; partialIdx < want; partialIdx++)
                {
                    if (job.TryPersistLastPartialImage(key, partialIdx))
                    {
                        var localUrl = $"/api/jobs/{job.Id}/images/{key}/{partialIdx}";
                        var keptUrl = localUrl;
                        if (_b2 != null
                            && job.TryGetImagePath(key, partialIdx, out var keptPath, out var keptType))
                        {
                            try
                            {
                                keptUrl = await UploadResultToB2Async(job, key, partialIdx, keptPath, keptType);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[ui #{job.Id}] kept-preview upload failed for {key}/{partialIdx}: {ex.Message}; keeping local serving");
                            }
                        }
                        partialUrls ??= Enumerable.Repeat<string?>(null, want).ToList();
                        partialThumbs ??= Enumerable.Repeat<string?>(null, want).ToList();
                        partialUrls[partialIdx] = keptUrl;
                        partialThumbs[partialIdx] = localUrl + "?thumb=1";
                    }
                }
            }

            // In-process progression snapshots (persisted per-partial as the
            // stream ran) ride the gen-result on success AND failure so the
            // user can revisit how the image came together. Owner decision
            // 2026-08-11: snapshots are B2-hosted like results; a failed
            // snapshot upload keeps the local url (same documented exception
            // as kept previews — the artifact stays local-served and is never
            // evicted without a CdnKey, and a successful generation must not
            // fail over a preview nicety). `thumb` is the local ?thumb=1 card
            // preview, valid after eviction because eviction force-builds
            // disk thumbs first.
            var snapshots = job.GetPartialSnapshots(key);
            List<object>? progressImages = null;
            if (snapshots.Count > 0)
            {
                progressImages = new List<object>();
                foreach (var s in snapshots)
                {
                    var snapGen = UiJob.PartialSnapshotGenKey(key, s.PartialIndex);
                    var localUrl = $"/api/jobs/{job.Id}/images/{snapGen}/{s.ImageIndex}";
                    var url = localUrl;
                    if (_b2 != null
                        && job.TryGetImagePath(snapGen, s.ImageIndex, out var snapPath, out var snapType))
                    {
                        try
                        {
                            url = await UploadResultToB2Async(job, snapGen, s.ImageIndex, snapPath, snapType);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[ui #{job.Id}] snapshot upload failed for {snapGen}/{s.ImageIndex}: {ex.Message}; keeping local serving");
                        }
                    }
                    progressImages.Add(new
                    {
                        partialIndex = s.PartialIndex,
                        imageIndex = s.ImageIndex,
                        url,
                        thumb = localUrl + "?thumb=1",
                    });
                }
            }

            // Known payment/auth failures carry a "next step" hint + the URL
            // where it gets fixed; the card renders it under the error text.
            var actionHint = ok ? null : ProviderActionHints.For(key, merged.ErrorMessage);
            job.Emit(new
            {
                type = "gen-result",
                gen = key,
                ok,
                error = ok ? "" : merged.ErrorMessage,
                errorHint = actionHint?.Text,
                errorHintUrl = actionHint?.Url,
                ms = elapsed,
                images = urls,
                // Failure only: last streamed preview partial(s) kept visible,
                // index-aligned with the requested output indexes; hosted URLs
                // when B2 hosting is on, plus index-aligned local ?thumb=1
                // card previews.
                partialImages = partialUrls,
                partialThumbs,
                // Success or failure: the durably persisted in-process
                // streamed previews, in arrival order.
                progressImages,
                // Local ?thumb=1 card previews, index-aligned with `images`.
                // Always present so the client never appends `?thumb=1` to a
                // B2 original.
                thumbs,
                mediaType,
                label = label ?? key,
                size = actualSize,
                videoMode = key == KeyGrokWebVideo ? spec.VideoMode : null,
                videoDurationSeconds = key == KeyGrokWebVideo
                    ? spec.VideoDurationSeconds
                    : (int?)null,
                videoResolution = key == KeyGrokWebVideo ? spec.VideoResolution : null,
                videoAspectRatio = key == KeyGrokWebVideo ? spec.VideoAspectRatio : null,
                // Failed calls generally aren't billed, so report 0 for them.
                cost = ok ? totalCost : 0m,
            });
            Logger.Log($"[ui #{job.Id}]   <- {(ok ? $"OK ({urls.Count}/{want})" : $"FAIL ({merged.ErrorMessage})")} from {key} in {elapsed} ms"
                + (actionHint != null ? $"\n[ui #{job.Id}]      next step: {actionHint.Text} -> {actionHint.Url}" : ""));
            return merged;
        }

        // Rough per-call cost ceilings for the describe targets, mirroring the
        // "estimate, not bill" convention of the image generators' GetCost().
        // Ideogram is a published flat price; the LLM vision calls are
        // token-billed and estimated at ~1K image-input tokens + the 1200-token
        // output cap at each provider's current list price.
        private static decimal DescribeCostEstimate(string key) => key switch
        {
            KeyDescribeIdeogram => IdeogramModelPricing.IdeogramDescribe,
            KeyDescribeOpenAi => CatalogDescribeCostEstimate(ManagerCatalog.KeyGpt62Sol, DateTimeOffset.UtcNow),
            KeyDescribeOpenAiAstra => CatalogDescribeCostEstimate(ManagerCatalog.KeyGpt62Astra, DateTimeOffset.UtcNow),
            KeyDescribeClaude => CatalogDescribeCostEstimate(ManagerCatalog.KeyClaudeSonnet55, DateTimeOffset.UtcNow),
            KeyDescribeGemini => FlashDescribeCostEstimate(DateTimeOffset.UtcNow),
            KeyDescribeGeminiNeon => CatalogDescribeCostEstimate(ManagerCatalog.KeyGemini4Neon, DateTimeOffset.UtcNow),
            KeyDescribeGeminiCarbon => CatalogDescribeCostEstimate(ManagerCatalog.KeyGemini4Carbon, DateTimeOffset.UtcNow),
            KeyDescribeGeminiHydrogen => CatalogDescribeCostEstimate(ManagerCatalog.KeyGemini4Hydrogen, DateTimeOffset.UtcNow),
            KeyDescribeGeminiArgon => ArgonDescribeCostEstimate(DateTimeOffset.UtcNow),
            KeyDescribeGrok => CatalogDescribeCostEstimate(ManagerCatalog.KeyGrok47, DateTimeOffset.UtcNow),
            // Layout map is one Gemini vision call per input image (same
            // transport and token budget class as describe-gemini); the
            // server-side map rendering itself costs nothing.
            KeyLayoutMap => FlashDescribeCostEstimate(DateTimeOffset.UtcNow),
            _ => throw new ArgumentException($"Unknown describe key '{key}'.", nameof(key)),
        };

        // Argon and Carbon double on 2026-12-01 and 3.8 Flash doubles on
        // 2027-01-01; ManagerCatalog holds those schedules. The ceiling
        // assumes 2,000 input tokens and the describe output budget.
        public static decimal ArgonDescribeCostEstimate(DateTimeOffset at)
            => CatalogDescribeCostEstimate(ManagerCatalog.KeyGemini4Argon, at);

        public static decimal FlashDescribeCostEstimate(DateTimeOffset at)
            => CatalogDescribeCostEstimate(ManagerCatalog.KeyGeminiFlash, at);

        public static decimal CatalogDescribeCostEstimate(string managerKey, DateTimeOffset at)
        {
            var (inputUsdPerMTok, outputUsdPerMTok) = ManagerCatalog.All
                .Single(m => m.Key == managerKey)
                .PricesAt(at);
            return (2_000m * inputUsdPerMTok!.Value + DescribeMaxTokens * outputUsdPerMTok!.Value) / 1_000_000m;
        }

        private ILocalVisionModel BuildDescriber(string key)
        {
            var availabilityProblem = DescribeAvailabilityProblem(key);
            if (availabilityProblem != null)
            {
                throw new InvalidOperationException(availabilityProblem);
            }

            switch (key)
            {
                case KeyDescribeIdeogram:
                    RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                    return new IdeogramDescriber(_settings.IdeogramApiKey);
                // OpenAI and Gemini get their provider-native JSON output modes
                // on top of the prompt contract; Claude and Grok have no such
                // knob on these transports and rely on the prompt alone.
                case KeyDescribeOpenAi:
                    RequireKey(_settings.OpenAIApiKey, "OpenAIApiKey", key);
                    return new OpenAIVisionDescriber(_settings.OpenAIApiKey) { RequestJsonOutput = true };
                case KeyDescribeOpenAiAstra:
                    RequireKey(_settings.OpenAIApiKey, "OpenAIApiKey", key);
                    return new OpenAIVisionDescriber(_settings.OpenAIApiKey, OpenAIVisionDescriber.AstraModel) { RequestJsonOutput = true };
                case KeyDescribeClaude:
                    RequireKey(_settings.AnthropicApiKey, "AnthropicApiKey", key);
                    return new ClaudeVisionDescriber(_settings.AnthropicApiKey);
                case KeyDescribeGemini:
                    RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                    return new GeminiVisionDescriber(_settings.GoogleGeminiApiKey) { RequestJsonOutput = true };
                case KeyDescribeGeminiNeon:
                    RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                    return new GeminiVisionDescriber(_settings.GoogleGeminiApiKey, GeminiVisionDescriber.NeonModel) { RequestJsonOutput = true };
                case KeyDescribeGeminiCarbon:
                    RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                    return new GeminiVisionDescriber(_settings.GoogleGeminiApiKey, GeminiVisionDescriber.CarbonModel) { RequestJsonOutput = true };
                case KeyDescribeGeminiHydrogen:
                    RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                    return new GeminiVisionDescriber(_settings.GoogleGeminiApiKey, GeminiVisionDescriber.HydrogenModel) { RequestJsonOutput = true };
                case KeyDescribeGeminiArgon:
                    RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                    return new GeminiVisionDescriber(_settings.GoogleGeminiApiKey, GeminiVisionDescriber.ArgonModel) { RequestJsonOutput = true };
                case KeyDescribeGrok:
                    RequireKey(_settings.XAIGrokApiKey, "XAIGrokApiKey", key);
                    return new GrokVisionDescriber(_settings.XAIGrokApiKey);
                default:
                    throw new ArgumentException($"unknown describe target '{key}'");
            }
        }

        private static void ApplyEndpointExtraText(
            PromptDetails copy,
            UiJobSpec spec,
            string key,
            string jobId)
        {
            if (spec.GeneratorExtraTexts.TryGetValue(key, out var extraText)
                && !string.IsNullOrWhiteSpace(extraText))
            {
                var suffixed = WithEndpointExtraText(copy.Prompt, extraText);
                copy.ReplacePrompt(suffixed, suffixed, TransformationType.ManualSuffixation);
            }
            else if (key is KeyGpt2 or KeyGpt25Sunburst or KeyGpt25Flare or KeyGpt26Sunburst or KeyGpt26Flare)
            {
                // Loud on purpose: OpenAI image models without the default
                // anti-murk suffix reliably come back darker, and this once went
                // unnoticed for two days (2026-07-31 → 08-02). GPT Image 2.5
                // and 2.6 share the lineage and the same default suffix.
                Logger.Log($"[ui #{jobId}]   {key} extra text is blank for this call — anti-murk guidance was not sent");
            }
        }

        /// Describe targets return TEXT, not media: one provider call per
        /// attached input image, all-or-nothing (fail closed — a blank or
        /// missing description for ANY input fails the whole target rather
        /// than presenting partial output as success). The descriptions ride
        /// the persisted gen-result event (resultKind "text" + texts[]), which
        /// is how they survive restarts and reach archive views; they are
        /// deliberately absent from the image contact sheet.
        private async Task<TaskProcessResult> RunDescribeOneAsync(
            UiJob job,
            UiJobSpec spec,
            string key,
            PromptDetails pd)
        {
            pd = pd.Copy();
            ApplyEndpointExtraText(pd, spec, key, job.Id);
            var wallClock = Stopwatch.StartNew();
            job.Emit(new
            {
                type = "gen-start",
                gen = key,
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            var result = new TaskProcessResult
            {
                PromptDetails = pd,
                GeneratorKey = key,
                ImageGeneratorDescription = key,
                ContentType = "text/plain",
            };

            var texts = new List<object>();
            string? error = null;
            string label = key;
            var perCallCost = 0m;
            // The exact full prompt sent over the wire (user instruction + the
            // JSON reply contract), carried on the event so the card's
            // sent/returned exchange viewer shows precisely what went out.
            // Ideogram sends nothing (fixed built-in instruction), flagged as "".
            var isIdeogram = key == KeyDescribeIdeogram;
            var sentPrompt = "";
            try
            {
                if (!job.HasInputImage)
                {
                    throw new InvalidOperationException($"'{key}' requires an input image; this job has none.");
                }
                var describer = BuildDescriber(key);
                perCallCost = DescribeCostEstimate(key);
                label = isIdeogram
                    ? $"{describer.GetModelName()} (fixed instruction; the prompt is not sent)"
                    : describer.GetModelName();
                var instruction = pd.Prompt;
                if (string.IsNullOrWhiteSpace(instruction))
                {
                    // POST /api/jobs substitutes the default instruction for
                    // blank describe-only prompts, so this only guards direct
                    // programmatic misuse.
                    throw new InvalidOperationException("describe instruction is empty");
                }
                sentPrompt = isIdeogram ? "" : instruction + "\n\n" + DescribeJsonReplyContract;
                Logger.Log($"[ui #{job.Id}]   -> {key} ({describer.GetModelName()}, ~${perCallCost:0.###} x {job.InputImageCount} input(s))");

                for (var i = 0; i < job.InputImageCount; i++)
                {
                    var imageBytes = await File.ReadAllBytesAsync(job.InputImagePaths[i]);
                    var raw = await describer.DescribeImageAsync(
                        imageBytes,
                        isIdeogram ? instruction : sentPrompt,
                        maxTokens: DescribeMaxTokens,
                        temperature: DescribeTemperature);
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        // Blank text from any endpoint is a hard failure here,
                        // never an empty "success".
                        throw new InvalidDataException(
                            $"{key} returned no description for input image {i + 1} of {job.InputImageCount}.");
                    }
                    raw = raw.Trim();
                    // Ideogram's captions are the description by definition; the
                    // instruction-capable models answer under the JSON contract
                    // and a non-conforming reply fails this target.
                    var (description, comments) = isIdeogram ? (raw, "") : ParseDescribeJsonReply(raw);
                    texts.Add(new { inputIndex = i, text = description, comments, raw });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ui #{job.Id}]   <- EXCEPTION from {key}: {ex.Message}");
                error = ex.Message;
                texts.Clear();
            }

            var ok = error == null && texts.Count == job.InputImageCount && texts.Count > 0;
            result.IsSuccess = ok;
            if (!ok)
            {
                result.ErrorMessage = error ?? "Describe completed without returning a description.";
            }

            var elapsed = wallClock.ElapsedMilliseconds;
            var actionHint = ok ? null : ProviderActionHints.For(key, result.ErrorMessage);
            job.Emit(new
            {
                type = "gen-result",
                gen = key,
                ok,
                error = ok ? "" : result.ErrorMessage,
                errorHint = actionHint?.Text,
                errorHintUrl = actionHint?.Url,
                ms = elapsed,
                // No media: the frontend renders resultKind "text" cells from
                // texts[] and the viewer pairs each text with its archived
                // input image (/api/jobs/{id}/images/input/{inputIndex}).
                images = Array.Empty<string>(),
                mediaType = "",
                label,
                size = (string?)null,
                resultKind = "text",
                texts,
                // The exact wire prompt ("" = Ideogram's fixed instruction);
                // each texts[] entry carries the raw pre-parse reply, so the
                // card's exchange toggle can show precisely what was sent to
                // and returned from each endpoint.
                sentPrompt,
                cost = ok ? perCallCost * texts.Count : 0m,
            });
            Logger.Log($"[ui #{job.Id}]   <- {(ok ? $"OK ({texts.Count} description(s))" : $"FAIL ({result.ErrorMessage})")} from {key} in {elapsed} ms"
                + (actionHint != null
                    ? $"\n[ui #{job.Id}]      next step: {actionHint.Text} -> {actionHint.Url}"
                    : ""));
            return result;
        }

        /// Layout map (image → labeled section-map IMAGE): one Gemini vision
        /// call per attached input under the strict LayoutMapJsonContract,
        /// then a server-rendered flat-color map with a numbered legend and
        /// the model's one-sentence summary baked into the PNG. All-or-nothing
        /// like describe (any input failing fails the whole target), but the
        /// result is a normal image gen-result so cards, the viewer, video
        /// follow-ups, and favorites treat it like any generated image. The
        /// composer prompt is optional CONTEXT for the section labels, never
        /// the contract itself. Deliberately absent from the contact sheet.
        private async Task<TaskProcessResult> RunLayoutMapOneAsync(
            UiJob job,
            UiJobSpec spec,
            string key,
            PromptDetails pd)
        {
            pd = pd.Copy();
            ApplyEndpointExtraText(pd, spec, key, job.Id);
            var wallClock = Stopwatch.StartNew();
            job.Emit(new
            {
                type = "gen-start",
                gen = key,
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            var result = new TaskProcessResult
            {
                PromptDetails = pd,
                GeneratorKey = key,
                ImageGeneratorDescription = key,
                ContentType = "image/png",
            };

            // Rendered map files, collected first and only published to the
            // job store + event after EVERY input succeeded (all-or-nothing;
            // the durable files themselves stay on disk either way).
            var rendered = new List<(string Path, string Size)>();
            string? error = null;
            string label = key;
            var perCallCost = 0m;
            var sentPrompt = "";
            try
            {
                if (!job.HasInputImage)
                {
                    throw new InvalidOperationException($"'{key}' requires an input image; this job has none.");
                }
                var availabilityProblem = DescribeAvailabilityProblem(key);
                if (availabilityProblem != null)
                {
                    throw new InvalidOperationException(availabilityProblem);
                }
                RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                var describer = new GeminiVisionDescriber(_settings.GoogleGeminiApiKey)
                {
                    RequestJsonOutput = true,
                };
                perCallCost = DescribeCostEstimate(key);
                label = $"{describer.GetModelName()} layout map";
                var instruction = pd.Prompt;
                sentPrompt = string.IsNullOrWhiteSpace(instruction)
                    ? LayoutMapJsonContract
                    : LayoutMapJsonContract
                        + "\n\nUser-provided context about the image (may inform your section labels): "
                        + instruction.Trim();
                Logger.Log($"[ui #{job.Id}]   -> {key} ({describer.GetModelName()}, ~${perCallCost:0.###} x {job.InputImageCount} input(s))");

                var today = DateTime.Now.ToString("yyyy-MM-dd-dddd");
                var folder = Path.Combine(_settings.ImageDownloadBaseFolder, today, "LayoutMaps");
                Directory.CreateDirectory(folder);

                for (var i = 0; i < job.InputImageCount; i++)
                {
                    var imageBytes = await File.ReadAllBytesAsync(job.InputImagePaths[i]);
                    RasterInfo info;
                    try
                    {
                        info = Raster.Identify(imageBytes);
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new InvalidDataException(
                            $"input image {i + 1} of {job.InputImageCount} could not be decoded for its dimensions: {ex.Message}", ex);
                    }
                    var raw = await describer.DescribeImageAsync(
                        imageBytes,
                        sentPrompt,
                        maxTokens: DescribeMaxTokens,
                        temperature: DescribeTemperature);
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        throw new InvalidDataException(
                            $"{key} returned no reply for input image {i + 1} of {job.InputImageCount}.");
                    }
                    var (summary, regions) = ParseLayoutMapJsonReply(raw.Trim());
                    Logger.Log($"[ui #{job.Id}]   {key} input {i + 1}: {regions.Count} region(s) — "
                        + string.Join("; ", regions.Select(r => r.Label)));

                    var mapPath = Path.Combine(
                        folder,
                        $"{DateTime.Now:HHmmss_fff}_{job.Id}_map{i}.png");
                    using (var map = UiLayoutMapRenderer.Render(info.Width, info.Height, regions, summary))
                    {
                        Raster.SavePng(map, mapPath);
                        rendered.Add((mapPath, $"{map.Width}x{map.Height}"));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ui #{job.Id}]   <- EXCEPTION from {key}: {ex.Message}");
                error = ex.Message;
                rendered.Clear();
            }

            var ok = error == null && rendered.Count == job.InputImageCount && rendered.Count > 0;
            var urls = new List<string>();
            var thumbs = new List<string>();
            if (ok)
            {
                try
                {
                    for (var i = 0; i < rendered.Count; i++)
                    {
                        job.StoreImagePath(key, i, rendered[i].Path, "image/png");
                        if (_b2 != null)
                        {
                            // Retry x3 inside; throws on final failure, failing
                            // this target visibly. Never a local URL substitute.
                            urls.Add(await UploadResultToB2Async(job, key, i, rendered[i].Path, "image/png"));
                        }
                        else
                        {
                            urls.Add($"/api/jobs/{job.Id}/images/{key}/{i}");
                        }
                        thumbs.Add($"/api/jobs/{job.Id}/images/{key}/{i}?thumb=1");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ui #{job.Id}]   <- EXCEPTION publishing {key} maps: {ex.Message}");
                    error = ex.Message;
                    ok = false;
                    urls.Clear();
                    thumbs.Clear();
                }
            }

            result.IsSuccess = ok;
            if (!ok)
            {
                result.ErrorMessage = error ?? "Layout map completed without producing a map image.";
            }

            var elapsed = wallClock.ElapsedMilliseconds;
            var actionHint = ok ? null : ProviderActionHints.For(key, result.ErrorMessage);
            job.Emit(new
            {
                type = "gen-result",
                gen = key,
                ok,
                error = ok ? "" : result.ErrorMessage,
                errorHint = actionHint?.Text,
                errorHintUrl = actionHint?.Url,
                ms = elapsed,
                images = urls,
                thumbs = ok ? thumbs : null,
                mediaType = ok ? "image/png" : "",
                label,
                size = ok ? rendered[0].Size : null,
                // The exact wire prompt (contract + optional user context),
                // recorded on the persisted event like describe's.
                sentPrompt,
                cost = ok ? perCallCost * urls.Count : 0m,
            });
            Logger.Log($"[ui #{job.Id}]   <- {(ok ? $"OK ({urls.Count} map(s))" : $"FAIL ({result.ErrorMessage})")} from {key} in {elapsed} ms"
                + (actionHint != null
                    ? $"\n[ui #{job.Id}]      next step: {actionHint.Text} -> {actionHint.Url}"
                    : ""));
            return result;
        }

        private IImageGenerator BuildGrokWeb(GrokWebClient client, UiJobSpec spec)
        {
            var mapped = UiShapeMapping.GrokAspect(spec.Shape);
            // Unlike the official API, the consumer transport has no working
            // prompt-aware auto mode: live tests (2026-07-20) showed both
            // literal "auto" and an omitted aspect_ratio always return Grok's
            // native 2:3 default regardless of the prompt. 2:3 is a poor
            // universal shape, so an unspecified shape requests square 1:1
            // instead (a declared input default chosen before the call, not a
            // fallback). Explicit shapes map as usual.
            var ar = mapped == "" ? "1:1" : mapped;
            return new GrokWebImagineGenerator(
                client, maxConcurrency: 1, _stats,
                pro: _options.GrokWebPro,
                aspectRatio: ar,
                enableSideBySide: spec.GrokWebSideBySide ?? _options.GrokWebSideBySide,
                settings: _settings,
                captureSessions: false);
        }

        private async Task<IImageGenerator> BuildGrokWebEditAsync(
            GrokWebClient client,
            UiJobSpec spec,
            UiJob job)
        {
            // Empty means inherit/derive from the submitted source image.
            var aspectRatio = UiShapeMapping.GrokAspect(spec.Shape);
            return await GrokWebImagineEditGenerator.CreateAsync(
                client,
                job.InputImagePath,
                maxConcurrency: 1,
                _stats,
                pro: _options.GrokWebPro,
                aspectRatio: aspectRatio,
                enableSideBySide: _options.GrokWebSideBySide,
                settings: _settings,
                captureSessions: false);
        }

        private async Task<IImageGenerator> BuildGrokWebChatAsync(
            GrokWebClient client,
            UiJob job)
        {
            if (!job.HasInputImage)
            {
                return GrokWebImagineChatGenerator.CreateForText(
                    client,
                    maxConcurrency: 1,
                    _stats,
                    chatModel: GrokWebClient.DefaultChatModel);
            }

            return await GrokWebImagineChatGenerator.CreateAsync(
                client,
                job.InputImagePath,
                maxConcurrency: 1,
                _stats,
                chatModel: GrokWebClient.DefaultChatModel);
        }

        private async Task<IImageGenerator> BuildGrokWebVideoAsync(
            GrokWebClient client,
            UiJob job,
            UiJobSpec spec)
        {
            if (!job.HasInputImage)
            {
                throw new InvalidOperationException(
                    "grok-web image-to-video requires a source image");
            }

            var aspectRatio = spec.VideoAspectRatio == "source"
                ? UiShapeMapping.GrokAspectForInput(
                    job.InputImageWidth,
                    job.InputImageHeight)
                : spec.VideoAspectRatio;
            return await GrokWebImagineVideoGenerator.CreateFromImageAsync(
                client,
                _settings,
                _stats,
                job.InputImagePath,
                maxConcurrency: 1,
                aspectRatio: aspectRatio,
                resolution: spec.VideoResolution,
                durationSeconds: spec.VideoDurationSeconds,
                enableSideBySide: false,
                videoMode: spec.VideoMode);
        }

        private static ImageGeneratorApiType BflApiTypeForKey(string key)
            => UiTargetCatalog.Find(key)?.BflApiType
                ?? throw new ArgumentException($"Unknown BFL generator key '{key}'.", nameof(key));

        // enablePartials: emit mid-stream preview images for gpt-image-2. Only
        // valid for a single-image request; RunOneAsync turns it off for N>1
        // (each generator is always built single-image and invoked N times).
        private IImageGenerator BuildGenerator(string key, UiJobSpec spec, UiJob job, bool enablePartials = true)
        {
            var availabilityProblem = DescribeAvailabilityProblem(key);
            if (availabilityProblem != null)
            {
                throw new InvalidOperationException(availabilityProblem);
            }

            switch (key)
            {
                case KeyGpt2:
                case KeyGpt25Sunburst:
                case KeyGpt25Flare:
                case KeyGpt26Sunburst:
                case KeyGpt26Flare:
                    {
                        RequireKey(_settings.OpenAIApiKey, "OpenAIApiKey", key);
                        var modelId = key switch
                        {
                            KeyGpt26Sunburst => GptImage2Generator.Sunburst26ModelId,
                            KeyGpt26Flare => GptImage2Generator.Flare26ModelId,
                            KeyGpt25Sunburst => GptImage2Generator.SunburstModelId,
                            KeyGpt25Flare => GptImage2Generator.FlareModelId,
                            _ => GptImage2Generator.DefaultModelId,
                        };
                        if (!Enum.TryParse<OpenAIGPTImageOneQuality>(spec.Quality, true, out var quality))
                        {
                            quality = OpenAIGPTImageOneQuality.high;
                        }
                        // Option-mapping policy (2026-07-20): unsupported option
                        // values are mapped before the call, not failed. The
                        // 2.5/2.6 tiers xhigh/max become high on gpt-image-2.
                        if (!GptImage2Generator.ModelSupportsQuality(modelId, quality))
                        {
                            quality = OpenAIGPTImageOneQuality.high;
                        }
                        var generationApiType = key switch
                        {
                            KeyGpt26Sunburst => ImageGeneratorApiType.GptImage26Sunburst,
                            KeyGpt26Flare => ImageGeneratorApiType.GptImage26Flare,
                            KeyGpt25Sunburst => ImageGeneratorApiType.GptImage25Sunburst,
                            KeyGpt25Flare => ImageGeneratorApiType.GptImage25Flare,
                            _ => ImageGeneratorApiType.GptImage2,
                        };
                        var editApiType = key switch
                        {
                            KeyGpt26Sunburst => ImageGeneratorApiType.GptImage26SunburstEdit,
                            KeyGpt26Flare => ImageGeneratorApiType.GptImage26FlareEdit,
                            KeyGpt25Sunburst => ImageGeneratorApiType.GptImage25SunburstEdit,
                            KeyGpt25Flare => ImageGeneratorApiType.GptImage25FlareEdit,
                            _ => ImageGeneratorApiType.GptImage2Edit,
                        };
                        var size = UiShapeMapping.Gpt2Size(
                            spec.Shape,
                            spec.Detail,
                            job.InputImageWidth,
                            job.InputImageHeight);
                        if (job.HasInputImage)
                        {
                            return new GptImage2EditGenerator(
                                _settings.OpenAIApiKey, maxConcurrency: 2,
                                job.InputImagePaths.Take(4).ToArray(),
                                size, quality, _stats, "ui",
                                imageCount: 1,
                                modelId: modelId,
                                apiType: editApiType);
                        }
                        return new GptImage2Generator(
                            _settings.OpenAIApiKey, maxConcurrency: 2,
                            sizePool: new[] { size },
                            moderation: spec.Moderation,
                            qualityPool: new[] { quality },
                            stats: _stats, name: "ui",
                            modelId: modelId,
                            apiType: generationApiType,
                            // No day-folder PartialsLive copies in UI mode
                            // (owner decision 2026-08-11): the UI persists its
                            // own durable progression snapshots per partial;
                            // the CLI-era live-viewing copies were unreferenced
                            // disk growth on the shared host.
                            partialSaveFolder: null,
                            popUpPartials: false,
                            imageCount: 1,
                            partialImageCallback: !enablePartials ? null : (partialIndex, imageIndex, bytes) =>
                            {
                                var outputIndex = Math.Max(0, imageIndex);
                                // Preview and final bytes deliberately share one stable
                                // URL. Each partial replaces the previous bytes, then
                                // RunOneAsync replaces them with the completed image.
                                job.StoreImage(key, outputIndex, bytes, "image/png");
                                // Independently persist this exact partial as a durable
                                // progression snapshot so the in-process previews stay
                                // viewable after the final lands (user-required,
                                // 2026-08-10). Additive: a failed write only means this
                                // step is absent from the progression strip.
                                if (!job.TryPersistPartialSnapshot(key, partialIndex, outputIndex, bytes, "image/png"))
                                {
                                    Logger.Log($"[ui #{job.Id}] could not persist progression snapshot {key}~p{partialIndex}/{outputIndex}");
                                }
                                job.Emit(new
                                {
                                    type = "gen-partial",
                                    gen = key,
                                    partialIndex,
                                    imageIndex = outputIndex,
                                    url = $"/api/jobs/{job.Id}/images/{key}/{outputIndex}",
                                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                });
                            });
                    }

                case KeyGpt1:
                case KeyGpt1Mini:
                    {
                        // Text-only target: on image jobs it runs from the prompt
                        // alone (see ImageCapableKeys).
                        if (!Enum.TryParse<OpenAIGPTImageOneQuality>(spec.Quality, true, out var quality))
                        {
                            quality = OpenAIGPTImageOneQuality.high;
                        }
                        return new GptImageOneGenerator(
                            _settings.OpenAIApiKey,
                            maxConcurrency: 2,
                            size: UiShapeMapping.Gpt1Size(spec.Shape),
                            moderation: spec.Moderation,
                            quality,
                            apiType: key == KeyGpt1 ? ImageGeneratorApiType.GptImage1 : ImageGeneratorApiType.GptImage1Mini,
                            _stats,
                            name: $"{key} ui",
                            imageCount: 1);
                    }

                case KeyGrokApi:
                case KeyGrokApiPro:
                    {
                        RequireKey(_settings.XAIGrokApiKey, "XAIGrokApiKey", key);
                        var pro = key == KeyGrokApiPro;
                        var mappedAr = UiShapeMapping.GrokAspect(
                            spec.Shape,
                            job.InputImageWidth,
                            job.InputImageHeight);
                        if (job.HasInputImage)
                        {
                            return new GrokImagineEditGenerator(
                                _settings.XAIGrokApiKey, maxConcurrency: 1, _stats, _settings,
                                inputImage: job.InputImagePath,
                                pro: pro,
                                aspectRatio: mappedAr,
                                quality: pro ? spec.Quality : "",
                                inputImages: pro ? job.InputImagePaths : new[] { job.InputImagePath });
                        }
                        return new GrokImagineGenerator(
                            _settings.XAIGrokApiKey, 1,
                            pro ? ImageGeneratorApiType.GrokImaginePro : ImageGeneratorApiType.GrokImagine,
                            _stats, "ui",
                            aspectRatio: mappedAr == "" ? "auto" : mappedAr,
                            quality: pro ? spec.Quality : "",
                            resolution: UiShapeMapping.GrokResolution(spec.Detail),
                            settings: _settings,
                            imageCount: 1);
                    }

                case KeyGoogle:
                case KeyGooglePro:
                    {
                        // Gemini honors aspectRatio + imageSize; a pasted image rides
                        // along as a reference part (Gemini is natively multimodal).
                        // No n equivalent — Gemini returns what it returns.
                        RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                        var googleApiType = key == KeyGooglePro
                            ? ImageGeneratorApiType.GoogleNanoBananaPro
                            : ImageGeneratorApiType.GoogleNanoBanana;
                        var googleAspect = UiShapeMapping.GoogleAspect(
                            spec.Shape,
                            job.InputImageWidth,
                            job.InputImageHeight);
                        return new GoogleGenerator(
                            googleApiType, _settings.GoogleGeminiApiKey, maxConcurrency: 2,
                            _stats, name: $"{key} ui",
                            aspectRatio: googleAspect == "" ? null : googleAspect,
                            imageSize: UiShapeMapping.GoogleImageSize(spec.Detail),
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null);
                    }

                case KeyBflFlux3:
                case KeyBflFlux3Klein:
                    {
                        RequireKey(_settings.BFLApiKey, "BFLApiKey", key);
                        var apiType = BflApiTypeForKey(key);
                        var aspect = UiShapeMapping.GrokAspect(spec.Shape);
                        if (string.IsNullOrEmpty(aspect))
                        {
                            aspect = "auto";
                        }
                        var resolution = UiShapeMapping.BflFlux3Resolution(
                            spec.Detail,
                            spec.Shape,
                            job.HasInputImage);
                        return new BFLGenerator(
                            apiType, _settings.BFLApiKey,
                            maxConcurrency: 2, aspect, false, 1024, 1024, _stats, $"{key} ui",
                            inputImagePaths: job.HasInputImage ? job.InputImagePaths.Take(10).ToArray() : null,
                            flux3Resolution: resolution);
                    }

                case KeyBfl:
                case KeyBflFlux2Pro:
                case KeyBflFlux2Max:
                case KeyBflFlux2Flex:
                case KeyBflFlux2Klein4b:
                case KeyBflFlux2Klein9bPreview:
                case KeyBflFlux2Klein9b:
                case KeyBflKontextPro:
                case KeyBflKontextMax:
                case KeyBflFlux11Ultra:
                case KeyBflFlux11:
                case KeyBflFluxPro:
                case KeyBflFluxDev:
                    {
                        RequireKey(_settings.BFLApiKey, "BFLApiKey", key);
                        var apiType = BflApiTypeForKey(key);
                        var legacyDimensions = apiType is ImageGeneratorApiType.BFLv11
                            or ImageGeneratorApiType.BFLFluxPro
                            or ImageGeneratorApiType.BFLFluxDev;
                        var (bflWidth, bflHeight) = legacyDimensions
                            ? UiShapeMapping.BflLegacySize(
                                spec.Shape,
                                job.InputImageWidth,
                                job.InputImageHeight)
                            : UiShapeMapping.BflSize(
                                spec.Shape,
                                spec.Detail,
                                job.InputImageWidth,
                                job.InputImageHeight);
                        var bflAspect = UiShapeMapping.GrokAspect(
                            spec.Shape,
                            job.InputImageWidth,
                            job.InputImageHeight);
                        if (string.IsNullOrEmpty(bflAspect))
                        {
                            bflAspect = "1:1";
                        }
                        return new BFLGenerator(
                            apiType, _settings.BFLApiKey,
                            maxConcurrency: 2, bflAspect, false, bflWidth, bflHeight, _stats, $"{key} ui",
                            inputImagePath: job.HasInputImage && IsImageCapable(key) ? job.InputImagePath : null);
                    }

                case KeyKrea:
                case KeyKreaTurbo:
                case KeyKreaLarge:
                    {
                        RequireKey(_settings.KreaApiKey, "KreaApiKey", key);
                        var variant = key switch
                        {
                            KeyKreaTurbo => Krea2Variant.MediumTurbo,
                            KeyKreaLarge => Krea2Variant.Large,
                            _ => Krea2Variant.Medium,
                        };
                        return new Krea2Generator(
                            _settings.KreaApiKey,
                            maxConcurrency: 2,
                            variant,
                            UiShapeMapping.KreaAspect(
                                spec.Shape,
                                job.InputImageWidth,
                                job.InputImageHeight),
                            _stats,
                            name: $"{key} ui",
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null);
                    }

                case KeyIdeogramV45:
                case KeyIdeogramV46:
                    {
                        // Ideogram 4.5/4.6 use Generate without an input. With
                        // inputs they use Precise Edit: the first image is
                        // edited at its own size and later images are
                        // references. Precise Edit has no size field, so an
                        // explicit shape cannot apply.
                        RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                        var isV46 = key == KeyIdeogramV46;
                        if (job.HasInputImage && !spec.Shape.Equals("auto", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new NotSupportedException(
                                $"{(isV46 ? "Ideogram 4.6" : "Ideogram 4.5")} Precise Edit always keeps the source dimensions and cannot override output aspect ratio.");
                        }
                        var size = job.HasInputImage
                            ? ""
                            : (isV46
                                ? UiShapeMapping.IdeogramV46Size(spec.Shape, spec.Detail)
                                : UiShapeMapping.IdeogramV45Size(spec.Shape));
                        return new IdeogramV45Generator(
                            _settings.IdeogramApiKey, maxConcurrency: 1,
                            size,
                            IdeogramV45Generator.QualityFromOption(spec.Quality),
                            _stats, $"{key} ui",
                            inputImagePaths: job.HasInputImage ? job.InputImagePaths : null,
                            apiType: isV46 ? ImageGeneratorApiType.IdeogramV46 : ImageGeneratorApiType.IdeogramV45);
                    }

                case KeyIdeogram:
                    {
                        // Ideogram 4.0 uses /generate without an input and the
                        // dedicated /remix endpoint when one is attached. Detail
                        // has no effect; auto Remix maps to the nearest published
                        // 2K resolution so the source is not cropped to square.
                        RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                        return new IdeogramV4Generator(
                            _settings.IdeogramApiKey, maxConcurrency: 1,
                            UiShapeMapping.IdeogramV4Resolution(
                                spec.Shape,
                                job.InputImageWidth,
                                job.InputImageHeight),
                            IdeogramRenderingSpeed.DEFAULT,
                            _stats, "ideogram ui",
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null);
                    }

                case KeyIdeogramV3:
                    {
                        // V3 is the image-capable Ideogram target: a pasted image
                        // rides along as a style reference; without one it's plain
                        // text-to-image on the same endpoint.
                        RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                        return new IdeogramV3Generator(
                            _settings.IdeogramApiKey, maxConcurrency: 1,
                            IdeogramV3StyleType.AUTO, IdeogramMagicPromptOption.ON,
                            UiShapeMapping.IdeogramV3Aspect(
                                spec.Shape,
                                job.InputImageWidth,
                                job.InputImageHeight),
                            IdeogramRenderingSpeed.QUALITY,
                            "", _stats, "ideogram-v3 ui",
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null,
                            imageCount: 1);
                    }

                case KeyIdeogramV2:
                    {
                        // V2 remains available through Ideogram's legacy JSON
                        // /generate endpoint. It is text-only in this UI and returns
                        // one image; detail and n therefore have no effect.
                        RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                        return new IdeogramGenerator(
                            _settings.IdeogramApiKey, maxConcurrency: 1,
                            IdeogramMagicPromptOption.ON,
                            UiShapeMapping.IdeogramV3Aspect(spec.Shape),
                            styleType: null,
                            negativePrompt: "",
                            model: IdeogramModel.V_2,
                            stats: _stats,
                            name: $"{key} ui");
                    }

                case KeyRecraft:
                case KeyRecraftV41Flash:
                case KeyRecraftV41Utility:
                case KeyRecraftV41Pro:
                case KeyRecraftV41Vector:
                case KeyRecraftV3:
                case KeyRecraftV4:
                case KeyRecraftV4Pro:
                    {
                        RequireKey(_settings.RecraftApiKey, "RecraftApiKey", key);
                        var sendRecraftImage = job.HasInputImage && IsImageCapable(key);
                        if (sendRecraftImage && !spec.Shape.Equals("auto", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new NotSupportedException(
                                "Recraft image-to-image always follows the source dimensions and cannot override output aspect ratio.");
                        }
                        // Recraft text-to-image accepts the same "w:h" aspect strings
                        // as Grok. Its image-to-image endpoint takes no size field and
                        // inherently follows the source dimensions.
                        var recraftAspect = UiShapeMapping.GrokAspect(spec.Shape);
                        // With an input image, every image-capable Recraft model
                        // runs through image-to-image; output size follows the source.
                        return new RecraftGenerator(
                            _settings.RecraftApiKey, maxConcurrency: 1,
                            RecraftImageSize._1024x1024, RecraftStyle.any,
                            null, null, null, _stats, $"{key} ui",
                            model: RecraftModelForKey(key),
                            inputImagePath: sendRecraftImage ? job.InputImagePath : null,
                            sizeOverride: recraftAspect,
                            imageCount: 1);
                    }

                case KeyLocalKlein:
                case KeyLocalZImage:
                    {
                        if (job.HasInputImage)
                        {
                            throw new NotSupportedException($"{key} is text-to-image only in the local UI; remove the input image");
                        }
                        return _generatorGroups.BuildByShortName(key);
                    }

                default:
                    throw new ArgumentException($"unknown generator '{key}'");
            }
        }

        // Minimal PNG/JPEG dimension reader for the display label. Returns "WxH",
        // or null for anything it doesn't recognize — no decode, no exceptions used
        // for control flow. PNG and JPEG cover every provider the UI shows.
        private static string ReadImageSize(string path)
        {
            try
            {
                if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    var (width, height) = SvgRaster.IntrinsicSize(path);
                    return $"{width}x{height}";
                }
                var info = Raster.Identify(path);
                return $"{info.Width}x{info.Height}";
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not identify saved result dimensions for {path}: {ex.Message}");
                return null;
            }
        }

        private static string ReadImageSize(byte[] b)
        {
            if (b == null) return null;

            // PNG: 8-byte signature, then IHDR with width @16, height @20 (BE).
            if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            {
                int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return (w > 0 && h > 0) ? $"{w}x{h}" : null;
            }

            // JPEG: FF D8, then walk segments to the first SOF (C0..CF except
            // C4/C8/CC); its payload is precision(1), height(2), width(2).
            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                int p = 2;
                while (p + 9 < b.Length)
                {
                    if (b[p] != 0xFF) { p++; continue; }
                    int marker = b[p + 1];
                    // Standalone markers (padding, RSTn, SOI/EOI) carry no length.
                    if (marker == 0xFF || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
                    {
                        p += 2;
                        continue;
                    }
                    int segLen = (b[p + 2] << 8) | b[p + 3];
                    if (segLen < 2) return null;
                    bool isSof = marker >= 0xC0 && marker <= 0xCF
                        && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (isSof)
                    {
                        int h = (b[p + 5] << 8) | b[p + 6];
                        int w = (b[p + 7] << 8) | b[p + 8];
                        return (w > 0 && h > 0) ? $"{w}x{h}" : null;
                    }
                    p += 2 + segLen;
                }
                return null;
            }

            return null;
        }

        public ValueTask DisposeAsync()
        {
            _finalizationLimit.Dispose();
            return ValueTask.CompletedTask;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");

        private static void RequireKey(string value, string settingName, string genName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Settings.{settingName} is not set; cannot run '{genName}'.");
            }
        }
    }
}
