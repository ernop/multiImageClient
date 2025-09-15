using OpenAI.Images;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class Dalle3Generator : AbstractImageGenerator, IImageGenerator  
    {
        public ImageGeneratorApiType GetApiType => ImageGeneratorApiType.Dalle3;
        public Dalle3Generator(IImageGenerationService svc, ImageGeneratorApiType imageGeneratorApiType) : base(svc, imageGeneratorApiType)
        {
        }
        public override async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails pd, MultiClientRunStats stats)
        {
            var dalle3Details = new Dalle3Details
            {
                Model = "dall-e-3",
                Size = GeneratedImageSize.W1024xH1024,
                Quality = GeneratedImageQuality.High,
                Format = GeneratedImageFormat.Uri
            };
            pd.Dalle3Details = dalle3Details;

            Logger.Log($"{pd.Show()} Submitting to Dalle3");
            var res = await _svc.ProcessPromptAsync(pd, stats);
            return res;
        }
    }
}
