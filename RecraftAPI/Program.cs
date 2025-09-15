using CommandLine;

using MultiImageClient;

using System;
using System.Threading.Tasks;

namespace RecraftAPIClient
{
    public class Program
    {
        static async Task<int> Main(string[] args)
        {
            var details = new RecraftDetails()
            {
                size = RecraftImageSize._1707x1024,
                style = RecraftStyle.digital_illustration.ToString()
                //substyle = RecraftVectorIllustrationSubstyles.
                //substyle = RecraftVectorIllustrationSubstyles.cosmics.ToString(),
            };

            var cli = new RecraftClient("ly990g6UShz0ODtjMoqUnpOqfQF935fG3dEjq5kHLMrF18EojUxw3FfOin3Xyrib");
            var res = await cli.GenerateImageAsync("A cute puppy playing in a garden", details);
            res.Data.ForEach(x => Console.WriteLine(x.Url));
            return 0;
        }
    }
    //does not have a suitable main method? 
}
