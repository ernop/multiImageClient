namespace MultiImageClient
{
    public class PromptReplacementMetadata
    {
        public decimal ClaudeTemp { get; set; }
        public PromptReplacementMetadata Copy()
        {
            return new PromptReplacementMetadata
            {
                ClaudeTemp = ClaudeTemp
            };
        }
    }
}
