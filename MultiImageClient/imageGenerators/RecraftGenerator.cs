using IdeogramAPIClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RecraftAPIClient;

namespace MultiImageClient
{
    public class RecraftGenerator : AbstractImageGenerator, IImageGenerator
    {
        public ImageGeneratorApiType GetApiType => ImageGeneratorApiType.Recraft;
        private static Random random = new Random();

        public RecraftGenerator(IImageGenerationService svc, ImageGeneratorApiType imageGeneratorApiType) : base(svc, imageGeneratorApiType)
        {
        }

        private RecraftDetails GetDefaultRecraftDetails()
        {
            var details = new RecraftDetails
            {
                size = RecraftImageSize._1024x1536
            };
            details.style = "any";
            return details;
        }
        private RecraftDetails GetRandomRecraftStyleAndSubstyleDetails()
        {
            var details = new RecraftDetails
            {
                size = RecraftImageSize._1536x1024
            };

            // Randomly select one of the three main styles
            var styles = Enum.GetValues(typeof(RecraftStyle)).Cast<RecraftStyle>().ToList();
            details.style = styles[random.Next(styles.Count)].ToString();

            // Based on chosen style, select appropriate substyle

            bool coinFlip = true;

            switch (details.style)
            {
                case "digital_illustration":
                    var digitalStyles = Enum.GetValues(typeof(RecraftDigitalIllustrationSubstyles))
                        .Cast<RecraftDigitalIllustrationSubstyles>().ToList();
                    if (coinFlip)
                    {
                        details.substyle = digitalStyles[random.Next(digitalStyles.Count)].ToString();
                    }
                    break;

                case "realistic_image":
                    var realisticStyles = Enum.GetValues(typeof(RecraftRealisticImageSubstyles))
                        .Cast<RecraftRealisticImageSubstyles>().ToList();
                    if (coinFlip)
                    {
                        details.substyle = realisticStyles[random.Next(realisticStyles.Count)].ToString();
                    }
                    break;

                case "vector_illustration":
                    var vectorStyles = Enum.GetValues(typeof(RecraftVectorIllustrationSubstyles))
                        .Cast<RecraftVectorIllustrationSubstyles>().ToList();
                    if (coinFlip)
                    {
                        details.substyle = vectorStyles[random.Next(vectorStyles.Count)].ToString();
                    }
                    break;
            }

            return details;
        }

        public override async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails pd, MultiClientRunStats stats)
        {
            if (pd.RecraftDetails == null)
            {
                pd.RecraftDetails = GetRandomRecraftStyleAndSubstyleDetails();
                //pd.RecraftDetails = GetDefaultRecraftDetails();
            }
            
            Logger.Log($"{pd.Show()} Submitting to Recraft");
            var res = await _svc.ProcessPromptAsync(pd, stats);
            return res;
        }
    }
}
