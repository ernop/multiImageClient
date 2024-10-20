using System.Threading.Tasks;

namespace MultiImageClient
{
    public interface IImageGenerationService
    {
        Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails, MultiClientRunStats stats);
    }
}