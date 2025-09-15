using System.Threading.Tasks;

namespace MultiImageClient
{
    public interface ITransformationStep
    {
        Task<bool> DoTransformation(PromptDetails pd);
        string Name { get; }
    }
}
