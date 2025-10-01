using MultiImageClient;

using System;
using System.Collections.Generic;
using System.Linq;



namespace MultiImageClient
{
    public class EmotionFaces : AbstractPromptSource
    {
        public EmotionFaces(Settings settings) : base(settings)
        {
        }

        public override string Name => "Paintings";
        public override string Prefix => "";
        public override int ImageCreationLimit => 30;
        public override int CopiesPer => 1;
        public override bool RandomizeOrder => false;
        public override int FullyResolvedCopiesPer => 2;
        public override string Suffix => "";
        

        private IEnumerable<PromptDetails> GetPrompts()
        {
            var emotions = "a smiling, a frowning, a scowling, a puzzled, an angry, an anxious, a worried, a surprised, a shocked, an annoyed, an amused, a happy, a sad, a confused, a thoughtful, a stern, an excited, an exhausted, a tired, a nervous, an upset, a serious, a concerned, a distant, an innocent, an irritated, a hopeful, a determined, a peaceful, a vacant".Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var emotion in emotions)
            {
                var pd = new PromptDetails();
                var prompt = $"A clear, very sharp, high resolution, artistic photograph of a close up of just the face of a 30 year old white middle class university lecturer in Biology from Florida, working at UNC, at a staff post-term party reception, standing at a table, with  {emotion} expression on his face. He has brown hair, a t-shirt and the reception takes place in on the rooftop garden in the the old mathematics building where they are eating hot dogs and burgers and watching the game. he is not handsome nor manly. He is a passable lecturer only; his brilliance at reading does not come out in his slow speech. He is not attractive. He doesn't have glasses. He is of average build, looks like a typical early career lecturer. His father was swiss and his mother is french so he looks fairly western european in face. He is clean shaven and has an ill-defined jaw.  Only has face is visible looking almost directly at the camera close up framing his face from forehead to chin, ear to ear. The rest of his body is not visible. He has no beard or stubble or moustache, his face is smooth. He is engaged in an intense discussion with a colleague.";
                pd.ReplacePrompt(prompt, prompt, TransformationType.InitialPrompt);
                var theSplit = emotion.Split(' ', 2);
                yield return pd;
            }

        }
        public override IEnumerable<PromptDetails> Prompts => GetPrompts();

    }
}
