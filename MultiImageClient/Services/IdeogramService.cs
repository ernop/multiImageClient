using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;

using BFLAPIClient;

using IdeogramAPIClient;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class IdeogramService : IImageGenerationService
    {
        private SemaphoreSlim _ideogramSemaphore;
        private IdeogramClient _ideogramClient;

        public IdeogramService(string apiKey, int maxConcurrency)
        {
            _ideogramClient = new IdeogramClient(apiKey);
            _ideogramSemaphore = new SemaphoreSlim(maxConcurrency);
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails, MultiClientRunStats stats)
        {
            await _ideogramSemaphore.WaitAsync();
            try
            {
                var ideogramDetails = promptDetails.IdeogramDetails;
                var request = new IdeogramGenerateRequest(promptDetails.Prompt, ideogramDetails);

                stats.IdeogramRequestCount++;
                GenerateResponse response = await _ideogramClient.GenerateImageAsync(request);
                if (response?.Data?.Count == 1)
                {
                    foreach (var imageObject in response.Data)
                    {
                        //there is only actually one ever.
                        var returnedPrompt = imageObject.Prompt;
                        if (returnedPrompt != promptDetails.Prompt)
                        {
                            //Ideogram replaced the prompt.
                            promptDetails.ReplacePrompt(returnedPrompt, returnedPrompt, TransformationType.IdeogramRewrite);
                        }
                        return new TaskProcessResult { IsSuccess = true, Url = imageObject.Url, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Ideogram };
                    }
                    throw new Exception("No images returned");
                }
                else if (response?.Data?.Count >= 1)
                {
                    throw new Exception("Multiple images returned? I can't handle this! Who knew!");
                }
                else
                {
                    return new TaskProcessResult { IsSuccess = false, ErrorMessage = "No images generated", PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Ideogram, GenericImageErrorType = GenericImageGenerationErrorType.NoImagesGenerated };
                }
            }

            catch (Exception ex)
            {
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Ideogram, GenericImageErrorType = GenericImageGenerationErrorType.Unknown };
            }
            finally
            {
                _ideogramSemaphore.Release();
            }
        }
    }
}