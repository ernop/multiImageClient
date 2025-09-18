using OpenAI;
using OpenAI.Images;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{

    public class Dalle3Generator : IImageGenerator
    {
        private SemaphoreSlim _dalle3Semaphore;
        private ImageClient _openAIImageClient;
        private GeneratedImageQuality _quality;
        private GeneratedImageSize _size;
        private MultiClientRunStats _stats;
        private string _name;

        public Dalle3Generator(string apiKey, int maxConcurrency,
            GeneratedImageQuality quality,
                GeneratedImageSize size,
            MultiClientRunStats stats, string name = "")
        {
            var openAIClient = new OpenAIClient(apiKey);
            _openAIImageClient = openAIClient.GetImageClient("dall-e-3");
            _dalle3Semaphore = new SemaphoreSlim(maxConcurrency);
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _quality = quality;
            _size = size;
            _stats = stats;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var qualpt = "";
            if (_quality != GeneratedImageQuality.High)
            {
                qualpt = $"_{_quality.ToString().ToLower()}";
            }
            var res = $"dalle3-{_name}{qualpt}";
            return res;
        }

        public decimal GetCost()
        {
            var ss = _size.ToString();
            switch (ss)
            {
                case "1024x1024":
                    return 0.08m;
                case "1792x1024":
                    return 0.12m;
                case "1024x1792":
                    return 0.12m;
                default:
                    throw new Exception("few");
            }
        }

        public List<string> GetRightParts()
        {
            var qualpt = "";
            if (_quality != GeneratedImageQuality.High)
            {
                qualpt = $"_{_quality.ToString().ToLower()}";
            }
            var res = $"dalle3-{_name}{qualpt}";

            var rightsideContents = new List<string>() { "dall-e-3", _name, qualpt};

            return rightsideContents;
        }

        /// it's rather annoying that we still have to send in the original pd. we do that because here is where we notice things like "de3 modified or banned the text, etc."
        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails)
        {
            await _dalle3Semaphore.WaitAsync();
            try
            {
                _stats.Dalle3RequestCount++;
                //det.Size = GeneratedImageSize.W1024xH1024;

                var det = new ImageGenerationOptions()
                {
                    Quality = GeneratedImageQuality.High,
                    Size = GeneratedImageSize.W1024xH1024
                };

                var res = await _openAIImageClient.GenerateImageAsync(promptDetails.Prompt, det);
                var uri = res.Value.ImageUri;
                var revisedPrompt = res.Value.RevisedPrompt;
                if (revisedPrompt != promptDetails.Prompt)
                {
                    promptDetails.ReplacePrompt(revisedPrompt, revisedPrompt, TransformationType.Dalle3Rewrite);
                }
                return new TaskProcessResult { IsSuccess = true, Url = uri.ToString(), ErrorMessage = "", PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Dalle3 };
            }
            catch (Exception ex)
            {
                var ms = ex.Message.Split("\r\n\r\n");
                var errorMessage = "Error.";
                if (ms.Length > 1)
                {
                    errorMessage = ms.Last();
                }
                    
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = errorMessage, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Dalle3 };
            }
            finally
            {
                _dalle3Semaphore.Release();
            }
        }
    }
}