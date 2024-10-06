using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;

namespace MultiClientRunner
{
    public class ClaudeService
    {
        private readonly AnthropicClient _anthropicClient;
        public ClaudeService(AnthropicClient anthropicClient)
        {
            _anthropicClient = anthropicClient ?? throw new ArgumentNullException(nameof(anthropicClient));

        }

        public async Task<string> RewritePromptAsync(string prompt, MultiClientRunStats stats)
        {
            var preparedClaudePrompt = $"Rewrite this image generation prompt to be much better and more detailed with interesting obscure details, unified themes and brilliance. {prompt}";
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
    }
}