using IdeogramAPIClient;
using System.Text.RegularExpressions;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class IdeogramGenerator : IImageGenerator
    {
        private SemaphoreSlim _ideogramSemaphore;
        private IdeogramClient _ideogramClient;
        private HttpClient _httpClient = new HttpClient();
        private IdeogramMagicPromptOption _magicPrompt;
        private IdeogramAspectRatio _aspectRatio;
        private IdeogramStyleType? _ideoGramStyleType;
        private string _negativePrompt;
        private MultiClientRunStats _stats;
        private IdeogramModel _model;
        private string _name;

        public string GetGeneratorSpecPart()
        {
            if (string.IsNullOrEmpty(_name))
            {
                return $"ideogram_{_model}";
            }
            else
            {
                return $"{_name}";
            }
        }

        public IdeogramGenerator(string apiKey, int maxConcurrency, IdeogramMagicPromptOption magicPrompt, IdeogramAspectRatio aspectRatio, IdeogramStyleType? styleType, string negativePrompt, IdeogramModel model, MultiClientRunStats stats, string name)
        {
            _ideogramClient = new IdeogramClient(apiKey);
            _ideogramSemaphore = new SemaphoreSlim(maxConcurrency);
            _magicPrompt = magicPrompt;
            _aspectRatio = aspectRatio;
            _ideoGramStyleType = styleType;
            _negativePrompt = negativePrompt;
            _model = model;
            _stats = stats;
            _name = string.IsNullOrEmpty(name) ? "" : name;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var neg = "";
            var stylepart = "";
            if (_ideoGramStyleType == null)
            {
                //auto
                stylepart = "";
            }
            else
            {
                stylepart = $"_{_ideoGramStyleType.ToString().ToLowerInvariant()}";
            }
            var clneg = TextUtils.CleanPrompt(_negativePrompt);
            if (!string.IsNullOrEmpty(clneg))
            {
                neg = $" neg_{clneg}";
            }
            
            var verpart = "";
            switch (_model)
            {
                case IdeogramModel.V_1:
                    verpart = "v1";
                    break;
                case IdeogramModel.V_2:
                    verpart = "v2";
                    break;
                case IdeogramModel.V_1_TURBO:
                    verpart = "v1_turbo";
                    break;
                case IdeogramModel.V_2_TURBO:
                    verpart = "v2_turbo";
                    break;
                case IdeogramModel.V_2A:
                    verpart = "v2a";
                    break;
                case IdeogramModel.V_2A_TURBO:
                    verpart = "v2a_turbo";
                    break;
                default:
                    throw new Exception("Q");
            }


            var res = $"ideogram_{verpart}{_name}_magic_{_magicPrompt.ToString().ToLowerInvariant()} {_aspectRatio.ToString().ToLowerInvariant().Replace("aspect_","ar").Replace("_","x")} {stylepart}{neg}";

            return res;
        }

        // https://ideogram.ai/features/api-pricing
        public decimal GetCost()
        {
            switch (_model)
            {
                case IdeogramModel.V_1:
                    return 0.06m;
                case IdeogramModel.V_1_TURBO:
                    return 0.02m;
                case IdeogramModel.V_2:
                    return 0.08m;
                case IdeogramModel.V_2_TURBO:
                    return 0.05m;
                case IdeogramModel.V_2A:
                    return 0.04m;
                case IdeogramModel.V_2A_TURBO:
                    return 0.025m;
                default:
                    throw new Exception("Q");
            }
        }

        public List<string> GetRightParts()
        {
            var neg = "";
            var stylepart = "";
            if (_ideoGramStyleType == null)
            {
                //auto
                stylepart = "style auto";
            }
            else
            {
                stylepart = $"style {_ideoGramStyleType.ToString().ToLowerInvariant()}";
            }
            var clneg = TextUtils.CleanPrompt(_negativePrompt);
            if (!string.IsNullOrEmpty(clneg))
            {
                neg = $" neg {clneg}";
            }
            var verpart = _model.ToString().Replace("_", "").ToLowerInvariant();

            var rightsideContents = new List<string>() { $"ideogram {verpart}", _name, stylepart, neg, $"magicprompt_{_magicPrompt.ToString()}", _aspectRatio.ToString().ToLowerInvariant().Replace("aspect_", "ar").Replace("_", "x") };

            return rightsideContents;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _ideogramSemaphore.WaitAsync();
            try
            {
                var ideogramOptions = new IdeogramOptions
                {
                    AspectRatio = _aspectRatio,
                    Model = IdeogramModel.V_2,
                    MagicPromptOption = _magicPrompt,
                    StyleType = _ideoGramStyleType
                };

                var request = new IdeogramGenerateRequest(promptDetails.Prompt, ideogramOptions);

                _stats.IdeogramRequestCount++;
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
                        var headResponse = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, imageObject.Url));
                        var contentType = headResponse.Content.Headers.ContentType?.MediaType;
                        return new TaskProcessResult { IsSuccess = true, Url = imageObject.Url, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Ideogram, ImageGeneratorDescription = generator.GetGeneratorSpecPart() };
                    }
                    throw new Exception("No images returned");
                }
                else if (response?.Data?.Count >= 1)
                {
                    throw new Exception("Multiple images returned? I can't handle this! Who knew!");
                }
                else
                {
                    return new TaskProcessResult { IsSuccess = false, ErrorMessage = "No images generated", PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Ideogram, GenericImageErrorType = GenericImageGenerationErrorType.NoImagesGenerated, ImageGeneratorDescription = generator.GetGeneratorSpecPart() };
                }
            }

            catch (Exception ex)
            {
                Console.WriteLine(ex);

                Match m = Regex.Match(ex.Message, "\"error\"\\s*:\\s*\"([^\"]+)\"");
                string errorMessage;
                if (m.Success)
                {
                    errorMessage = m.Groups[1].Value;
                    Console.WriteLine(errorMessage);
                }
                else
                {
                    errorMessage = ex.Message;
                }
                    return new TaskProcessResult { IsSuccess = false, ErrorMessage = errorMessage, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Ideogram, GenericImageErrorType = GenericImageGenerationErrorType.Unknown, ImageGeneratorDescription = generator.GetGeneratorSpecPart() };
            }
            finally
            {
                _ideogramSemaphore.Release();
            }
        }
    }
}