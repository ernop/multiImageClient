
using OpenAI.Images;

using System;
using System.ClientModel;
using System.Collections.Generic;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;

using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Drawing;

namespace MultiImageClient
{
    public class GptImageOneGenerator : IImageGenerator
    {
        private SemaphoreSlim _gptImageOneSemaphore;
        static readonly HttpClient http = new HttpClient();
        private MultiClientRunStats _stats;

        public GptImageOneGenerator(string apiKey, int maxConcurrency, MultiClientRunStats stats)
        {
            _gptImageOneSemaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }



        public string GetFilenamePart(PromptDetails pd)
        {
            var res = $"";
            return res;
        }

        public Bitmap GetLabelBitmap(int width)
        {
            throw new NotImplementedException();
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails)
        {
            await _gptImageOneSemaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GptImageOneRequestCount++;
                var genOptions = new ImageGenerationOptions();
                genOptions.Quality = "high";

                var body = new
                {
                    model = "gpt-image-1",
                    prompt = promptDetails.Prompt,
                    moderation = "low",
                    quality = "high",
                    n = 1,
                    size = "auto",
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