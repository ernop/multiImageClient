using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;


namespace MultiImageClient
{
    public class ClaudeService
    {
        private readonly AnthropicClient _anthropicClient;
        private readonly SemaphoreSlim _claudeSemaphore;
        private MultiClientRunStats stats;

        public ClaudeService(string apiKey, int maxConcurrency, MultiClientRunStats stats)
        {
            var anthropicApikeyAuth = new APIAuthentication(apiKey);
            _anthropicClient = new AnthropicClient(anthropicApikeyAuth);
            _claudeSemaphore = new SemaphoreSlim(maxConcurrency);
            this.stats = stats;
        }

        ///Claude gets mad sometimes. This is for detecting this and optionally derailing since you probably don't want to continue with this bad rewrite output.
        internal static bool DidClaudeRefuse(string claudeResponse)
        {
            var unhappyClaudeResponses = new List<string>
            {
                "i'm sorry, i can't",
                "sexualized",
                "i will not produce",
                "harmful stereotypes",
                "i apologize",
                "don't feel comfortable",
                "do not feel comfortable",
                "that is overtly",
                "not comfortable",
                "will not generate",
                "will not be able to generate",
                "i regret th"
            };

            foreach (var unhappyClaudeResponse in unhappyClaudeResponses)
            {
                if (claudeResponse.Contains(unhappyClaudeResponse, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }


        public async Task<TaskProcessResult> RewritePromptAsync(PromptDetails promptDetails, decimal temp)
        {
            if (ClaudeWillHateThis(promptDetails.Prompt))
            {
                promptDetails.AddStep("Claude wouldn't have touched this prompt", TransformationType.ClaudeWouldRefuseRewrite);
                Logger.Log($"\t\tClaude would have refused to rewrite: {promptDetails.Show()}");
                stats.ClaudeWouldRefuseCount++;
                return new TaskProcessResult { ImageGeneratorDescription="Claude?", IsSuccess = false, ErrorMessage = "Claude wouldn't have touched this prompt", PromptDetails = promptDetails, TextGenerator = TextGeneratorApiType.Claude, GenericImageErrorType = GenericImageGenerationErrorType.RequestModerated};
            }
            await _claudeSemaphore.WaitAsync();
            try
            {
                var messages = new List<Message>()
                {
                    new Message(RoleType.User, promptDetails.Prompt),
                };

                var parameters = new MessageParameters()
                {
                    Messages = messages,
                    MaxTokens = 2048,
                    Model = AnthropicModels.Claude3Haiku,
                    Stream = false,
                    Temperature = temp,
                };

                MessageResponse firstResult = await _anthropicClient.Messages.GetClaudeMessageAsync(parameters);
                var claudeResponse = firstResult.Message.ToString();

                var isClaudeUnhappy = DidClaudeRefuse(claudeResponse);
                if (isClaudeUnhappy)
                {
                    stats.ClaudeRefusedCount++;
                    Logger.Log($"\t\tClaude was unhappy about\n\t\t\t{promptDetails.Show()}\n\t\t\t{claudeResponse}");
                    return new TaskProcessResult { IsSuccess = false, ErrorMessage = $"Claude was unhappy about the prompt and refused to rewrite it. {claudeResponse}", PromptDetails = promptDetails, TextGenerator = TextGeneratorApiType.Claude, GenericTextErrorType = GenericTextGenerationErrorType.RequestModerated,
                        ImageGeneratorDescription = "ClaudeGeneratorDescription",
                    };
                }
                else
                {
                    Logger.Log($"\t___Step:Claude____ => rewrote to: {claudeResponse}");
                    promptDetails.ReplacePrompt(claudeResponse, claudeResponse, TransformationType.ClaudeRewrite);
                    stats.ClaudeRewroteCount++;

                    return new TaskProcessResult { IsSuccess = true, ErrorMessage = "", PromptDetails = promptDetails, TextGenerator = TextGeneratorApiType.Claude,
                        ImageGeneratorDescription = "ClaudeGeneratorDescription",
                    };
                }
            }
            finally
            {
                _claudeSemaphore.Release();
            }
        }

        /// Spelling-only correction for the web UI's "fix spelling" button.
        /// Deliberately NOT a rewrite: the system prompt forbids rephrasing,
        /// and temperature 0 keeps the result deterministic. Returns the
        /// corrected text; throws on refusal or empty output (fail closed —
        /// the caller must never swap the user's prompt for garbage).
        public async Task<string> FixSpellingAsync(string text)
        {
            await _claudeSemaphore.WaitAsync();
            try
            {
                var parameters = new MessageParameters()
                {
                    System = new List<SystemMessage>
                    {
                        new SystemMessage(
                            "Fix spelling mistakes and obvious typos in the user's text. Return ONLY the corrected text, nothing else. "
                            + "Preserve the author's wording, word order, punctuation, capitalization style, line breaks, and formatting exactly, "
                            + "changing only misspelled words. Do not rephrase, do not add or remove content, do not correct grammar, "
                            + "and do not normalize slang or intentional stylization. If nothing needs fixing, return the input unchanged."),
                    },
                    Messages = new List<Message> { new Message(RoleType.User, text) },
                    MaxTokens = 4096,
                    Model = AnthropicModels.Claude3Haiku,
                    Stream = false,
                    Temperature = 0m,
                };

                var result = await _anthropicClient.Messages.GetClaudeMessageAsync(parameters);
                var corrected = result.Message.ToString();
                if (string.IsNullOrWhiteSpace(corrected))
                {
                    throw new InvalidOperationException("Claude returned an empty spelling correction.");
                }
                if (DidClaudeRefuse(corrected))
                {
                    stats.ClaudeRefusedCount++;
                    throw new InvalidOperationException($"Claude refused to correct this text: {corrected}");
                }
                return corrected;
            }
            finally
            {
                _claudeSemaphore.Release();
            }
        }

        public static IEnumerable<string> WordsClaudeHates =>
            System.IO.File.Exists("claude-bad.txt")
                ? System.IO.File.ReadAllLines("claude-bad.txt")
                    .OrderBy(el => el)
                    .Distinct()
                : Enumerable.Empty<string>();

        public static bool ClaudeWillHateThis(string prompt)
        {
            return WordsClaudeHates.Any(word => prompt.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }
}