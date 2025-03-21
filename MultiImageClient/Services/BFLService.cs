﻿using BFLAPIClient;

using IdeogramAPIClient;


using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class BFLService : IImageGenerationService
    {
        private SemaphoreSlim _bflSemaphore;
        private BFLClient _bflClient;
        private HttpClient _httpClient;


        public BFLService(string apiKey, int maxConcurrency)
        {
            _bflClient = new BFLClient(apiKey);
            _bflSemaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient();
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails, MultiClientRunStats stats)
        {
            await _bflSemaphore.WaitAsync();
            try
            {
                var bflDetails = promptDetails.BFLDetails;
                var request = new FluxPro11Request
                {
                    Prompt = promptDetails.Prompt,
                    Width = bflDetails.Width,
                    Height = bflDetails.Height,
                    PromptUpsampling = bflDetails.PromptUpsampling,
                    SafetyTolerance = bflDetails.SafetyTolerance,
                    Seed = bflDetails.Seed
                };

                stats.BFLImageGenerationRequestCount++;

                var generationResult = await _bflClient.GenerateFluxPro11Async(request);
                Logger.Log($"\tFrom BFL: '{generationResult.Status}'");

                // this is where we handle generator-specific error types.
                if (generationResult.Status != "Ready")
                {
                    if (generationResult.Status == "Content Moderated")
                    {
                        stats.BFLImageGenerationErrorCount++;
                        return new TaskProcessResult { IsSuccess = false, ErrorMessage = generationResult.Status, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.BFL, GenericImageErrorType = GenericImageGenerationErrorType.ContentModerated};
                    }
                    else if (generationResult.Status == "Request Moderated")
                    {
                        stats.BFLImageGenerationErrorCount++;
                        return new TaskProcessResult { IsSuccess = false, ErrorMessage = generationResult.Status, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.BFL, GenericImageErrorType = GenericImageGenerationErrorType.RequestModerated };
                    }
                    else
                    {
                        // also you have to handle the out of money case.
                        stats.BFLImageGenerationErrorCount++;
                        return new TaskProcessResult { IsSuccess = false, ErrorMessage = generationResult.Status, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.BFL, GenericImageErrorType = GenericImageGenerationErrorType.Unknown };
                    }
                        
                }
                else
                {
                    Logger.Log($"BFL image generated: {generationResult.Result.Sample}");
                    stats.BFLImageGenerationRequestCount++;
                    var returnedPrompt = generationResult.Result.Prompt.Trim();
                    if (returnedPrompt.Trim() != promptDetails.Prompt.Trim())
                    {
                        //BFL replaced the prompt. Never actually happens.
                        promptDetails.ReplacePrompt(returnedPrompt, returnedPrompt, TransformationType.BFLRewrite);
                    }

                    var headResponse = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, generationResult.Result.Sample));
                    var contentType = headResponse.Content.Headers.ContentType?.MediaType;
                    return new TaskProcessResult { IsSuccess = true, Url = generationResult.Result.Sample, ContentType = contentType, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.BFL };
                }

            }
            catch (Exception ex)
            {
                Logger.Log($"BFL error: {ex.Message}");
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.BFL };
            }
            finally
            {
                _bflSemaphore.Release();
            }
        }
    }
}