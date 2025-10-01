using MultiImageClient;

using System;
using System.Collections.Generic;
using System.Linq;



namespace MultiImageClient
{
    public class ScenesFromStory : AbstractPromptSource
    {
        public ScenesFromStory(Settings settings) : base(settings)
        {
        }

        public override string Name => "Scenes from Equinoctal";
        public override string Prefix => "";
        public override int ImageCreationLimit => 300;
        public override int CopiesPer => 3;
        public override bool RandomizeOrder => true;
        public override int FullyResolvedCopiesPer => 1;
        public override string Suffix => "";
        

        private IEnumerable<PromptDetails> GetPrompts()
        {
            var rawText = System.IO.File.ReadAllText("d:\\proj\\make-audio\\input\\equinoctal.clean.txt");
            var pd = new PromptDetails();
            pd.ReplacePrompt(rawText, "the full text of the story", TransformationType.InitialPrompt);
            yield return pd;
        }
        public override IEnumerable<PromptDetails> Prompts => GetPrompts().OrderBy(el=>Random.Shared.Next());
    }
}
