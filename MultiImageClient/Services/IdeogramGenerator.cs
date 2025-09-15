using IdeogramAPIClient;

using System;
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
        private MultiClientRunStats _stats;
        private IdeogramMagicPromptOption _magicPrompt;
        private IdeogramAspectRatio _aspectRatio;
        private IdeogramStyleType _ideoGramStyleType;

        public IdeogramGenerator(string apiKey, int maxConcurrency, MultiClientRunStats stats, IdeogramMagicPromptOption magicPrompt, IdeogramAspectRatio aspectRatio, IdeogramStyleType styleType)
        {
            _ideogramClient = new IdeogramClient(apiKey);
            _ideogramSemaphore = new SemaphoreSlim(maxConcurrency);
            _magicPrompt = magicPrompt;
            _aspectRatio = aspectRatio;
            _ideoGramStyleType = styleType;
            _stats = stats;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var res = $"magic_{_magicPrompt.ToString()} ar_{_aspectRatio.ToString()} style_{_ideoGramStyleType.ToString()}";

            return res;
        }

        public Bitmap GetLabelBitmap(int width)
        {
            throw new NotImplementedException();
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails)
        {
            await _ideogramSemaphore.WaitAsync();
            try
            {
                var ideogramDetails = new IdeogramDetails
                {
                    AspectRatio = _aspectRatio,
                    Model = IdeogramModel.V_2,
                    MagicPromptOption = _magicPrompt,
                    StyleType = _ideoGramStyleType
                };

                var request = new IdeogramGenerateRequest(promptDetails.Prompt, ideogramDetails);

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
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Ideogram, GenericImageErrorType = GenericImageGenerationErrorType.Unknown };
            }
            finally
            {
                _ideogramSemaphore.Release();
            }
        }
    }
}