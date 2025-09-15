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
        public ImageGeneratorApiType GetApiType => ImageGeneratorApiType.Ideogram;

        public IdeogramGenerator(IImageGenerationService svc, ImageGeneratorApiType imageGeneratorApiType) : base(svc, imageGeneratorApiType)
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

            Logger.Log($"{pd.Show()} Submitting to Ideogram");
            var res = await _svc.ProcessPromptAsync(pd, stats);
            return res;
        }
    }
}
