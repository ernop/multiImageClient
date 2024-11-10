namespace MultiImageClient
{
    public enum TransformationType
    {
        InitialPrompt = 1,
        Variants = 2,
        InitialExpand = 3,
         

        ClaudeRewrite = 20,
        ClauedeRewriteRequest = 21,
        ClaudeWouldRefuseRewrite = 22,
        ClaudeDidRefuseRewrite = 23,

        TextToImage = 30,
        ImageToText = 40,
        FigureOutAR = 50,
        IdeogramRewrite = 60,
        BFLRewrite = 70,
        LLAmARewrite = 80,

        Dalle3Rewrite = 90,

        Randomizer = 100,

        ManualSuffixation = 110,
        
        AddingArtistStyle = 120,
    }
}

