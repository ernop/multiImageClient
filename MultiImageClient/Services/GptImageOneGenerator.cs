
using OpenAI.Images;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{

    public class GptImageOneGenerator : IImageGenerator
    {
        private SemaphoreSlim _gptImageOneSemaphore;
        static readonly HttpClient http = new HttpClient();
        private MultiClientRunStats _stats;
        private string _size;
        private string _moderation;
        private OpenAIGPTImageOneQuality _quality;
        private string _name;

        public GptImageOneGenerator(string apiKey, int maxConcurrency, string size, string moderation, OpenAIGPTImageOneQuality quality, MultiClientRunStats stats, string name)
        {
            _gptImageOneSemaphore = new SemaphoreSlim(maxConcurrency);

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

            _size = size;
            _moderation = moderation;
            _quality = quality;
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _stats = stats;

        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var modpt = "";
            if (_moderation != "low")
            {
                modpt = $" mod{_moderation}";
            }

            var qualitypt = "";
            if (_quality != OpenAIGPTImageOneQuality.auto)
            {
                qualitypt = $" qual{_quality}";
            }

            var sizept = _size.ToString();

            var res = $"gpt-1_{_name}{modpt}{sizept}{qualitypt}";
            return res;
        }
        public decimal GetCost()
        {
            if (_size == "1024x1024")
            {
                switch (_quality)
                {
                    case OpenAIGPTImageOneQuality.low:
                        return 0.01088m;
                    case OpenAIGPTImageOneQuality.medium:
                        return 0.04224m;
                    case OpenAIGPTImageOneQuality.high:
                        return 0.1664m;
                    default:
                        throw new Exception("Swe");
                }
            }
            else if (_size == "1024x1536")
            {
                switch (_quality)
                {
                    case OpenAIGPTImageOneQuality.low:
                        return 0.01632m;
                    case OpenAIGPTImageOneQuality.medium:
                        return 0.06336m;
                    case OpenAIGPTImageOneQuality.high:
                        return 0.24960m;
                    default:
                        throw new Exception("S");
                }
            }
            else if (_size == "1536x1024")
            {
                switch (_quality)
                {
                    case OpenAIGPTImageOneQuality.low:
                        return 0.016m;
                    case OpenAIGPTImageOneQuality.medium:
                        return 0.06272m;
                    case OpenAIGPTImageOneQuality.high:
                        return 0.24832m;
                    default:
                        throw new Exception("S");
                }
            }
            else
            {
                throw new Exception("bad size.");
            }
        }

        public List<string> GetRightParts()
        {
            var modpt = "";
            modpt = $" moderation {_moderation}";

            var qualitypt = "";
            qualitypt = $"quality {_quality}";

            var sizept = $"size {_size.ToString()}";
            var rightsideContents = new List<string>() { "gpt-image-1", _name, sizept, qualitypt, modpt };

            return rightsideContents;
        }


        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails)
        {
            await _gptImageOneSemaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GptImageOneRequestCount++;
                var body = new
                {
                    model = "gpt-image-1",
                    prompt = promptDetails.Prompt,
                    moderation = _moderation,
                    quality = _quality.ToString(),
                    n = 1,
                    size = _size,
                };

                using var content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json");

                using var resp = await http.PostAsync(
                    "https://api.openai.com/v1/images/generations",
                    content);

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"\t\tXXXXXXXXXXXXX == Fail: {promptDetails.Prompt}");
                }
                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync();
                //await File.WriteAllTextAsync("response.json", json);
                using var doc = JsonDocument.Parse(json);
                var qq = doc.RootElement.GetProperty("data");
                var ll = qq.GetArrayLength();
                var b64s = new List<string>();
                foreach (var el in qq.EnumerateArray())
                {
                    var b64 = el.GetProperty("b64_json").GetString();
                    b64s.Add(b64);
                }


                Console.WriteLine($"Generated:{promptDetails.Prompt}");
                return new TaskProcessResult { IsSuccess = true, Base64ImageDatas = b64s, Url = "", ErrorMessage = "", PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.GptImage1, CreateTotalMs = sw.ElapsedMilliseconds };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\t\t{promptDetails.Prompt} Error: {ex.Message}");
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.GptImage1, CreateTotalMs = sw.ElapsedMilliseconds };
            }
            finally
            {
                _gptImageOneSemaphore.Release();
            }
        }

    }
}