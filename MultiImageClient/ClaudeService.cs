using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;

namespace MultiClientRunner
{
    public class ClaudeService
    {
        private readonly AnthropicClient _anthropicClient;
        private readonly SemaphoreSlim _claudeSemaphore;

        public ClaudeService(string apiKey, int maxConcurrency)
        {
            var anthropicApikeyAuth = new APIAuthentication(apiKey);
            _anthropicClient = new AnthropicClient(anthropicApikeyAuth);
            _claudeSemaphore = new SemaphoreSlim(maxConcurrency);
        }

        ///Claude gets mad when you ask it to do ceratin things.
        internal static bool CheckClaudeUnhappiness(string claudeResponse)
        {
            var unhappyClaudeResponses = new List<string>
            {
                "i'm sorry, i can't",
                "sexualized",
                "i will not produce",
                "harmful stereotypes",
                "i apologize",
                "don't feel comfortable",
                "that is overtly", 
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

        public async Task<string> RewritePromptAsync(string prompt, MultiClientRunStats stats, decimal tempterature)
        {
            await _claudeSemaphore.WaitAsync();
            try
            {

                var messages = new List<Message>()
                {
                    new Message(RoleType.User, prompt),
                };

                var parameters = new MessageParameters()
                {
                    Messages = messages,
                    MaxTokens = 1024,
                    Model = AnthropicModels.Claude3Opus,
                    Stream = false,
                    Temperature = tempterature,
                };

                stats.ClaudeRequestCount++;
                MessageResponse firstResult = await _anthropicClient.Messages.GetClaudeMessageAsync(parameters);

                return firstResult.Message.ToString();
            }
            finally
            {
                _claudeSemaphore.Release();
            }
        }

        public static IEnumerable<string> WordsClaudeHates => 
            System.IO.File.Exists("claude-bad.txt")
                ? System.IO.File.ReadAllLines("claude-bad.txt")
                    .Select(el => el.Trim())
                    .OrderBy(el => el)
                    .Distinct()
                : Enumerable.Empty<string>();

        public static bool ClaudeWillHateThis(string prompt)
        {
            return WordsClaudeHates.Any(word => prompt.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }
}