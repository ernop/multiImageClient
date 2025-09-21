using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;

using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;


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
                    return new TaskProcessResult { IsSuccess = false, ErrorMessage = $"Claude was unhappy about the prompt and refused to rewrite it. {claudeResponse}", PromptDetails = promptDetails, TextGenerator = TextGeneratorApiType.Claude, GenericTextErrorType = GenericTextGenerationErrorType.RequestModerated };
                }
                else
                {
                    Logger.Log($"\t___Step:Claude____ => rewrote to: {claudeResponse}");
                    promptDetails.ReplacePrompt(claudeResponse, claudeResponse, TransformationType.ClaudeRewrite);
                    stats.ClaudeRewroteCount++;

                    return new TaskProcessResult { IsSuccess = true, ErrorMessage = "", PromptDetails = promptDetails, TextGenerator = TextGeneratorApiType.Claude };
                }
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