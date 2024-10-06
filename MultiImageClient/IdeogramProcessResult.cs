using IdeogramAPIClient;

namespace MultiClientRunner
{
    public class IdeogramProcessResult
    {
        public GenerateResponse Response { get; set; }
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }
    }
}