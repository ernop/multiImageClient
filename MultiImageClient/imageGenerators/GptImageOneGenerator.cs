using OpenAI.Images;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class GptImageOneGenerator : AbstractImageGenerator, IImageGenerator  
    {
        public ImageGeneratorApiType GetApiType => ImageGeneratorApiType.Dalle3;
        public GptImageOneGenerator(IImageGenerationService svc, ImageGeneratorApiType imageGeneratorApiType) : base(svc, imageGeneratorApiType)
        {
        }
        public override async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails pd, MultiClientRunStats stats)
        {
            var gptImageOneDetails = new GptImageOneDetails();
            pd.GptImageOneDetails= gptImageOneDetails;

            Logger.Log($"{pd.Show()} Submitting to GptImageOne");
            var res = await _svc.ProcessPromptAsync(pd, stats);
            return res;
        }
    }
}
