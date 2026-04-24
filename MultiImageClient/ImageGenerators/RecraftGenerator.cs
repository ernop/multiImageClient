using IdeogramAPIClient;


using RecraftAPIClient;

using SixLabors.ImageSharp;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        private RecraftModel _model;
        private ImageGeneratorApiType _apiType;
        private string _artistic_level;
        private string _name;

        public ImageGeneratorApiType ApiType => _apiType;

        private static ImageGeneratorApiType ApiTypeFor(RecraftModel model) => model switch
        {
            RecraftModel.recraftv4 => ImageGeneratorApiType.RecraftV4,
            RecraftModel.recraftv4pro => ImageGeneratorApiType.RecraftV4Pro,
            _ => ImageGeneratorApiType.Recraft,
        };

        public string GetGeneratorSpecPart()
        {
            if (string.IsNullOrEmpty(_name))
            {
                var usingSubstyle = "";
                if (_style == RecraftStyle.digital_illustration)
                {
                    usingSubstyle = "\n" + _substyleDigital.ToString();
                }
                else if (_style == RecraftStyle.realistic_image)
                {
                    usingSubstyle = "\n" + _substyleRealistic.ToString();
                }
                else if (_style == RecraftStyle.vector_illustration)
                {
                    usingSubstyle = "\n"+_substyleVector.ToString();
                }
                else if (_style == RecraftStyle.any)
                {
                    usingSubstyle = "";
                }
                else
                {
                    throw new Exception("x");
                }
                var alpart = "";
                if (!string.IsNullOrEmpty(_artistic_level) && _artistic_level != "0" )
                {
                    alpart = $"\nartistic level {_artistic_level}";
                }
                var using2 = string.Join('\n', usingSubstyle.Split('\n').Where(el => !string.IsNullOrWhiteSpace(el)));
                return $"{_model}\n{_style}\n{using2}{alpart}";
            }
            else
            {
                return $"{_name}";
            }
        }

        public RecraftGenerator(string apiKey, int maxConcurrency, RecraftImageSize size, RecraftStyle style, RecraftVectorIllustrationSubstyle? substyleVector, RecraftDigitalIllustrationSubstyle? substyleDigital, RecraftRealisticImageSubstyle? substyleRealistic, MultiClientRunStats stats, string name, string artistic_level = "", RecraftModel model = RecraftModel.recraftv3)
        {
            _recraftClient = new RecraftClient(apiKey);
            _recraftSemaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient();
            _artistic_level = artistic_level.ToString() ?? "";
            // so actually, ""


            // we probably could use some validation here.
            _style = style;
            _substyleVector = substyleVector;
            _substyleDigital = substyleDigital;
            _substyleRealistic = substyleRealistic;

            _imageSize = size;
            _stats = stats;
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _model = model;
            _apiType = ApiTypeFor(model);
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
            var res = $"{_model}{_name}_{_imageSize}_{_style}_{usingSubstyle}";
            return res;
        }

        // https://www.recraft.ai/docs/api-reference/pricing
        public decimal GetCost()
        {
            // V4 Pro charges a flat premium regardless of raster/vector style.
            if (_model == RecraftModel.recraftv4pro)
            {
                return _style == RecraftStyle.vector_illustration ? 0.30m : 0.25m;
            }

            // V2 / V3 / V4 raster: $0.04 (V2: $0.022); vector: $0.08 (V2: $0.044).
            var isVector = _style == RecraftStyle.vector_illustration;
            return _model switch
            {
                RecraftModel.recraftv2 => isVector ? 0.044m : 0.022m,
                _ => isVector ? 0.08m : 0.04m,
            };
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
            var alpart = "";
            if (!string.IsNullOrEmpty(_artistic_level))
            {
                alpart = $"artistic level {_artistic_level}";
            }

            var rightsideContents = new List<string>() { _model.ToString(), _name, _style.ToString(), usingSubstyle, alpart };
            return rightsideContents;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
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
                else if (_style == RecraftStyle.any)
                {
                    usingSubstyle = "";
                }
                else
                {
                    Console.WriteLine("err.");
                    usingSubstyle = "any";
                }

                usingSubstyle = Regex.Replace(usingSubstyle, @"^_([\d])", "$1");
                var generationResult = await _recraftClient.GenerateImageAsync(usingPrompt, _artistic_level, usingSubstyle, _style.ToString(), _imageSize, _model);
                Logger.Log($"\tFrom Recraft: {promptDetails.Show()} '{generationResult.Created}'");
                _stats.RecraftImageGenerationSuccessCount++;
                var theUrl = generationResult.Data[0].Url;

                var headResponse = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, theUrl));
                var contentType = headResponse.Content.Headers.ContentType?.MediaType;

                return new TaskProcessResult
                {
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    IsSuccess = true,
                    Url = theUrl,
                    ContentType = contentType,
                    PromptDetails = promptDetails,
                    ImageGenerator = _apiType
                };
            }
            catch (Exception ex)
            {
                Logger.Log($"Recraft error: {ex.Message}");
                var jsonPart = ex.Message.Split(" - ").Last().Trim();

                using var doc = JsonDocument.Parse(jsonPart);
                var detailedError = doc.RootElement.GetProperty("code").GetString();
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = detailedError, PromptDetails = promptDetails, ImageGenerator = _apiType, ImageGeneratorDescription = generator.GetGeneratorSpecPart() };
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
                    return "Any";
                default:
                    return "Unknown";
            }
        }
    }
}
