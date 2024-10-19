using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;

using BFLAPIClient;

using IdeogramAPIClient;
using MultiImageClient.Implementation;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

using OpenAI;
using OpenAI.Images;
using OpenAI.Models;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;

using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{

    public class Dalle3Service : IImageGenerationService
    {
        private SemaphoreSlim _dalle3Semaphore;
        private ImageClient _openAIImageClient;

        public Dalle3Service(string apiKey, int maxConcurrency)
        {
            var openAIClient = new OpenAIClient(apiKey);
            _openAIImageClient = openAIClient.GetImageClient("dall-e-3");
            _dalle3Semaphore = new SemaphoreSlim(maxConcurrency);
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails, MultiClientRunStats stats)
        {
            await _dalle3Semaphore.WaitAsync();
            try
            {
                stats.Dalle3RequestCount++;
                var genOptions = new ImageGenerationOptions();
                genOptions.Size = promptDetails.Dalle3Details.Size;
                genOptions.Quality = promptDetails.Dalle3Details.Quality;
                genOptions.ResponseFormat = promptDetails.Dalle3Details.Format;
                var res = _openAIImageClient.GenerateImageAsync(promptDetails.Prompt, genOptions);
                var uri = res.Result.Value.ImageUri;
                var revisedPrompt = res.Result.Value.RevisedPrompt;
                if (revisedPrompt != promptDetails.Prompt)
                {
                    //BFL replaced the prompt.
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