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
        public Dalle3Generator(IImageGenerationService svc) : base(svc)
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

            Logger.Log($"\t\tSubmitting to Dalle3: {pd.Prompt}");
            var res = await _svc.ProcessPromptAsync(pd, stats);
            return res;
        }
    }
}
