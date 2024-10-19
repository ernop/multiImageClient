using System.Threading.Tasks;

namespace MultiImageClient.Implementation
{
    public interface IImageGenerationService
    {
        Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails, MultiClientRunStats stats);
    }
}