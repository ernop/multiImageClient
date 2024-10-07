using System;
using System.Collections.Generic;
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

        public async Task<string> RewritePromptAsync(string prompt, MultiClientRunStats stats)
        {
            await _claudeSemaphore.WaitAsync();
            try
            {
                var preparedClaudePrompt = $"Rewrite this image generation prompt to be much better and more detailed with interesting obscure details, unified themes and brilliance. {prompt}";
                preparedClaudePrompt = $"Help the user expand this kernel of an idea into a fully detailed image description, carefully following the style they seem to be indicating they are interested in. Output the description. '{prompt}'";
                var messages = new List<Message>()
                {
                    new Message(RoleType.User, preparedClaudePrompt),
                };

                var myTemp = 1.0m;
                var parameters = new MessageParameters()
                {
                    Messages = messages,
                    MaxTokens = 1024,
                    Model = AnthropicModels.Claude35Sonnet,
                    Stream = false,
                    Temperature = myTemp,
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
    }
}