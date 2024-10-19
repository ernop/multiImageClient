namespace MultiImageClient
{
    /// a step along the pipeline of generating the prompt.
    /// The idea is that you generate these as you go along expanding/modifying the prompt, 
    /// and then at least have them to accompany the actual image output to know its history.
    public class PromptHistoryStep
    {
        public PromptHistoryStep(string newPrompt, string humanExplanation, TransformationType transformationType)
        {
            Prompt = newPrompt;
            Explanation = humanExplanation;
            TransformationType = transformationType;
        }
        public PromptHistoryStep(PromptHistoryStep other)
        {
            Prompt = other.Prompt;
            Explanation = other.Explanation;
            TransformationType = other.TransformationType;
        }

        /// You transformed the prompt somehow and got this new version!  This is always internal; when annotating, always use the explanation.
        public string Prompt { get; set; }

        /// Human description of what you atually did. For example, if you prompt a rewriter with an outer prompt + {prompt}, then put that text in, and the transformationType as Rewrite.
        /// Question: should I do the replacement here? or do that live. For example, I need a step to track "figuring out the correct aspect ratio to request" e.g. with LLAMA or whatever.
        public string Explanation { get; set; }
        public TransformationType TransformationType { get; set; }

        //a method to make mousing over this object show the description and details:
        public override string ToString()
        {
            return $"{TransformationType} {Explanation}";
        }
    }
}

