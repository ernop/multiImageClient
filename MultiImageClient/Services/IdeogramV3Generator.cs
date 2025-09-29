using MultiImageClient;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;


namespace IdeogramAPIClient
{
    public class IdeogramV3Generator : IImageGenerator
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly IdeogramClient _client;
        private readonly MultiClientRunStats _stats;
        private readonly IdeogramV3StyleType _styleType;
        private readonly IdeogramMagicPromptOption? _magicPromptOption;
        private readonly string _aspectRatio;
        private readonly IdeogramRenderingSpeed _renderingSpeed;
        private readonly string _negativePrompt;
        private readonly string _name;
        
        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.IdeogramV3;

        public IdeogramV3Generator(
            string apiKey,
            int maxConcurrency,
            IdeogramV3StyleType styleType,
            IdeogramMagicPromptOption? magicPromptOption,
            IdeogramAspectRatio aspectRatio,
            IdeogramRenderingSpeed renderingSpeed,
            string negativePrompt,
            MultiClientRunStats stats,
            string name)
        {
            _client = new IdeogramClient(apiKey);
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _styleType = styleType;
            _magicPromptOption = magicPromptOption;
            var convertedAspectRatio = aspectRatio.ToString().Replace("ASPECT_", "").Replace("_", "x");
            _aspectRatio = convertedAspectRatio;
            _renderingSpeed = renderingSpeed;
            _negativePrompt = negativePrompt ?? string.Empty;
            _name = string.IsNullOrEmpty(name) ? "" : name;
        }

        

        public string GetFilenamePart(PromptDetails pd)
        {
            var parts = new List<string> { ApiType.ToString() };
            if (!string.IsNullOrEmpty(_name))
            {
                parts.Add(_name);
            }

            parts.Add(_styleType.ToString());
            parts.Add(_aspectRatio.ToString().Replace(":", "x"));
            parts.Add(_renderingSpeed.ToString());
            return string.Join("_", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        public List<string> GetRightParts()
        {
            var contents = new List<string> { "ideogram v3" };
            if (!string.IsNullOrEmpty(_name))
            {
                contents.Add(_name);
            }

            contents.Add(_styleType.ToString());
            contents.Add(_aspectRatio.ToString().Replace(":", "x"));
            contents.Add(_renderingSpeed.ToString());

            return contents;
        }

        public string GetGeneratorSpecPart()
        {
            if (!string.IsNullOrEmpty(_name))
            {
                return _name;
            }
            return "ideogram-v3";
        }

        public decimal GetCost()
        {
            // Pricing is not yet documented; leave a placeholder number until official rates available.
            return 0.08m;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            try
            {
                _stats.IdeogramV3RequestCount++;

                var request = new IdeogramV3GenerateRequest(promptDetails.Prompt)
                {
                    AspectRatio = _aspectRatio,
                    RenderingSpeed = _renderingSpeed,
                    StyleType = _styleType,
                    MagicPrompt = _magicPromptOption,
                    NegativePrompt = string.IsNullOrWhiteSpace(_negativePrompt) ? null : _negativePrompt
                };

                var response = await _client.GenerateImageV3Async(request);

                if (response?.Data == null || response.Data.Count == 0)
                {
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "No images generated",
                        PromptDetails = promptDetails,
                        ImageGenerator = ImageGeneratorApiType.IdeogramV3,
                        GenericImageErrorType = GenericImageGenerationErrorType.NoImagesGenerated,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                    };
                }

                if (response.Data.Count > 1)
                {
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "Multiple images returned and the client is not configured for batches",
                        PromptDetails = promptDetails,
                        ImageGenerator = ImageGeneratorApiType.IdeogramV3,
                        GenericImageErrorType = GenericImageGenerationErrorType.Unknown,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                    };
                }

                var imageObject = response.Data[0];
                if (!string.IsNullOrWhiteSpace(imageObject.Prompt) &&
                    !string.Equals(imageObject.Prompt, promptDetails.Prompt, StringComparison.OrdinalIgnoreCase))
                {
                    promptDetails.ReplacePrompt(imageObject.Prompt, imageObject.Prompt, TransformationType.IdeogramRewrite);
                }

                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Url = imageObject.Url,
                    PromptDetails = promptDetails,
                    ImageGenerator = ImageGeneratorApiType.IdeogramV3,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                };
            }
            catch (HttpRequestException ex)
            {
                _stats.IdeogramV3RefusedCount++;
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGenerator = ImageGeneratorApiType.IdeogramV3,
                    GenericImageErrorType = GenericImageGenerationErrorType.Unknown,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                };
            }
            catch (Exception ex)
            {
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGenerator = ImageGeneratorApiType.IdeogramV3,
                    GenericImageErrorType = GenericImageGenerationErrorType.Unknown,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                };
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}

