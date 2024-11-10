using System.Threading.Tasks;

namespace MultiImageClient
{
    public interface ITransformationStep
    {
        Task<bool> DoTransformation(PromptDetails pd, MultiClientRunStats stats);
        string Name { get; }
    }
}
