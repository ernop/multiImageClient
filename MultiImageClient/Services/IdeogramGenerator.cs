using IdeogramAPIClient;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
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
        private string _name;


        public IdeogramGenerator(string apiKey, int maxConcurrency, IdeogramMagicPromptOption magicPrompt, IdeogramAspectRatio aspectRatio, IdeogramStyleType? styleType, string negativePrompt, MultiClientRunStats stats, string name)
        {
            _ideogramClient = new IdeogramClient(apiKey);
            _ideogramSemaphore = new SemaphoreSlim(maxConcurrency);
            _magicPrompt = magicPrompt;
            _aspectRatio = aspectRatio;
            _ideoGramStyleType = styleType;
            _negativePrompt = negativePrompt;
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
            var res = $"ideogramv2{_name}_magic_{_magicPrompt.ToString().ToLowerInvariant()} {_aspectRatio.ToString().ToLowerInvariant().Replace("aspect_","ar").Replace("_","x")} {stylepart}{neg}";

            return res;
        }

        public List<string> GetRightParts()
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
            var res = $"ideogramv2{_name}_magic_{_magicPrompt.ToString().ToLowerInvariant()} {_aspectRatio.ToString().ToLowerInvariant().Replace("aspect_", "ar").Replace("_", "x")} {stylepart}{neg}";

            var rightsideContents = new List<string>() { "ideogram_v2", _name, stylepart, clneg, $"magicprompt_{_magicPrompt.ToString()}", _aspectRatio.ToString().ToLowerInvariant().Replace("aspect_", "ar").Replace("_", "x") };

            return rightsideContents;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails)
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
                Console.WriteLine(ex);
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Ideogram, GenericImageErrorType = GenericImageGenerationErrorType.Unknown };
            }
            finally
            {
                _ideogramSemaphore.Release();
            }
        }
    }
}