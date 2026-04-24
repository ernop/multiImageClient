using BFLAPIClient;

using System;
using System.Collections.Generic;
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

        public ImageGeneratorApiType ApiType => _apiType;

        private string _name;


        public string GetGeneratorSpecPart()
        {
            if (string.IsNullOrEmpty(_name))
            {
                return $"{_apiType}";
            }
            else
            {
                return $"{_name}";
            }
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var res = $"{_apiType}{_name}";
            var upsamplingPart = _promptUpsampling ? "_up" : "";
            switch (_apiType)
            {
                case ImageGeneratorApiType.BFLv11:
                case ImageGeneratorApiType.BFLFlux2Pro:
                case ImageGeneratorApiType.BFLFlux2Max:
                case ImageGeneratorApiType.BFLFlux2Flex:
                case ImageGeneratorApiType.BFLFlux2Klein4b:
                case ImageGeneratorApiType.BFLFlux2Klein9b:
                    res = $"{res}_{_height}x{_width}{upsamplingPart}";
                    break;
                case ImageGeneratorApiType.BFLv11Ultra:
                    res = $"{res}_{_aspectRatio}{upsamplingPart}";
                    break;
                default:
                    throw new Exception($"BFLGenerator: unhandled api type {_apiType}");
            }

            return res;
        }

        public BFLGenerator(ImageGeneratorApiType apiType, string apiKey, int maxConcurrency, string aspectRatio, bool promptUpscaling, int width, int height, MultiClientRunStats stats, string name)
        {
            _apiType = apiType;
            _bflClient = new BFLClient(apiKey);
            _bflSemaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient();

            _aspectRatio = aspectRatio;
            _promptUpsampling = promptUpscaling;
            _width = width;
            _height = height;

            _stats = stats;
            _name = string.IsNullOrEmpty(name) ? "" : name;
        }
        public List<string> GetRightParts()
        {
            var upsamplingPart = _promptUpsampling ? "prompt rewritten." : "";
            var rightsideContents = new List<string>() { _apiType.ToString(), upsamplingPart};
            return rightsideContents;
        }

        // https://docs.bfl.ai/quick_start/pricing
        public decimal GetCost()
        {
            switch (_apiType)
            {
                case ImageGeneratorApiType.BFLv11:
                    return 0.04m;
                case ImageGeneratorApiType.BFLv11Ultra:
                    return 0.06m;
                // FLUX.2 is megapixel-priced; the numbers below are the headline
                // rate at 1 MP output and will under-report for larger sizes.
                case ImageGeneratorApiType.BFLFlux2Pro:
                    return 0.03m;
                case ImageGeneratorApiType.BFLFlux2Max:
                    return 0.07m;
                case ImageGeneratorApiType.BFLFlux2Flex:
                    return 0.06m;
                case ImageGeneratorApiType.BFLFlux2Klein4b:
                    return 0.014m;
                case ImageGeneratorApiType.BFLFlux2Klein9b:
                    return 0.015m;
                default:
                    throw new Exception($"BFLGenerator: no cost entry for {_apiType}");
            }
        }
        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _bflSemaphore.WaitAsync();

            try
            {
                GenerationResponse generationResponse = null;
                switch (_apiType)
                {
                    case ImageGeneratorApiType.BFLv11:
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
                        break;
                    }
                    case ImageGeneratorApiType.BFLv11Ultra:
                    {
                        var request = new FluxPro11UltraRequest
                        {
                            Prompt = promptDetails.Prompt,
                            AspectRatio = _aspectRatio,
                            PromptUpsampling = _promptUpsampling,
                            Width = _width,
                            Height = _height,
                            SafetyTolerance = 6
                        };
                        generationResponse = await _bflClient.GenerateFluxPro11UltraAsync(request);
                        break;
                    }
                    case ImageGeneratorApiType.BFLFlux2Pro:
                    case ImageGeneratorApiType.BFLFlux2Max:
                    case ImageGeneratorApiType.BFLFlux2Flex:
                    case ImageGeneratorApiType.BFLFlux2Klein4b:
                    case ImageGeneratorApiType.BFLFlux2Klein9b:
                    {
                        var request = new Flux2Request
                        {
                            Prompt = promptDetails.Prompt,
                            Width = _width,
                            Height = _height,
                            PromptUpsampling = _promptUpsampling,
                            SafetyTolerance = 6,
                        };
                        // flex lets you steer denoising; keep it permissive by default.
                        if (_apiType == ImageGeneratorApiType.BFLFlux2Flex)
                        {
                            request.Steps = 40;
                            request.Guidance = 4.5f;
                        }

                        generationResponse = _apiType switch
                        {
                            ImageGeneratorApiType.BFLFlux2Pro => await _bflClient.GenerateFlux2ProAsync(request),
                            ImageGeneratorApiType.BFLFlux2Max => await _bflClient.GenerateFlux2MaxAsync(request),
                            ImageGeneratorApiType.BFLFlux2Flex => await _bflClient.GenerateFlux2FlexAsync(request),
                            ImageGeneratorApiType.BFLFlux2Klein4b => await _bflClient.GenerateFlux2Klein4bAsync(request),
                            ImageGeneratorApiType.BFLFlux2Klein9b => await _bflClient.GenerateFlux2Klein9bAsync(request),
                            _ => throw new Exception("unreachable"),
                        };
                        break;
                    }
                    default:
                        throw new Exception($"BFLGenerator: unsupported api type {_apiType}");
                }


                _stats.BFLImageGenerationRequestCount++;

                Logger.Log($"{promptDetails} From BFL ({_apiType}): '{generationResponse.Status}'");

                if (generationResponse.Status != "Ready")
                {
                    var baseResponse = new TaskProcessResult { IsSuccess = false, PromptDetails = promptDetails, ImageGeneratorDescription = generator.GetGeneratorSpecPart(), ImageGenerator = _apiType, ErrorMessage = generationResponse.Status };
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
                        _stats.BFLImageGenerationErrorCount++;
                        baseResponse.GenericImageErrorType = GenericImageGenerationErrorType.Unknown;
                        return baseResponse;
                    }

                }
                else
                {
                    Logger.Log($"{promptDetails} BFL image generated: {generationResponse.Result.Sample}");
                    _stats.BFLImageGenerationSuccessCount++;
                    var returnedPrompt = generationResponse.Result.Prompt?.Trim();
                    if (!string.IsNullOrEmpty(returnedPrompt) && returnedPrompt != promptDetails.Prompt.Trim())
                    {
                        // BFL rewrote the prompt. It actually happens (prompt upsampling, safety, etc.).
                        promptDetails.ReplacePrompt(returnedPrompt, returnedPrompt, TransformationType.BFLRewrite);
                    }

                    var headResponse = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, generationResponse.Result.Sample));
                    var contentType = headResponse.Content.Headers.ContentType?.MediaType;
                    return new TaskProcessResult { IsSuccess = true, Url = generationResponse.Result.Sample, ContentType = contentType, ImageGeneratorDescription = generator.GetGeneratorSpecPart(), PromptDetails = promptDetails, ImageGenerator = _apiType };
                }

            }
            catch (Exception ex)
            {
                Logger.Log($"{promptDetails} BFL error: {ex.Message}");
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGeneratorDescription = generator.GetGeneratorSpecPart(), ImageGenerator = _apiType };
            }
            finally
            {
                _bflSemaphore.Release();
            }
        }
    }
}
