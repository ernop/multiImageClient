using System.Threading.Tasks;

namespace MultiImageClient
{
    public abstract class AbstractImageGenerator
    {
        internal IImageGenerationService _svc { get; set; }
        public ImageGeneratorApiType GetApiType { get; set; }

        public AbstractImageGenerator(IImageGenerationService svc, ImageGeneratorApiType getApiType)
        {
            _svc = svc;
            GetApiType = getApiType;
        }
        public abstract Task<TaskProcessResult> ProcessPromptAsync(PromptDetails pd, MultiClientRunStats stats);
    }
}
