using IdeogramAPIClient;

using RecraftAPIClient;

using SixLabors.ImageSharp;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class RecraftGenerator : IImageGenerator
    {
        private SemaphoreSlim _recraftSemaphore;
        private RecraftClient _recraftClient;
        private HttpClient _httpClient;
        private MultiClientRunStats _stats;
        private static Random _Random = new Random();
        private RecraftStyle _style;
        private RecraftVectorIllustrationSubstyle? _substyleVector;
        private RecraftDigitalIllustrationSubstyle? _substyleDigital;
        private RecraftRealisticImageSubstyle? _substyleRealistic;
        private RecraftImageSize _imageSize;
        private string _name;

        public RecraftGenerator(string apiKey, int maxConcurrency, RecraftImageSize size, RecraftStyle style, RecraftVectorIllustrationSubstyle? substyleVector, RecraftDigitalIllustrationSubstyle? substyleDigital, RecraftRealisticImageSubstyle? substyleRealistic, MultiClientRunStats stats, string name)
        {
            _recraftClient = new RecraftClient(apiKey);
            _recraftSemaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient();

            // we probably could use some validation here.
            _style = style;
            _substyleVector = substyleVector;
            _substyleDigital = substyleDigital;
            _substyleRealistic = substyleRealistic;

            _imageSize = size;
            _stats = stats;
            _name = string.IsNullOrEmpty(name) ? "" : name;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var usingSubstyle = "";
            if (_style == RecraftStyle.digital_illustration)
            {
                usingSubstyle = _substyleDigital.ToString();
            }
            else if (_style == RecraftStyle.realistic_image)
            {
                usingSubstyle = _substyleRealistic.ToString();
            }
            else if (_style == RecraftStyle.vector_illustration)
            {
                usingSubstyle = _substyleVector.ToString();
            }
            var res = $"recraft{_name}_{_imageSize}_{_style}_{usingSubstyle}";
            return res;
        }

        // https://www.recraft.ai/docs/api-reference/pricing
        public decimal GetCost()
        {
            switch (_style)
            {
                case RecraftStyle.digital_illustration:
                    return 0.04m;
                case RecraftStyle.realistic_image:
                    return 0.04m; //unclear actually.
                case RecraftStyle.vector_illustration:
                    return 0.08m;
                default:
                    throw new Exception("whoah");
            }
        }

        public List<string> GetRightParts()
        {
            var usingSubstyle = "";
            if (_style == RecraftStyle.digital_illustration)
            {
                usingSubstyle = _substyleDigital.ToString();
            }
            else if (_style == RecraftStyle.realistic_image)
            {
                usingSubstyle = _substyleRealistic.ToString();
            }
            else if (_style == RecraftStyle.vector_illustration)
            {
                usingSubstyle = _substyleVector.ToString();
            }

            var rightsideContents = new List<string>() { "recraft_v3", _name, _style.ToString(), usingSubstyle};
            return rightsideContents;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails)
        {
            await _recraftSemaphore.WaitAsync();
            try
            {
                _stats.RecraftImageGenerationRequestCount++;
                var usingPrompt = promptDetails.Prompt;
                if (usingPrompt.Length > 1000)
                {
                    usingPrompt = usingPrompt.Substring(0, 990);
                    Logger.Log("Truncating the prompt for Recraft.");
                }

                //// Randomly select one of the three main styles
                //if (_style == "any" || _style == "")
                //{
                //    var styles = Enum.GetValues(typeof(RecraftStyle)).Cast<RecraftStyle>().ToList();
                //    _style = styles[_Random.Next(styles.Count)].ToString();
                //    Console.WriteLine($"set style to: {_style}");
                //}

                //// Based on chosen style, select appropriate substyle

                //bool coinFlip = true;

                //if (string.IsNullOrEmpty(_substyle))
                //{
                //    switch (_style)
                //    {
                //        case "digital_illustration":
                //            var digitalStyles = Enum.GetValues(typeof(RecraftDigitalIllustrationSubstyles))
                //                .Cast<RecraftDigitalIllustrationSubstyles>().ToList();
                //            if (coinFlip)
                //            {
                //                _substyle = digitalStyles[_Random.Next(digitalStyles.Count)].ToString();
                //            }
                //            break;

                //        case "realistic_image":
                //            var realisticStyles = Enum.GetValues(typeof(RecraftRealisticImageSubstyles))
                //                .Cast<RecraftRealisticImageSubstyles>().ToList();
                //            if (coinFlip)
                //            {
                //                _substyle = realisticStyles[_Random.Next(realisticStyles.Count)].ToString();
                //            }
                //            break;

                //        case "vector_illustration":
                //            var vectorStyles = Enum.GetValues(typeof(RecraftVectorIllustrationSubstyles))
                //                .Cast<RecraftVectorIllustrationSubstyles>().ToList();
                //            if (coinFlip)
                //            {
                //                _substyle = vectorStyles[_Random.Next(vectorStyles.Count)].ToString();
                //            }
                //            break;
                //    }
                //    Console.WriteLine($"set substyle to: {_substyle}");
                //}
                var usingSubstyle = "";
                if (_style == RecraftStyle.digital_illustration)
                {
                    usingSubstyle = _substyleDigital.ToString();
                }
                else if (_style == RecraftStyle.realistic_image)
                {
                    usingSubstyle = _substyleRealistic.ToString();
                }
                else if (_style == RecraftStyle.vector_illustration)
                {
                    usingSubstyle = _substyleVector.ToString();
                }
                else
                {
                    Console.WriteLine("err.");
                    usingSubstyle = "any";
                }
                var generationResult = await _recraftClient.GenerateImageAsync(usingPrompt, usingSubstyle, _style.ToString(), _imageSize);
                Logger.Log($"\tFrom Recraft: {promptDetails.Show()} '{generationResult.Created}'");
                _stats.RecraftImageGenerationSuccessCount++;
                var theUrl = generationResult.Data[0].Url;

                var headResponse = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, theUrl));
                var contentType = headResponse.Content.Headers.ContentType?.MediaType;

                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Url = theUrl,
                    ContentType = contentType,
                    PromptDetails = promptDetails,
                    ImageGenerator = ImageGeneratorApiType.Recraft
                };
            }
            catch (Exception ex)
            {
                Logger.Log($"Recraft error: {ex.Message}");
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Recraft };
            }
            finally
            {
                _recraftSemaphore.Release();
            }
        }

        public string GetFullStyleName(string style, string substyle)
        {
            switch (style)
            {
                case "digital_illustration":
                    return $"{nameof(RecraftStyle.digital_illustration)} - {substyle}";
                case "realistic_image":
                    return $"{nameof(RecraftStyle.realistic_image)} - {substyle}";
                case "vector_illustration":
                    return $"{nameof(RecraftStyle.vector_illustration)} - {substyle}";
                case "any":
                    return "Any Style";
                default:
                    return "Unknown";
            }
        }
    }
}
