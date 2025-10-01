using System.Threading.Tasks;

namespace MultiImageClient
{
    public interface ILocalVisionModel
    {
        Task<string> DescribeImageAsync(byte[] imageBytes, string prompt, int maxTokens = 512, float temperature = 0.8f);
        string GetModelName();
    }
}


