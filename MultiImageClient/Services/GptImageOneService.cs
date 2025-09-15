using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;

using BFLAPIClient;

using IdeogramAPIClient;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

using OpenAI;
using OpenAI.Assistants;
using OpenAI.Images;
using OpenAI.Models;

using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;

using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;

namespace MultiImageClient
{
    public class GptImageOneOptions
    {
        public string Prompt { get; set; }
        public int? N { get; set; }
        public string Model { get; set; }
    }

    public class GptImageOneService : IImageGenerationService
    {
        private SemaphoreSlim _gptImageOneSemaphore;
        static readonly HttpClient http = new HttpClient();

        public GptImageOneService(string apiKey, int maxConcurrency)
        {
            //var openAIClient = new OpenAIClient(apiKey);
            //_openAIImageClient = openAIClient.GetImageClient("gpt-image-1");
            _gptImageOneSemaphore = new SemaphoreSlim(maxConcurrency);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }



        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails, MultiClientRunStats stats)
        {
            await _gptImageOneSemaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                stats.GptImageOneRequestCount++;
                var genOptions = new ImageGenerationOptions();
                genOptions.Quality = promptDetails.GptImageOneDetails.quality;

                var body = new
                {
                    model = "gpt-image-1",
                    prompt = promptDetails.Prompt,
                    moderation = "low",
                    quality = "high",
                    //response_format = "png",

                    n = 1,
                    //size = "1024x1024"
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
                return new TaskProcessResult { IsSuccess = true, Base64ImageDatas = b64s,  Url = "", ErrorMessage = "", PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.GptImage1, CreateTotalMs = sw.ElapsedMilliseconds };
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