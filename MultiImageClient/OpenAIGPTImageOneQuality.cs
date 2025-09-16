namespace MultiImageClient
{
    /// We only well-control the initial prompt text generation. The actual process of applying various steps, logging etc is all hardcoded in here which is not ideal.
    public enum OpenAIGPTImageOneQuality
    {
        auto = 1,
        low = 2,
        medium = 3,
        high = 4,
    }
}
