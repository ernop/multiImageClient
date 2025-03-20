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
        public ImageGeneratorApiType GetApiType => ImageGeneratorApiType.BFL;
        public BFLGenerator(IImageGenerationService svc) : base(svc)
        {
        }
        public override async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails pd, MultiClientRunStats stats)
        {
            var bflDetails = new BFLDetails
            {
                Width = 1440,
                Height = 1440, //1376
                PromptUpsampling = false,
                SafetyTolerance = 6,
            };
            pd.BFLDetails = bflDetails;
            Logger.Log($"\tSubmitting to BFL: {pd.Prompt}");
            var res = await _svc.ProcessPromptAsync(pd, stats);
            return res;
        }
    }
}
