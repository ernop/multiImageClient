using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace MultiImageClient
{
    public class ClaudeRewriteStep : ITransformationStep
    {
        //var claudeStep = new ClaudeRewriteStep("Please take the following topic and make it specific; cast the die, take a chance, and expand it to a longer, detailed, specific description of a scene with all the elements of it described. Describe how the thing looks, feels, appears, etc in high detail. Put the most important aspects first such as the overall description, then continue by expanding that and adding more detail, structure, theme. Be specific in whatevr you do. If it seems appropriate, if a man appears don't just say 'the man', but instead actually give him a name, traits, personality, etc. The goal is to deeply expand the world envisioned by the original topic creator. Overall, follow the implied theem and goals of the creator, but just expand it into much more specifics and concreate actualization. Never use phrases or words like 'diverse', 'vibrant' etc. Be very concrete and precise in your descriptions, similar to how ansel adams describing a new treasured species of bird would - detailed, caring, dense, clear, sharp, speculative and never wordy or fluffy. every single word you say must be relevant to the goal of increasing the info you share about this image or sitaution or scene. Be direct and clear.", "", claudeService, 0.4m, stats);

        private string _prefix { get; set; }
        private string _suffix { get; set; }
        private ClaudeService _claudeService { get; set; }
        private decimal _temperature { get; set; }
        private MultiClientRunStats _stats { get; set; }

        public string Name => nameof(ClaudeRewriteStep);

        /// you need to put the instructions to claude into the prefix and/or suffix.  generally things like "based on this idea, expand it quite a bit"
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
            var preparedClaudePrompt = $"{_prefix}\nHere is the topic:\n'{pd.Prompt}'\nPlease reply in a paragraph with no line breaks at all, just a single unified paragraph text.{_suffix}".Trim();
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
