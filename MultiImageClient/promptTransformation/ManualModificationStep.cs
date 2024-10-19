using System.Threading.Tasks;
using MultiImageClient.Implementation;

namespace MultiImageClient
{
    public class ManualModificationStep : ITransformationStep
    {
        public string Name => nameof(ManualModificationStep);
        private string Prefix { get; set; }
        private string Suffix { get; set; }
        public ManualModificationStep(string prefix, string suffix)
        {
            Prefix = prefix;
            Suffix = suffix;
        }

        public Task<bool> DoTransformation(PromptDetails pd, MultiClientRunStats stats)
        {
            var newp = $"{Prefix} {pd.Prompt} {Suffix}".Trim();
            pd.ReplacePrompt(newp, newp, TransformationType.ManualSuffixation);
            return Task.FromResult(true);
        }
    }
}
