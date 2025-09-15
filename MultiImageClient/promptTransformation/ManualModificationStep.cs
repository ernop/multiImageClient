using System.Threading.Tasks;

namespace MultiImageClient
{
    public class ManualModificationStep : ITransformationStep
    {
        public string Name => nameof(ManualModificationStep);
        private string Prefix { get; set; }
        private string Suffix { get; set; }
        private MultiClientRunStats _stats { get; set; }
        public ManualModificationStep(string prefix, string suffix, MultiClientRunStats stats)
        {
            Prefix = prefix;
            Suffix = suffix;
            _stats = stats;
        }

        public Task<bool> DoTransformation(PromptDetails pd)
        {
            var newp = $"{Prefix} {pd.Prompt} {Suffix}".Trim();
            pd.ReplacePrompt(newp, newp, TransformationType.ManualSuffixation);
            return Task.FromResult(true);
        }
    }
}
