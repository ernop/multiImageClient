using System;
using System.Threading.Tasks;

namespace IdeogramAPIClient
{
    public class Program
    {
        static async Task Main(string[] args)
        {
            var apiKey = "";
            var client = new IdeogramClient(apiKey);
            var prompt = "An image of a man name Dr. John Ideogram. He specializes in ancient ideograms and is at work in a recently discovered tomb of the ancient scholarly school of ideogramography. In the foreground is an inset close-up image of a previously unknown style of ideogram.";
            var ideogramDetails = new IdeogramDetails
            {
                AspectRatio = IdeogramAspectRatio.ASPECT_2_3,
                Model = IdeogramModel.V_2,
                MagicPromptOption = IdeogramMagicPromptOption.ON,
                StyleType = IdeogramStyleType.GENERAL
            };
            var req = new IdeogramGenerateRequest(prompt,ideogramDetails);
            client.GenerateImageAsync(req).Wait();
        }
    }
}