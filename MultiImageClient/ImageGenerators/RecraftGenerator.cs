using IdeogramAPIClient;


using RecraftAPIClient;

using SixLabors.ImageSharp;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
        private string _inputImagePath;
        private string _sizeOverride;
        private int _imageCount;
        private int? _randomSeed;
        private float _imageStrength;

        public ImageGeneratorApiType ApiType => _apiType;

        private static ImageGeneratorApiType ApiTypeFor(RecraftModel model) => model switch
        {
            RecraftModel.recraftv4 => ImageGeneratorApiType.RecraftV4,
            RecraftModel.recraftv4pro => ImageGeneratorApiType.RecraftV4Pro,
            RecraftModel.recraftv4_1 => ImageGeneratorApiType.RecraftV41,
            RecraftModel.recraftv4_1_pro => ImageGeneratorApiType.RecraftV41Pro,
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

        // sizeOverride: raw size string sent to the API instead of the enum —
        //   either "WxH" or an aspect ratio like "16:9"; "" (empty, non-null)
        //   omits size entirely so Recraft auto-selects from the prompt.
        // imageCount: n per call, 1-6. imageStrength: imageToImage strength in
        //   [0,1] when inputImagePath is set (0 = near-identical source). The
        //   scale is VERY aggressive: observed 2026-07-20 with recraftv4_1,
        //   both 0.35 and 0.2 discarded the source composition entirely;
        //   only 0.1 preserved subject + layout while still applying the
        //   prompt. Default 0.1 keeps the pasted image recognizable, which is
        //   what "reference/guide" means in our UIs.
        public RecraftGenerator(string apiKey, int maxConcurrency, RecraftImageSize size, RecraftStyle style, RecraftVectorIllustrationSubstyle? substyleVector, RecraftDigitalIllustrationSubstyle? substyleDigital, RecraftRealisticImageSubstyle? substyleRealistic, MultiClientRunStats stats, string name, string artistic_level = "", RecraftModel model = RecraftModel.recraftv3, string inputImagePath = null, string sizeOverride = null, int imageCount = 1, int? randomSeed = null, float imageStrength = 0.1f)
        {
            _inputImagePath = inputImagePath;
            _sizeOverride = sizeOverride;
            _imageCount = Math.Clamp(imageCount, 1, 6);
            _randomSeed = randomSeed;
            _imageStrength = Math.Clamp(imageStrength, 0f, 1f);
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
            // Pro tiers charge a flat premium regardless of raster/vector style.
            // V4.1 Pro is assumed to match V4 Pro until Recraft publishes a delta.
            if (_model == RecraftModel.recraftv4pro || _model == RecraftModel.recraftv4_1_pro)
            {
                return (_style == RecraftStyle.vector_illustration ? 0.30m : 0.25m) * _imageCount;
            }

            // V2 / V3 / V4 / V4.1 raster: $0.04 (V2: $0.022); vector: $0.08 (V2: $0.044).
            var isVector = _style == RecraftStyle.vector_illustration;
            var perImage = _model switch
            {
                RecraftModel.recraftv2 => isVector ? 0.044m : 0.022m,
                _ => isVector ? 0.08m : 0.04m,
            };
            return perImage * _imageCount;
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

                GenerationResponse generationResult;
                if (!string.IsNullOrEmpty(_inputImagePath))
                {
                    // Reference image → /images/imageToImage (works on V3 and
                    // V4/V4.1; API-created custom styles are V3-only, so the old
                    // create-style-then-style_id path silently broke on V4.x).
                    var refBytes = File.ReadAllBytes(_inputImagePath);
                    generationResult = await _recraftClient.ImageToImageAsync(
                        refBytes, usingPrompt, _imageStrength, _model, _imageCount, _randomSeed);
                }
                else
                {
                    // null sizeOverride = use the enum; "" = omit size entirely
                    // (Recraft auto-selects from the prompt).
                    var size = _sizeOverride ?? _imageSize.ToString().TrimStart('_');
                    generationResult = await _recraftClient.GenerateImageAsync(
                        usingPrompt, _artistic_level, usingSubstyle, _style.ToString(),
                        size, _model, styleId: null, n: _imageCount, randomSeed: _randomSeed);
                }
                Logger.Log($"\tFrom Recraft: {promptDetails.Show()} '{generationResult.Created}'");
                _stats.RecraftImageGenerationSuccessCount++;
                var theUrl = generationResult.Data[0].Url;

                if (generationResult.Data.Count == 1)
                {
                    var contentType = await ProbeContentTypeAsync(theUrl);

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

                // n > 1: ImageManager's Url path only handles one image, so
                // download all of them here and hand back base64 entries.
                var images = new List<CreatedBase64Image>();
                string multiContentType = null;
                foreach (var item in generationResult.Data)
                {
                    var download = await DownloadImageAsync(item.Url);
                    multiContentType ??= download.ContentType;
                    var bytes = download.Bytes;
                    images.Add(new CreatedBase64Image { bytesBase64 = Convert.ToBase64String(bytes), newPrompt = "" });
                }

                return new TaskProcessResult
                {
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    IsSuccess = true,
                    Base64ImageDatas = images,
                    ContentType = multiContentType,
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

        private async Task<string> ProbeContentTypeAsync(string url)
        {
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage response = null;
            Exception traceError = null;
            try
            {
                response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                if (!response.IsSuccessStatusCode)
                {
                    traceError = new HttpRequestException(
                        $"Recraft image content-type probe returned HTTP {(int)response.StatusCode}.");
                }
                return response.Content.Headers.ContentType?.MediaType;
            }
            catch (Exception ex)
            {
                traceError = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "recraft",
                    "http",
                    "HEAD",
                    url,
                    startedAtUtc,
                    response: new
                    {
                        contentType = response?.Content.Headers.ContentType?.MediaType,
                        contentLength = response?.Content.Headers.ContentLength,
                    },
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: traceError,
                    metadata: new { operation = "content-type-probe" });
                response?.Dispose();
            }
        }

        private async Task<(byte[] Bytes, string ContentType)> DownloadImageAsync(string url)
        {
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage response = null;
            byte[] bytes = null;
            Exception traceError = null;
            try
            {
                response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync();
                return (bytes, response.Content.Headers.ContentType?.MediaType);
            }
            catch (Exception ex)
            {
                traceError = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "recraft",
                    "http",
                    "GET",
                    url,
                    startedAtUtc,
                    response: BinaryResponseMetadata(response, bytes),
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: traceError,
                    metadata: new { operation = "image-download" });
                response?.Dispose();
            }
        }

        private static object BinaryResponseMetadata(HttpResponseMessage response, byte[] bytes)
            => new
            {
                contentType = response?.Content.Headers.ContentType?.MediaType,
                contentLength = response?.Content.Headers.ContentLength,
                byteLength = bytes?.LongLength ?? 0,
                sha256 = bytes == null
                    ? ""
                    : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            };

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
