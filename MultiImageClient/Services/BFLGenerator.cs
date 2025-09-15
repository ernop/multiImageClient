using BFLAPIClient;

using IdeogramAPIClient;

using MultiImageClient;

using System;
using System.Drawing;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class BFLGenerator : IImageGenerator
    {
        private SemaphoreSlim _bflSemaphore;
        private BFLClient _bflClient;
        private HttpClient _httpClient;
        private MultiClientRunStats _stats;
        private string _aspectRatio = "1:1";
        private bool _promptUpsampling = false;
        private int _width { get; set; }
        private int _height { get; set; }
        private ImageGeneratorApiType _apiType { get; }

        public BFLGenerator(ImageGeneratorApiType apiType, string apiKey, int maxConcurrency, MultiClientRunStats stats, bool useUltra, string aspectRatio, bool promptUpscaling, int width, int height)
        {
            _apiType = apiType;
            _bflClient = new BFLClient(apiKey);
            _bflSemaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient();
            _stats = stats;
            _aspectRatio = aspectRatio;
            _promptUpsampling = promptUpscaling;
            _width = width;
            _height = height;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails)
        {
            await _bflSemaphore.WaitAsync();

            try
            {
                GenerationResponse generationResponse = null;
                if (_apiType == ImageGeneratorApiType.BFLv11)
                {
                    var request = new FluxPro11Request
                    {
                        Prompt = promptDetails.Prompt,
                        Width = _width,
                        Height = _height,
                        PromptUpsampling = _promptUpsampling,
                        SafetyTolerance = 6
                    };

                    generationResponse = await _bflClient.GenerateFluxPro11Async(request); 
                }
                else if (_apiType == ImageGeneratorApiType.BFLv11Ultra)
                {
                    var request2 = new FluxPro11UltraRequest
                    {
                        Prompt = promptDetails.Prompt,
                        AspectRatio = _aspectRatio,
                        PromptUpsampling = _promptUpsampling,
                        SafetyTolerance = 6
                    };
                    generationResponse = await _bflClient.GenerateFluxPro11UltraAsync(request2);
                }
                else
                {
                    Console.WriteLine("error.");
                }


                _stats.BFLImageGenerationRequestCount++;

                Logger.Log($"{promptDetails} From BFL: '{generationResponse.Status}'");

                // this is where we handle generator-specific error types.
                if (generationResponse.Status != "Ready")
                {
                    var baseResponse = new TaskProcessResult { IsSuccess = false, PromptDetails = promptDetails, ImageGenerator = _apiType, ErrorMessage = generationResponse.Status };
                    if (generationResponse.Status == "Content Moderated")
                    {
                        _stats.BFLImageGenerationErrorCount++;
                        baseResponse.GenericImageErrorType = GenericImageGenerationErrorType.ContentModerated;
                        return baseResponse;
                    }
                    else if (generationResponse.Status == "Request Moderated")
                    {
                        _stats.BFLImageGenerationErrorCount++;
                        baseResponse.GenericImageErrorType = GenericImageGenerationErrorType.RequestModerated;
                        return baseResponse;

                    }
                    else
                    {
                        // also you have to handle the out of money case.
                        _stats.BFLImageGenerationErrorCount++;
                        baseResponse.GenericImageErrorType = GenericImageGenerationErrorType.Unknown;
                        return baseResponse;
                    }

                }
                else
                {
                    Logger.Log($"{promptDetails} BFL image generated: {generationResponse.Result.Sample}");
                    _stats.BFLImageGenerationRequestCount++;
                    var returnedPrompt = generationResponse.Result.Prompt.Trim();
                    if (returnedPrompt.Trim() != promptDetails.Prompt.Trim())
                    {
                        //BFL replaced the prompt. It actually happens!
                        promptDetails.ReplacePrompt(returnedPrompt, returnedPrompt, TransformationType.BFLRewrite);
                    }

                    var headResponse = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, generationResponse.Result.Sample));
                    var contentType = headResponse.Content.Headers.ContentType?.MediaType;
                    return new TaskProcessResult { IsSuccess = true, Url = generationResponse.Result.Sample, ContentType = contentType, PromptDetails = promptDetails, ImageGenerator = _apiType };
                }

            }
            catch (Exception ex)
            {
                Logger.Log($"{promptDetails} BFL error: {ex.Message}");
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.BFLv11 };
            }
            finally
            {
                _bflSemaphore.Release();
            }
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var res = $"";
            if (_apiType == ImageGeneratorApiType.BFLv11)
            {
                var ups = _promptUpsampling ? "_up" : "";
                res = $"{res} {_height}x{_width}{ups}";
            }
            else if (_apiType == ImageGeneratorApiType.BFLv11Ultra)
            {
                var ups = _promptUpsampling ? "_up" : "";
                res = $"{_aspectRatio}{ups}";
            }
            else
            {
                throw new Exception("X");
            }
                
            return res;
        }

        public Bitmap GetLabelBitmap(int width)
        {
            throw new NotImplementedException();
        }
    }
}