using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // OpenAI `gpt-image-2` — released 2026-04-21. Uses the standard
    // Images API at /v1/images/generations. Accepts `size`, `quality`
    // (low/medium/high only; no `auto`), and `n`. `input_fidelity` is
    // ignored by the model and must not be sent. Transparent backgrounds
    // are not supported. Pricing is token-based ($30/1M output tokens),
    // so there is no clean per-image cost table — GetCost() returns a
    // placeholder until OpenAI publishes per-size averages.
    //
    // Popular sizes: 1024x1024, 1536x1024, 1024x1536, 2048x2048, 2048x1152,
    // 3840x2160, 2160x3840, or "auto". Arbitrary resolutions are also
    // allowed under the constraints: edges multiple of 16, max edge 3840,
    // total pixels in [655360, 8294400], long:short edge ratio <= 3:1.
    public class GptImage2Generator : IImageGenerator
    {
        private const string ModelId = "gpt-image-2";

        private readonly SemaphoreSlim _semaphore;
        // gpt-image-2 at high/2K routinely takes 2-3 minutes; default 100s is too short.
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        private readonly MultiClientRunStats _stats;
        private readonly string _size;
        private readonly string _moderation;
        private readonly OpenAIGPTImageOneQuality _quality;
        private readonly string _name;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GptImage2;

        public GptImage2Generator(string apiKey, int maxConcurrency, string size, string moderation, OpenAIGPTImageOneQuality quality, MultiClientRunStats stats, string name)
        {
            if (quality == OpenAIGPTImageOneQuality.auto)
            {
                throw new ArgumentException("gpt-image-2 does not accept quality=auto; choose low, medium, or high.", nameof(quality));
            }

            _semaphore = new SemaphoreSlim(maxConcurrency);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _size = size;
            _moderation = moderation;
            _quality = quality;
            _name = name ?? "";
            _stats = stats;
        }

        public string GetGeneratorSpecPart() => string.IsNullOrEmpty(_name) ? ModelId : _name;

        public string GetFilenamePart(PromptDetails pd)
        {
            var modpt = string.IsNullOrEmpty(_moderation) || _moderation == "low" ? "" : $" mod{_moderation}";
            var qualitypt = $" qual{_quality}";
            return $"gpt-2_{_name}{modpt}{_size}{qualitypt}";
        }

        // Token-based pricing. Until per-image averages are published this
        // returns a conservative estimate derived from the documented
        // $30/1M output-token rate and typical token counts seen for
        // gpt-image-1 at the same size; treat as a ceiling, not a bill.
        public decimal GetCost()
        {
            return _quality switch
            {
                OpenAIGPTImageOneQuality.low => 0.02m,
                OpenAIGPTImageOneQuality.medium => 0.08m,
                OpenAIGPTImageOneQuality.high => 0.25m,
                _ => 0.25m,
            };
        }

        public List<string> GetRightParts()
        {
            var modpt = $" moderation {_moderation}";
            var qualitypt = $"quality {_quality}";
            var sizept = $"size {_size}";
            return new List<string> { ModelId, _name, sizept, qualitypt, modpt };
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GptImage2RequestCount++;

                object body;
                if (string.IsNullOrEmpty(_moderation))
                {
                    body = new
                    {
                        model = ModelId,
                        prompt = promptDetails.Prompt,
                        quality = _quality.ToString(),
                        n = 1,
                        size = _size,
                    };
                }
                else
                {
                    body = new
                    {
                        model = ModelId,
                        prompt = promptDetails.Prompt,
                        moderation = _moderation,
                        quality = _quality.ToString(),
                        n = 1,
                        size = _size,
                    };
                }

                using var content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json");

                using var resp = await _http.PostAsync(
                    "https://api.openai.com/v1/images/generations",
                    content);

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"\t\tXXXXXXXXXXXXX == Fail: {promptDetails.Prompt}");
                    _stats.GptImage2RefusedCount++;

                    var errBody = await resp.Content.ReadAsStringAsync();
                    string errorMessage;
                    try
                    {
                        using var errDoc = JsonDocument.Parse(errBody);
                        errorMessage = errDoc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? errBody;
                    }
                    catch
                    {
                        errorMessage = errBody;
                    }
                    var cleanedMessage = errorMessage.Split("If you believe").First().Trim();
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = cleanedMessage,
                        PromptDetails = promptDetails,
                        ImageGenerator = ImageGeneratorApiType.GptImage2,
                        CreateTotalMs = sw.ElapsedMilliseconds,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                    };
                }

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                var b64s = new List<CreatedBase64Image>();
                foreach (var el in data.EnumerateArray())
                {
                    var b64 = el.GetProperty("b64_json").GetString();
                    b64s.Add(new CreatedBase64Image { bytesBase64 = b64, newPrompt = "" });
                }

                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = b64s,
                    Url = "",
                    ErrorMessage = "",
                    PromptDetails = promptDetails,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    ImageGenerator = ImageGeneratorApiType.GptImage2,
                    CreateTotalMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\t\t{promptDetails.Prompt} Error: {ex.Message}");
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    ImageGenerator = ImageGeneratorApiType.GptImage2,
                    CreateTotalMs = sw.ElapsedMilliseconds
                };
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
