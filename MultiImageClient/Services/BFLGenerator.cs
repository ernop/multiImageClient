using BFLAPIClient;


using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Color = SixLabors.ImageSharp.Color;
using FontStyle = SixLabors.Fonts.FontStyle;
using PointF = SixLabors.ImageSharp.PointF;
using RectangleF = SixLabors.ImageSharp.RectangleF;
using SystemFonts = SixLabors.Fonts.SystemFonts;

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
        private string _name;

        public string GetFilenamePart(PromptDetails pd)
        {
            var res = $"{_apiType}{_name}";
            var upsamplingPart = _promptUpsampling ? "_up" : "";
            if (_apiType == ImageGeneratorApiType.BFLv11)
            {
                res = $"{res}_{_height}x{_width}{upsamplingPart}";
            }
            else if (_apiType == ImageGeneratorApiType.BFLv11Ultra)
            {
                res = $"{res}_{_aspectRatio}{upsamplingPart}";
            }
            else
            {
                throw new Exception("X");
            }

            return res;
        }

        public BFLGenerator(ImageGeneratorApiType apiType, string apiKey, int maxConcurrency, bool useUltra, string aspectRatio, bool promptUpscaling, int width, int height, MultiClientRunStats stats, string name)
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

        // https://bfl.ai/pricing
        public decimal GetCost()
        {
            if (_apiType == ImageGeneratorApiType.BFLv11)
            {
                return 0.04m;
            }
            else if (_apiType == ImageGeneratorApiType.BFLv11Ultra)
            {
                return 0.06m;
                    }
            else { throw new Exception("Q"); }
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

   
    }
}