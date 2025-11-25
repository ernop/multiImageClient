using IdeogramAPIClient;

using OpenAI.Images;

using RecraftAPIClient;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.JavaScript;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

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
            var dalle3wide = new Dalle3Generator(_settings.OpenAIApiKey, _concurrency, GeneratedImageQuality.High, GeneratedImageSize.W1792xH1024, _stats, "");
            var recraft1 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.hard_comics, null, _stats, "");
            var recraft2 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.bold_fantasy, null, _stats, "");
            var recraft3 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.freehand_details, null, _stats, "");
            var recraft4 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.studio_portrait, _stats, "");
            var recraft5 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, RecraftStyle.vector_illustration, RecraftVectorIllustrationSubstyle.line_art, null, null, _stats, "");
            var recraft6 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.real_life_glow, _stats, "");
            var recraft7 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.bold_fantasy, null, _stats, "");
            var recraft8 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.organic_calm, _stats, "");
            var recraft9 = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.organic_calm, _stats, "", "5");
            var recraft_any = new RecraftGenerator(_settings.RecraftApiKey, _concurrency, RecraftImageSize._1365x1024, RecraftStyle.any, null, null, null, _stats, "");
            var ideogram1 = new IdeogramGenerator(_settings.IdeogramApiKey, _concurrency, IdeogramMagicPromptOption.ON, IdeogramAspectRatio.ASPECT_16_10, IdeogramStyleType.DESIGN, "", IdeogramModel.V_2, _stats, "");
            var ideogram2 = new IdeogramGenerator(_settings.IdeogramApiKey, _concurrency, IdeogramMagicPromptOption.OFF, IdeogramAspectRatio.ASPECT_1_1, null, "", IdeogramModel.V_2_TURBO, _stats, "");
            var ideogram3 = new IdeogramGenerator(_settings.IdeogramApiKey, _concurrency, IdeogramMagicPromptOption.ON, IdeogramAspectRatio.ASPECT_1_1, null, "", IdeogramModel.V_2A, _stats, "");
            var ideogram4 = new IdeogramGenerator(_settings.IdeogramApiKey, _concurrency, IdeogramMagicPromptOption.OFF, IdeogramAspectRatio.ASPECT_4_3, null, "", IdeogramModel.V_2A_TURBO, _stats, "");
            var ideogramV3 = new IdeogramV3Generator(_settings.IdeogramApiKey, _concurrency, IdeogramV3StyleType.AUTO, IdeogramMagicPromptOption.ON, IdeogramAspectRatio.ASPECT_16_10, IdeogramRenderingSpeed.QUALITY, "", _stats, "" );
            var bfl1 = new BFLGenerator(ImageGeneratorApiType.BFLv11, _settings.BFLApiKey, _concurrency, "3:2", false, 1024, 1024, _stats, "");
            var bfl2 = new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra, _settings.BFLApiKey, _concurrency, "1:1", false, 1024, 1024, _stats, "");
            var bfl3 = new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra, _settings.BFLApiKey, _concurrency, "3:2", true, 1024, 1024, _stats, "");

            // A new type of coding has just been invented!  It's jsut as revolutionary as this cool thing "Vibe Coding"! This new one is called "Brain Coding!" The difference is that you think first, using your brain, then type out the code and debut it yourself!  (((Illustrate this revolutionary new coding workflow! You have to include captions, the inventor, and the image should illustrate clearly what it is and how it works, like a New Yorker cartoon with a funny punchline which also hits you right in the feels!  People who are so well-educated that they can laugh at anything!)))

            var gptimage1_1 = new GptImageOneGenerator(_settings.OpenAIApiKey, _concurrency, "1024x1024", "low", OpenAIGPTImageOneQuality.high, ImageGeneratorApiType.GptImage1, _stats, "");
            var gptimage1_2 = new GptImageOneGenerator(_settings.OpenAIApiKey, _concurrency, "1024x1536", "low", OpenAIGPTImageOneQuality.auto, ImageGeneratorApiType.GptImage1, _stats, "");
            var gptimagemini1_1 = new GptImageOneGenerator(_settings.OpenAIApiKey, _concurrency, "1536x1024", "low", OpenAIGPTImageOneQuality.high, ImageGeneratorApiType.GptImage1Mini,_stats, "");
            var gptimagemini1_2 = new GptImageOneGenerator(_settings.OpenAIApiKey, _concurrency, "1024x1024", "low", OpenAIGPTImageOneQuality.auto, ImageGeneratorApiType.GptImage1Mini, _stats, "");
            var gptimagemini1_3 = new GptImageOneGenerator(_settings.OpenAIApiKey, _concurrency, "1024x1536", "low", OpenAIGPTImageOneQuality.high, ImageGeneratorApiType.GptImage1Mini, _stats, "");

            //var myGenerators = new List<IImageGenerator>() { dalle3, ideogram2, bfl1, bfl2, bfl3, recraft6, ideogram4, };
            //var myGenerators = new List<IImageGenerator>() { dalle3, recraft1, recraft2, recraft3, recraft4, recraft5, recraft6, ideogram1, ideogram2, bfl1, bfl2 };

            // Google Gemini/Nano Banana generators with various resolutions
            var google_banana = new GoogleGenerator(ImageGeneratorApiType.GoogleNanoBanana, _settings.GoogleGeminiApiKey, _concurrency, _stats, 
                imageSize: GoogleImageSize.Size1K, aspectRatio: GoogleImageAspectRatio.Ratio1x1);
            var google_banana_2k = new GoogleGenerator(ImageGeneratorApiType.GoogleNanoBanana, _settings.GoogleGeminiApiKey, _concurrency, _stats, 
                name: "banana-2k", imageSize: GoogleImageSize.Size2K, aspectRatio: GoogleImageAspectRatio.Ratio16x9);
            var google_banana_4k = new GoogleGenerator(ImageGeneratorApiType.GoogleNanoBanana, _settings.GoogleGeminiApiKey, _concurrency, _stats, 
                name: "banana-4k", imageSize: GoogleImageSize.Size4K, aspectRatio: GoogleImageAspectRatio.Ratio1x1);
            
            // Google Imagen 4 generators (max 2K, no 4K support) - using options class
            var imagen4Options1k = new GoogleImageGenerationOptions
            {
                ImageSize = GoogleImageSize.Size1K,
                AspectRatio = GoogleImageAspectRatio.Ratio16x9,
                SafetyFilterLevel = GoogleSafetyFilterLevel.BlockNone,
                PersonGeneration = GooglePersonGeneration.AllowAll
            };
            var googleimagen = new GoogleImagen4Generator(_settings.GoogleGeminiApiKey, _concurrency, _stats, "", 
                location: _settings.GoogleCloudLocation, projectId: _settings.GoogleCloudProjectId, 
                googleServiceAccountKeyPath: _settings.GoogleServiceAccountKeyPath,
                options: imagen4Options1k);
            
            var imagen4Options2k = new GoogleImageGenerationOptions
            {
                ImageSize = GoogleImageSize.Size2K,
                AspectRatio = GoogleImageAspectRatio.Ratio16x9,
                SafetyFilterLevel = GoogleSafetyFilterLevel.BlockNone,
                PersonGeneration = GooglePersonGeneration.AllowAll
            };
            var googleimagen_2k = new GoogleImagen4Generator(_settings.GoogleGeminiApiKey, _concurrency, _stats, "imagen4-2k", 
                location: _settings.GoogleCloudLocation, projectId: _settings.GoogleCloudProjectId, 
                googleServiceAccountKeyPath: _settings.GoogleServiceAccountKeyPath,
                options: imagen4Options2k);
            
            // Example with JPEG output and deterministic seed
            var imagen4OptionsJpeg = new GoogleImageGenerationOptions
            {
                ImageSize = GoogleImageSize.Size2K,
                AspectRatio = GoogleImageAspectRatio.Ratio3x2,
                OutputMimeType = GoogleOutputMimeType.Jpeg,
                CompressionQuality = 90,
                SafetyFilterLevel = GoogleSafetyFilterLevel.BlockOnlyHigh,
                Seed = 12345  // Deterministic output
            };
            var googleimagen_jpeg = new GoogleImagen4Generator(_settings.GoogleGeminiApiKey, _concurrency, _stats, "imagen4-jpeg",
                location: _settings.GoogleCloudLocation, projectId: _settings.GoogleCloudProjectId,
                googleServiceAccountKeyPath: _settings.GoogleServiceAccountKeyPath,
                options: imagen4OptionsJpeg);
            //recraft8, recraft9, 

            var myGenerators = new List<IImageGenerator>() { };
            myGenerators = new List<IImageGenerator>() { dalle3, ideogram1, ideogram2, ideogram3, ideogram4, ideogramV3, recraft1, recraft2, recraft3, recraft4, recraft5, bfl1, bfl2, bfl3, gptimage1_1, gptimage1_2, google_banana, gptimagemini1_1, googleimagen };
            myGenerators = new List<IImageGenerator>() { 
                gptimage1_1, 
                gptimage1_2, 
                gptimagemini1_1, 
                gptimagemini1_2, 
                //ideogram3, 
                //ideogram4, 
                ideogramV3,
                recraft_any, 
                recraft_any,
                //recraft3,
                //recraft6,
                dalle3,
                dalle3wide,
                //bfl1, 
                //bfl2, 
                //bfl3, 
                google_banana, googleimagen,gptimagemini1_3, 
                
            };

            //myGenerators = new List<IImageGenerator>() { dalle3, bfl1, recraft_any };
            //myGenerators = new List<IImageGenerator>() { recraft_any, googleimagen, google_banana };

            return myGenerators;
        }

            public IEnumerable<IImageGenerator> GetAllStylesOfRecraft()
        {
            var res = new List<IImageGenerator>();
            var styles = Enum.GetValues(typeof(RecraftStyle)).Cast<RecraftStyle>().ToList();
            var lim = 5;
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
