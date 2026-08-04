using BFLAPIClient;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class BFLGenerator : IImageGenerator
    {
        private SemaphoreSlim _bflSemaphore;
        private BFLClient _bflClient;
        private MultiClientRunStats _stats;
        private string _aspectRatio = "1:1";
        private bool _promptUpsampling = false;
        private int _width { get; set; }
        private int _height { get; set; }
        private string _inputImagePath;
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
                case ImageGeneratorApiType.BFLFlux2ProPreview:
                case ImageGeneratorApiType.BFLFlux2Max:
                case ImageGeneratorApiType.BFLFlux2Flex:
                case ImageGeneratorApiType.BFLFlux2Klein4b:
                case ImageGeneratorApiType.BFLFlux2Klein9b:
                case ImageGeneratorApiType.BFLFlux2Klein9bPreview:
                case ImageGeneratorApiType.BFLFluxPro:
                case ImageGeneratorApiType.BFLFluxDev:
                    res = $"{res}_{_height}x{_width}{upsamplingPart}";
                    break;
                case ImageGeneratorApiType.BFLv11Ultra:
                case ImageGeneratorApiType.BFLFluxKontextPro:
                case ImageGeneratorApiType.BFLFluxKontextMax:
                    res = $"{res}_{_aspectRatio}{upsamplingPart}";
                    break;
                default:
                    throw new Exception($"BFLGenerator: unhandled api type {_apiType}");
            }

            return res;
        }

        /// inputImagePath: when set (FLUX.2 endpoints only), the image is sent as
        ///   input_image conditioning — a reference/guide the model draws on, not
        ///   a mask-based edit. All FLUX.2 variants support this.
        public BFLGenerator(ImageGeneratorApiType apiType, string apiKey, int maxConcurrency, string aspectRatio, bool promptUpscaling, int width, int height, MultiClientRunStats stats, string name, string inputImagePath = null)
        {
            _apiType = apiType;
            _bflClient = new BFLClient(apiKey);
            _bflSemaphore = new SemaphoreSlim(maxConcurrency);

            _aspectRatio = aspectRatio;
            _promptUpsampling = promptUpscaling;
            _width = width;
            _height = height;

            _stats = stats;
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _inputImagePath = inputImagePath;
        }
        public List<string> GetRightParts()
        {
            var upsamplingPart = _promptUpsampling ? "prompt rewritten." : "";
            var rightsideContents = new List<string>() { _apiType.ToString(), upsamplingPart };
            return rightsideContents;
        }

        // https://docs.bfl.ai/quick_start/pricing
        public decimal GetCost()
        {
            var outputMegapixels = Math.Max(1m, (decimal)_width * _height / 1_000_000m);
            switch (_apiType)
            {
                case ImageGeneratorApiType.BFLv11:
                    return 0.04m;
                case ImageGeneratorApiType.BFLv11Ultra:
                    return 0.06m;
                case ImageGeneratorApiType.BFLFluxPro:
                    return 0.05m;
                case ImageGeneratorApiType.BFLFluxDev:
                    return 0.025m;
                case ImageGeneratorApiType.BFLFluxKontextPro:
                    return 0.04m;
                case ImageGeneratorApiType.BFLFluxKontextMax:
                    return 0.08m;
                case ImageGeneratorApiType.BFLFlux2Pro:
                case ImageGeneratorApiType.BFLFlux2ProPreview:
                    return outputMegapixels * (string.IsNullOrEmpty(_inputImagePath) ? 0.03m : 0.045m);
                case ImageGeneratorApiType.BFLFlux2Max:
                    return outputMegapixels * 0.07m;
                case ImageGeneratorApiType.BFLFlux2Flex:
                    return outputMegapixels * 0.05m;
                case ImageGeneratorApiType.BFLFlux2Klein4b:
                    return 0.014m + Math.Max(0m, outputMegapixels - 1m) * 0.001m;
                case ImageGeneratorApiType.BFLFlux2Klein9b:
                case ImageGeneratorApiType.BFLFlux2Klein9bPreview:
                    return 0.015m + Math.Max(0m, outputMegapixels - 1m) * 0.002m;
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
                // BFL rejects safety_tolerance 6 with a 403 ("safety_tolerance > 5
                // requires authorization") on normal accounts; 5 is the max
                // permissive value available to us.
                const int MaxPermissiveSafetyTolerance = 5;
                promptDetails.RuntimeMeta["size"] = $"{_width}x{_height}";
                promptDetails.RuntimeMeta["width"] = _width.ToString();
                promptDetails.RuntimeMeta["height"] = _height.ToString();
                promptDetails.RuntimeMeta["prompt_upsampling"] = _promptUpsampling.ToString().ToLowerInvariant();
                promptDetails.RuntimeMeta["safety_tolerance"] = MaxPermissiveSafetyTolerance.ToString();
                promptDetails.RuntimeMeta["output_format"] = "png";
                switch (_apiType)
                {
                    case ImageGeneratorApiType.BFLv11:
                        {
                            var request = new FluxPro11Request
                            {
                                Prompt = promptDetails.Prompt,
                                ImagePrompt = ReadInputImageBase64(),
                                Width = _width,
                                Height = _height,
                                PromptUpsampling = _promptUpsampling,
                                SafetyTolerance = MaxPermissiveSafetyTolerance
                            };
                            generationResponse = await _bflClient.GenerateFluxPro11Async(request);
                            break;
                        }
                    case ImageGeneratorApiType.BFLv11Ultra:
                        {
                            var request = new FluxPro11UltraRequest
                            {
                                Prompt = promptDetails.Prompt,
                                ImagePrompt = ReadInputImageBase64(),
                                ImagePromptStrength = string.IsNullOrEmpty(_inputImagePath) ? null : 0.1f,
                                AspectRatio = _aspectRatio,
                                PromptUpsampling = _promptUpsampling,
                                SafetyTolerance = MaxPermissiveSafetyTolerance
                            };
                            generationResponse = await _bflClient.GenerateFluxPro11UltraAsync(request);
                            break;
                        }
                    case ImageGeneratorApiType.BFLFluxPro:
                        {
                            var request = new FluxProRequest
                            {
                                Prompt = promptDetails.Prompt,
                                Width = _width,
                                Height = _height,
                                PromptUpsampling = _promptUpsampling,
                                SafetyTolerance = MaxPermissiveSafetyTolerance
                            };
                            generationResponse = await _bflClient.GenerateFluxProAsync(request);
                            break;
                        }
                    case ImageGeneratorApiType.BFLFluxDev:
                        {
                            var request = new FluxDevRequest
                            {
                                Prompt = promptDetails.Prompt,
                                ImagePrompt = ReadInputImageBase64(),
                                Width = _width,
                                Height = _height,
                                PromptUpsampling = _promptUpsampling,
                                SafetyTolerance = MaxPermissiveSafetyTolerance
                            };
                            generationResponse = await _bflClient.GenerateFluxDevAsync(request);
                            break;
                        }
                    case ImageGeneratorApiType.BFLFluxKontextPro:
                    case ImageGeneratorApiType.BFLFluxKontextMax:
                        {
                            var request = new FluxKontextRequest
                            {
                                Prompt = promptDetails.Prompt,
                                InputImage = ReadInputImageBase64(),
                                AspectRatio = _aspectRatio,
                                PromptUpsampling = _promptUpsampling,
                                SafetyTolerance = MaxPermissiveSafetyTolerance
                            };
                            generationResponse = _apiType == ImageGeneratorApiType.BFLFluxKontextPro
                                ? await _bflClient.GenerateFluxKontextProAsync(request)
                                : await _bflClient.GenerateFluxKontextMaxAsync(request);
                            break;
                        }
                    case ImageGeneratorApiType.BFLFlux2Pro:
                    case ImageGeneratorApiType.BFLFlux2ProPreview:
                    case ImageGeneratorApiType.BFLFlux2Max:
                        {
                            promptDetails.RuntimeMeta["endpoint"] = GetFlux2Endpoint(_apiType);
                            var request = BuildFlux2Request<Flux2Request>(
                                promptDetails,
                                MaxPermissiveSafetyTolerance);
                            request.DisablePromptUpsampling = !_promptUpsampling;
                            generationResponse = _apiType switch
                            {
                                ImageGeneratorApiType.BFLFlux2Pro => await _bflClient.GenerateFlux2ProAsync(request),
                                ImageGeneratorApiType.BFLFlux2ProPreview => await _bflClient.GenerateFlux2ProPreviewAsync(request),
                                ImageGeneratorApiType.BFLFlux2Max => await _bflClient.GenerateFlux2MaxAsync(request),
                                _ => throw new Exception("unreachable"),
                            };
                            break;
                        }
                    case ImageGeneratorApiType.BFLFlux2Flex:
                        {
                            promptDetails.RuntimeMeta["endpoint"] = GetFlux2Endpoint(_apiType);
                            var request = BuildFlux2Request<Flux2FlexRequest>(
                                promptDetails,
                                MaxPermissiveSafetyTolerance);
                            request.PromptUpsampling = _promptUpsampling;
                            request.Steps = 40;
                            request.Guidance = 4.5f;
                            generationResponse = await _bflClient.GenerateFlux2FlexAsync(request);
                            break;
                        }
                    case ImageGeneratorApiType.BFLFlux2Klein4b:
                    case ImageGeneratorApiType.BFLFlux2Klein9b:
                    case ImageGeneratorApiType.BFLFlux2Klein9bPreview:
                        {
                            promptDetails.RuntimeMeta["endpoint"] = GetFlux2Endpoint(_apiType);
                            var request = BuildFlux2Request<Flux2KleinRequest>(
                                promptDetails,
                                MaxPermissiveSafetyTolerance);
                            generationResponse = _apiType switch
                            {
                                ImageGeneratorApiType.BFLFlux2Klein4b => await _bflClient.GenerateFlux2Klein4bAsync(request),
                                ImageGeneratorApiType.BFLFlux2Klein9b => await _bflClient.GenerateFlux2Klein9bAsync(request),
                                ImageGeneratorApiType.BFLFlux2Klein9bPreview => await _bflClient.GenerateFlux2Klein9bPreviewAsync(request),
                                _ => throw new Exception("unreachable"),
                            };
                            break;
                        }
                    default:
                        throw new Exception($"BFLGenerator: unsupported api type {_apiType}");
                }


                _stats.BFLImageGenerationRequestCount++;

                if (generationResponse == null)
                {
                    throw new InvalidOperationException("BFL returned an empty generation response.");
                }
                Logger.Log($"{promptDetails} From BFL ({_apiType}): '{generationResponse.Status}'");

                if (!string.Equals(generationResponse.Status, "Ready", StringComparison.OrdinalIgnoreCase))
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
                    if (string.IsNullOrWhiteSpace(generationResponse.Result?.Sample))
                    {
                        throw new InvalidOperationException(
                            "BFL reported Ready but did not provide a result image URL.");
                    }
                    Logger.Log($"{promptDetails} BFL image generated: {generationResponse.Result.Sample}");
                    _stats.BFLImageGenerationSuccessCount++;
                    var returnedPrompt = generationResponse.Result.Prompt?.Trim();
                    if (!string.IsNullOrEmpty(returnedPrompt) && returnedPrompt != promptDetails.Prompt.Trim())
                    {
                        // BFL rewrote the prompt. It actually happens (prompt upsampling, safety, etc.).
                        promptDetails.ReplacePrompt(returnedPrompt, returnedPrompt, TransformationType.BFLRewrite);
                    }

                    return new TaskProcessResult { IsSuccess = true, Url = generationResponse.Result.Sample, ImageGeneratorDescription = generator.GetGeneratorSpecPart(), PromptDetails = promptDetails, ImageGenerator = _apiType };
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

        private TRequest BuildFlux2Request<TRequest>(
            PromptDetails promptDetails,
            int safetyTolerance)
            where TRequest : Flux2RequestBase, new()
        {
            var request = new TRequest
            {
                Prompt = promptDetails.Prompt,
                Width = _width,
                Height = _height,
                SafetyTolerance = safetyTolerance,
            };
            if (!string.IsNullOrEmpty(_inputImagePath))
            {
                request.InputImage = ReadInputImageBase64();
                promptDetails.RuntimeMeta["input_image"] = Path.GetFileName(_inputImagePath);
            }
            return request;
        }

        private string ReadInputImageBase64()
        {
            if (string.IsNullOrEmpty(_inputImagePath))
            {
                return null;
            }
            return Convert.ToBase64String(File.ReadAllBytes(_inputImagePath));
        }

        private static string GetFlux2Endpoint(ImageGeneratorApiType apiType)
        {
            return apiType switch
            {
                ImageGeneratorApiType.BFLFlux2Pro => "flux-2-pro",
                ImageGeneratorApiType.BFLFlux2ProPreview => "flux-2-pro-preview",
                ImageGeneratorApiType.BFLFlux2Max => "flux-2-max",
                ImageGeneratorApiType.BFLFlux2Flex => "flux-2-flex",
                ImageGeneratorApiType.BFLFlux2Klein4b => "flux-2-klein-4b",
                ImageGeneratorApiType.BFLFlux2Klein9b => "flux-2-klein-9b",
                ImageGeneratorApiType.BFLFlux2Klein9bPreview => "flux-2-klein-9b-preview",
                _ => throw new Exception($"Not a FLUX.2 endpoint: {apiType}"),
            };
        }
    }
}
