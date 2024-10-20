using BFLAPIClient;

using IdeogramAPIClient;
using MultiImageClient.Enums;

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

        public BFLService(string apiKey, int maxConcurrency)
        {
            _bflClient = new BFLClient(apiKey);
            _bflSemaphore = new SemaphoreSlim(maxConcurrency);
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
                Console.WriteLine($"\tFrom BFL: '{generationResult.Status}'");

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
                    Console.WriteLine($"BFL image generated: {generationResult.Result.Sample}");
                    stats.BFLImageGenerationRequestCount++;
                    var returnedPrompt = generationResult.Result.Prompt.Trim();
                    if (returnedPrompt.Trim() != promptDetails.Prompt.Trim())
                    {
                        //BFL replaced the prompt. Never actually happens.
                        promptDetails.ReplacePrompt(returnedPrompt.Trim(), returnedPrompt.Trim(), TransformationType.BFLRewrite);
                    }
                    return new TaskProcessResult { IsSuccess = true, Url = generationResult.Result.Sample, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.BFL };
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"BFL error: {ex.Message}");
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.BFL };
            }
            finally
            {
                _bflSemaphore.Release();
            }
        }
    }
}