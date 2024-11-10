using IdeogramAPIClient;
using OpenAI.Images;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{

    public class IdeogramGenerator : AbstractImageGenerator, IImageGenerator
    {
        public IdeogramGenerator(IImageGenerationService svc) : base(svc)
        {
        }

        public override async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails pd, MultiClientRunStats stats)
        {
            var ideogramDetails = new IdeogramDetails
            {
                AspectRatio = IdeogramAspectRatio.ASPECT_1_1,
                Model = IdeogramModel.V_2,
                MagicPromptOption = IdeogramMagicPromptOption.OFF,
                StyleType = IdeogramStyleType.GENERAL,
            };
            pd.IdeogramDetails = ideogramDetails;

            Console.WriteLine($"\t\tSubmitting to Ideogram: {pd.Prompt}");
            var res = await _svc.ProcessPromptAsync(pd, stats);
            return res;
        }
    }
}
