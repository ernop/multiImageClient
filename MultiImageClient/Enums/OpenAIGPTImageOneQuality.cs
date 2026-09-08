namespace MultiImageClient
{
    /// We only well-control the initial prompt text generation. The actual process of applying various steps, logging etc is all hardcoded in here which is not ideal.
    public enum OpenAIGPTImageOneQuality
    {
        auto = 1,
        low = 2,
        medium = 3,
        high = 4,
        // gpt-image-2.5 (Sunburst/Flare, released 2026-09-08) adds two tiers
        // above high. Earlier GPT Image models reject these values; the
        // generators validate quality against the configured model id.
        xhigh = 5,
        max = 6,
    }
}
