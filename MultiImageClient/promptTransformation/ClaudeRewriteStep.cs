using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace MultiImageClient
{
    public class ClaudeRewriteStep : ITransformationStep
    {
        private string _prefix { get; set; }
        private string _suffix { get; set; }
        private ClaudeService _claudeService { get; set; }
        private decimal _temperature { get; set; }
        private MultiClientRunStats _stats { get; set; }

        public string Name => nameof(ClaudeRewriteStep);

        public ClaudeRewriteStep(string prefix, string suffix, ClaudeService svc, decimal temperature, MultiClientRunStats stats)
        {
            _claudeService = svc;
            _prefix = prefix;
            _suffix = suffix;
            _temperature = temperature;
            _stats = stats;
        }

        public async Task<bool> DoTransformation(PromptDetails pd)
        {
            var preparedClaudePrompt = $"{_prefix}\nHere is the topic:\n'{pd.Prompt}'\n{_suffix}".Trim();
            var preparedClaudePromptWithTemp = $"temp={_temperature} {preparedClaudePrompt}";
            pd.ReplacePrompt(preparedClaudePrompt, preparedClaudePromptWithTemp, TransformationType.ClaudeRewriteRequest);

            var response = await _claudeService.RewritePromptAsync(pd, _temperature);
            if (!response.IsSuccess)
            {
                Logger.Log($"\tClaude fail so reverting FROM: {pd.Show()}");
                pd.UndoLastStep();
                //pd.UndoLastStep();
                Logger.Log($"\tClaude fail so reverted TO: {pd.Show()}");
                pd.ReplacePrompt(pd.Prompt, response.ErrorMessage, TransformationType.ClaudeDidRefuseRewrite);
                
                return false;
            }
            return true;
        }
    }
}
