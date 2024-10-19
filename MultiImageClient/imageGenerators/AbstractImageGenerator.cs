using System.Threading.Tasks;
using MultiImageClient.Implementation;

namespace MultiImageClient
{
    public abstract class AbstractImageGenerator
    {
        internal IImageGenerationService _svc { get; set; }
        public AbstractImageGenerator(IImageGenerationService svc)
        {
            _svc = svc;
        }
        public abstract Task<TaskProcessResult> ProcessPromptAsync(PromptDetails pd, MultiClientRunStats stats);
    }
}
