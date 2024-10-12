using System.Threading.Tasks;

namespace MultiClientRunner
{
    public interface IImageGenerationService
    {
        Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails, MultiClientRunStats stats);
    }
}