using IdeogramAPIClient;

using OpenAI.Images;

using RecraftAPIClient;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class GeneratorGroups
    {
        private Settings _settings;
        private int _concurrency;
        private MultiClientRunStats _stats;

        public GeneratorGroups(Settings settings, int concurrency, MultiClientRunStats stats)
        {
            _settings = settings;
            _concurrency = concurrency;
            _stats = stats;
        }
        public IEnumerable<IImageGenerator> GetAll()
        {
            var dalle3 = new Dalle3Generator(_settings.OpenAIApiKey, _concurrency, GeneratedImageQuality.High, GeneratedImageSize.W1024xH1024, _stats, "");
            var recraft1 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.hard_comics, null, _stats, "");
            var recraft2 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.bold_fantasy, null, _stats, "");
            var recraft3 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.freehand_details, null, _stats, "");
            var recraft4 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.studio_portrait, _stats, "");
            var recraft5 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, RecraftStyle.vector_illustration, RecraftVectorIllustrationSubstyle.line_art, null, null, _stats, "");
            var recraft6 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.real_life_glow, _stats, "");
            var recraft7 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.bold_fantasy, null, _stats, "");
            var recraft8 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.retro_realism, _stats, "");

            var ideogram1 = new IdeogramGenerator(_settings.IdeogramApiKey, _concurrency, IdeogramMagicPromptOption.ON, IdeogramAspectRatio.ASPECT_16_10, IdeogramStyleType.DESIGN, "", IdeogramModel.V_2, _stats, "");
            var ideogram2 = new IdeogramGenerator(_settings.IdeogramApiKey, _concurrency, IdeogramMagicPromptOption.OFF, IdeogramAspectRatio.ASPECT_1_1, null, "", IdeogramModel.V_2_TURBO, _stats, "");
            var ideogram3 = new IdeogramGenerator(_settings.IdeogramApiKey, _concurrency, IdeogramMagicPromptOption.ON, IdeogramAspectRatio.ASPECT_4_3, null, "", IdeogramModel.V_2A, _stats, "");
            var ideogram4 = new IdeogramGenerator(_settings.IdeogramApiKey, _concurrency, IdeogramMagicPromptOption.OFF, IdeogramAspectRatio.ASPECT_4_3, null, "", IdeogramModel.V_2A_TURBO, _stats, "");
            var bfl1 = new BFLGenerator(ImageGeneratorApiType.BFLv11, _settings.BFLApiKey, _concurrency, "3:2", false, 1024, 1024, _stats, "");
            var bfl2 = new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra, _settings.BFLApiKey, _concurrency, "1:1", false, 1024, 1024, _stats, "");
            var bfl3 = new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra, _settings.BFLApiKey, _concurrency, "3:2", false, 1024, 1024, _stats, "");

            var gptimage1 = new GptImageOneGenerator(_settings.OpenAIApiKey, _concurrency, "1024x1024", "low", OpenAIGPTImageOneQuality.high, _stats, "");

            //var myGenerators = new List<IImageGenerator>() { dalle3, ideogram2, bfl1, bfl2, bfl3, recraft6, ideogram4, };
            //var myGenerators = new List<IImageGenerator>() { dalle3, recraft1, recraft2, recraft3, recraft4, recraft5, recraft6, ideogram1, ideogram2, bfl1, bfl2 };
            var myGenerators = new List<IImageGenerator>() { gptimage1, ideogram4, dalle3, recraft6, recraft5, recraft8, bfl1, bfl2, bfl3 };
            return myGenerators;
        }

        public IEnumerable<IImageGenerator> GetAllStylesOfRecraft()
        {
            var res = new List<IImageGenerator>();
            var styles = Enum.GetValues(typeof(RecraftStyle)).Cast<RecraftStyle>().ToList();
            var lim = 4000;
            foreach (var style in styles)
            {
                var ii = 0;
                
                if (style == RecraftStyle.digital_illustration)
                {
                    var substyles = Enum.GetValues(typeof(RecraftDigitalIllustrationSubstyle)).Cast<RecraftDigitalIllustrationSubstyle>();
                    
                    foreach (var substyle in substyles)
                    {
                        ii++;
                        var gen = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, style, null, substyle, null, _stats, $"");
                        res.Add(gen);
                        if (ii > lim) { break; }
                        //var gen2 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, style, null, substyle, null, _stats, $"");
                        //res.Add(gen2);
                    }
                }
                else if (style == RecraftStyle.realistic_image)
                {
                    var substyles = Enum.GetValues(typeof(RecraftRealisticImageSubstyle)).Cast<RecraftRealisticImageSubstyle>().ToList();
                    ii = 0;
                    foreach (var substyle in substyles)
                    {
                        ii++;
                        var gen = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, style, null, null, substyle, _stats, $"");
                        res.Add(gen);
                        if (ii > lim) { break; }
                        //var gen2 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, style, null, null, substyle, _stats, $"");
                        //res.Add(gen2);
                    }
                }
                else if (style == RecraftStyle.vector_illustration)
                {
                    var substyles = Enum.GetValues(typeof(RecraftVectorIllustrationSubstyle)).Cast<RecraftVectorIllustrationSubstyle>().ToList();
                    ii = 0;
                    foreach (var substyle in substyles)
                    {
                        ii++;
                        var gen = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, style, substyle, null, null, _stats, $"");
                        res.Add(gen);
                        if (ii > lim) { break; }
                        //var gen2 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, style, substyle, null, null, _stats, $"");
                        //res.Add(gen2);
                    }
                }
            }

            return res;
            
        }

    }
}
