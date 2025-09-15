using BFLAPIClient;

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
            
            ImageGeneratorApiType genType;
            try
            {
                GenerationResponse generationResponse = null;
                if (promptDetails.BFL11UltraDetails != null)
                {
                    genType = ImageGeneratorApiType.BFLv11Ultra;
                    var request2 = new FluxPro11UltraRequest
                    {
                        Prompt = promptDetails.Prompt,
                        AspectRatio = "1:1",
                        //Width = 2048,
                        //Height = 2048,
                        PromptUpsampling = promptDetails.BFL11UltraDetails.PromptUpsampling,
                        SafetyTolerance = promptDetails.BFL11UltraDetails.SafetyTolerance,
                        Seed = promptDetails.BFL11UltraDetails.Seed

                    };
                    generationResponse = await _bflClient.GenerateFluxPro11UltraAsync(request2);
                }
                else
                {
                    genType = ImageGeneratorApiType.BFLv11;
                    var request = new FluxPro11Request
                    {
                        Prompt = promptDetails.Prompt,
                        Width = promptDetails.BFL11Details.Width,
                        Height = promptDetails.BFL11Details.Height,
                        PromptUpsampling = promptDetails.BFL11Details.PromptUpsampling,
                        SafetyTolerance = promptDetails.BFL11Details.SafetyTolerance,
                        Seed = promptDetails.BFL11Details.Seed
                    };

                    generationResponse = await _bflClient.GenerateFluxPro11Async(request);
                }
                    //var bflDetails = promptDetails.BFL11Details;
                    
                stats.BFLImageGenerationRequestCount++;

                
                Logger.Log($"{promptDetails.Index} From BFL: '{generationResponse.Status}'");

                // this is where we handle generator-specific error types.
                if (generationResponse.Status != "Ready")
                {
                    var baseResponse = new TaskProcessResult { IsSuccess = false, PromptDetails = promptDetails, ImageGenerator = genType, ErrorMessage = generationResponse.Status };
                    if (generationResponse.Status == "Content Moderated")
                    {
                        stats.BFLImageGenerationErrorCount++;
                        baseResponse.GenericImageErrorType = GenericImageGenerationErrorType.ContentModerated;
                        return baseResponse;
                    }
                    else if (generationResponse.Status == "Request Moderated")
                    {
                        stats.BFLImageGenerationErrorCount++;
                        baseResponse.GenericImageErrorType = GenericImageGenerationErrorType.RequestModerated;
                        return baseResponse;

                    }
                    else
                    {
                        // also you have to handle the out of money case.
                        stats.BFLImageGenerationErrorCount++;
                        baseResponse.GenericImageErrorType = GenericImageGenerationErrorType.Unknown;
                        return baseResponse;
                    }
                        
                }
                else
                {
                    Logger.Log($"{promptDetails.Index} BFL image generated: {generationResponse.Result.Sample}");
                    stats.BFLImageGenerationRequestCount++;
                    var returnedPrompt = generationResponse.Result.Prompt.Trim();
                    if (returnedPrompt.Trim() != promptDetails.Prompt.Trim())
                    {
                        //BFL replaced the prompt. It actually happens!
                        promptDetails.ReplacePrompt(returnedPrompt, returnedPrompt, TransformationType.BFLRewrite);
                    }

                    var headResponse = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, generationResponse.Result.Sample));
                    var contentType = headResponse.Content.Headers.ContentType?.MediaType;
                    return new TaskProcessResult { IsSuccess = true, Url = generationResponse.Result.Sample, ContentType = contentType, PromptDetails = promptDetails, ImageGenerator = genType };
                }

            }
            catch (Exception ex)
            {
                Logger.Log($"{promptDetails.Index} BFL error: {ex.Message}");
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.BFLv11 };
            }
            finally
            {
                _bflSemaphore.Release();
            }
        }
    }
}