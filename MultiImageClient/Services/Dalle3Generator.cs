using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;

using BFLAPIClient;

using IdeogramAPIClient;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

using OpenAI;
using OpenAI.Images;
using OpenAI.Models;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Reflection;

using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{

    public class Dalle3Generator : IImageGenerator
    {
        private SemaphoreSlim _dalle3Semaphore;
        private ImageClient _openAIImageClient;
        private MultiClientRunStats _stats;

        public Dalle3Generator(string apiKey, int maxConcurrency, MultiClientRunStats stats)
        {
            var openAIClient = new OpenAIClient(apiKey);
            _openAIImageClient = openAIClient.GetImageClient("dall-e-3");
            _dalle3Semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var res = $"";
            return res;
        }

        public Bitmap GetLabelBitmap(int width)
        {
            throw new NotImplementedException();
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
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Dalle3 };
            }
            finally
            {
                _dalle3Semaphore.Release();
            }

        }

    }
}