using IdeogramAPIClient;

namespace MultiClientRunner
{
    public class TaskProcessResult
    {
        public GenerateResponse Response { get; set; }
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }
        public string Url { get; set; }
        
        //The original request data and all changes etc.
        public PromptDetails PromptDetails { get; set; }
        public GeneratorApiType Generator { get; set; }
    }

    public enum GeneratorApiType
    {
        Midjourney = 1,
        Dalle3 = 2,
        Ideogram = 3,
        BFL = 4,
    }
}