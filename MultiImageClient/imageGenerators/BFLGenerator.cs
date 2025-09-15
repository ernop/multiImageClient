using IdeogramAPIClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class BFLGenerator : AbstractImageGenerator, IImageGenerator
    {
        public BFLGenerator(IImageGenerationService svc, ImageGeneratorApiType specificGenerator) : base(svc, specificGenerator)
        {
        }
        public override async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails pd, MultiClientRunStats stats)
        {
            //this is where we set up the details actually.
            if (GetApiType == ImageGeneratorApiType.BFLv11)
            { 
                var bflDetails = new BFL11Details
                {
                    Width = 1440,
                    Height = 1440, //1376
                    PromptUpsampling = true,
                    SafetyTolerance = 6,
                };
                pd.BFL11Details = bflDetails;
            }
            else if (GetApiType == ImageGeneratorApiType.BFLv11Ultra)
            {
                var bflDetails11Ultra = new BFL11UltraDetails
                {
                    AspectRatio = "1:1",
                    PromptUpsampling = true,
                    SafetyTolerance = 6,
                };
                pd.BFL11UltraDetails = bflDetails11Ultra;
            }
            else
            {
                Logger.Log("erro.?");
            }

            Logger.Log($"{pd.Index} Submitting to {GetApiType}");
            var res = await _svc.ProcessPromptAsync(pd, stats);
            return res;
        }
    }
}
