namespace MultiImageClient
{
    /// <summary>
    /// The annotation type on a saved image generated from a prompt via a number of steps.
    /// </summary>
    public enum SaveType
    {
        Raw = 1,
        FullAnnotation = 2,
        InitialIdea = 3,
        FinalPrompt = 4,
        JustOverride = 5, // when w generate the prompt sometimes we just have a core word/phrase called the which we want to make visible in both the filename and in this version with the subtitle for illustrative purposes. If you get one of these then just draw the text large, centered, in a nice font, with no other junk.
        Label = 6,
    }
}
