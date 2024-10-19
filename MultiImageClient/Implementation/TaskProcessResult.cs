using IdeogramAPIClient;
using MultiImageClient.Enums;

namespace MultiImageClient
{
    /// Response type for async text rewrites or image generation steps.
    public class TaskProcessResult
    {
        public bool IsSuccess { get; set; }
        public GenericImageGenerationErrorType GenericImageErrorType { get; set; } = 0;
        public GenericTextGenerationErrorType GenericTextErrorType { get; set; } = 0;
        
        public string ErrorMessage { get; set; }
        public string Url { get; set; }

        //The original request data and all changes etc.
        public PromptDetails PromptDetails { get; set; }
        public ImageGeneratorApiType ImageGenerator { get; set; }
        public TextGeneratorApiType TextGenerator { get; set; }

        public override string ToString()
        {
            if (GenericImageErrorType != 0)
            {
                return $"Error: {GenericImageErrorType} {ErrorMessage}";
            }
            if (GenericTextErrorType != 0)
            {
                return $"Error: {GenericTextErrorType} {ErrorMessage}";
            }
            return $"Success. {PromptDetails}";
        }
    }
}