using System.Collections.Generic;

namespace MultiImageClient
{
    /// A prompt source that yields exactly one prompt supplied at
    /// construction time. Used by --prompt "..." for non-interactive runs.
    public class InlinePromptSource : AbstractPromptSource
    {
        private readonly string _prompt;

        public InlinePromptSource(Settings settings, string prompt) : base(settings)
        {
            _prompt = prompt;
        }

        public override string Name => nameof(InlinePromptSource);
        public override int ImageCreationLimit => 1;
        public override int CopiesPer => 1;
        public override int FullyResolvedCopiesPer => 1;
        public override bool RandomizeOrder => false;
        public override string Prefix => "";
        public override string Suffix => "";

        public override IEnumerable<PromptDetails> Prompts
        {
            get
            {
                var pd = new PromptDetails();
                pd.ReplacePrompt(_prompt, _prompt, TransformationType.InitialPrompt);
                yield return pd;
            }
        }
    }
}
