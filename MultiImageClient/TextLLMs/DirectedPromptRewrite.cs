#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public static class DirectedPromptRewrite
    {
        public const string OpusModel = "claude-opus-5-5";
        public const string SonnetModel = "claude-sonnet-5-5";
        public const string DefaultAdviceKey = ManagerCatalog.KeyClaudeOpus55;
        public const string DefaultFleshOutPrimaryKey = ManagerCatalog.KeyClaudeOpus55;
        public const string DefaultFleshOutSecondaryKey = ManagerCatalog.KeyClaudeSonnet55;
        public const string DefaultCondenseKey = ManagerCatalog.KeyClaudeSonnet55;
        // Default two quick-button models for flesh-out; any model in
        // ManagerCatalog.All may be chosen locally in Settings (2026-11-19).
        public static readonly IReadOnlyList<string> Models = new[] { OpusModel, SonnetModel };
        public const string AdviceModel = OpusModel;
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

        // Condensing shortens the prompt only for selected targets over their
        // prompt limit. See docs/prompt-length-limits.md.
        public const string CondenseModel = SonnetModel;
        public const string CondensePurpose = "condense";

        // Models count characters poorly, so the request aims 10% under the
        // budget. The server still rejects a reply over the budget.
        public static string CondenseInstruction(int budget, int sourceCharacters)
        {
            var aim = budget * 9 / 10;
            var percent = (int)Math.Round(100.0 * aim / sourceCharacters);
            return $"Condense this image-generation prompt to at most {budget:N0} characters. "
                + "Count every letter, space, punctuation mark, and line break. "
                + $"The source has {sourceCharacters:N0} characters. "
                + $"Aim for about {aim:N0} characters, about {percent}% of the source. "
                + "Keep everything that changes the image: subjects, names, counts, actions, positions, spatial relationships, setting, style, materials, colors, lighting, mood, and composition. "
                + "Keep requested on-image text word for word. "
                + "Remove repetition, filler, and wording that does not change the image. "
                + "Merge descriptions that overlap. "
                + "Do not add subjects, objects, or details. "
                + "Keep any lighting the source states. "
                + "If it states none, describe bright, clear daytime light in a few words. "
                + "Never make the scene dim, murky, or dark unless the source asks for that. "
                + "Return only the condensed prompt.";
        }

        private static readonly SemaphoreSlim Slots = new(8, 8);

        // Resolves either a ManagerCatalog key (e.g. "manager-gemini-4-neon")
        // or an exact provider model ID (e.g. "gemini-4-neon"). Returns null
        // for any unknown identifier.
        public static ManagerDefinition? ResolveModel(string? modelOrKey)
            => ManagerCatalog.FindByKeyOrModel(modelOrKey);

        public static string? AvailabilityProblem(string modelOrKey, Settings settings)
        {
            var definition = ResolveModel(modelOrKey);
            return definition == null
                ? "Unknown rewrite model."
                : ManagerCatalog.DescribeAvailabilityProblem(definition, settings);
        }

        public static async Task<ClaudePromptAdviceResult> RewriteAsync(string modelOrKey, string instruction, string prompt, Settings settings, CancellationToken ct)
        {
            var definition = ResolveModel(modelOrKey)
                ?? throw new InvalidOperationException("Unknown rewrite model.");
            var problem = ManagerCatalog.DescribeAvailabilityProblem(definition, settings);
            if (problem != null) throw new InvalidOperationException(problem);
            if (!await Slots.WaitAsync(0, ct)) throw new InvalidOperationException("Eight prompt rewrites are already running. Try again after one finishes.");
            var wire = ClaudeService.BuildPromptAdviceWirePrompt(instruction, prompt);
            var raw = "";
            try
            {
                var client = ManagerCatalog.Build(definition, settings, expectJson: false);
                var reply = await client.CompleteAsync(
                    ClaudeService.PromptAdviceSystemPrompt,
                    new[] { new ManagerChatMessage { Role = "user", Text = wire } },
                    ct);
                raw = reply.RawResponse;
                var replacement = ValidateReply(reply);
                return new ClaudePromptAdviceResult
                {
                    Model = definition.Model,
                    WirePrompt = wire,
                    RawResponse = raw,
                    ResultPrompt = replacement,
                };
            }
            catch (Exception ex)
            {
                return new ClaudePromptAdviceResult
                {
                    Model = definition.Model,
                    WirePrompt = wire,
                    RawResponse = raw,
                    Error = ex.Message,
                };
            }
            finally { Slots.Release(); }
        }

        public static string ValidateReply(ManagerChatReply reply)
        {
            if (reply == null) throw new InvalidDataException("The rewrite returned no reply.");
            if (!string.IsNullOrWhiteSpace(reply.ProviderStop))
                throw new InvalidDataException($"The rewrite did not finish ({reply.ProviderStop}).");
            var text = reply.Text;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000)
                throw new InvalidDataException("The replacement must contain between 1 and 100,000 characters.");
            return text;
        }
    }
}
