
using OpenAI.Images;

using System;
using System.Collections.Generic;
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
        private string _quality;
        private string _name;

        public GptImageOneGenerator(string apiKey, int maxConcurrency, string size, string moderation, string quality, MultiClientRunStats stats, string name)
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
            if (_quality != "high")
            {
                qualitypt = $" qual{_quality}";
            }
            var res = $"gpt-1_{_name}{modpt}{qualitypt}";
            return res;
        }

        public List<string> GetRightParts()
        {
            var modpt = "";
            if (_moderation != "low")
            {
                modpt = $" mod{_moderation}";
            }

            var qualitypt = "";
            if (_quality != "high")
            {
                qualitypt = $" qual{_quality}";
            }

            var rightsideContents = new List<string>() { "gpt-image-1", _name , modpt, qualitypt};

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
                    quality = _quality,
                    n = 1,
                    size = _size,
                };

                using var content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(body),
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