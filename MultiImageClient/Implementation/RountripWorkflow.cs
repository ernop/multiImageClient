using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;
using System.Security.Cryptography;

namespace MultiImageClient
{
    /// Reads the clipboard and returns a text string if the item in the clipboard is a png image. if it isn't, it returns '' if it is, it returns a descriptive text string with the image size etc.
    public class RoundTripWorkflow
    {
        private MultiClientRunStats? _stats;
        private Settings? _settings;
        private int _concurrency;
        private ImageManager? _imageManager;
        private IEnumerable<IImageGenerator>? _generators;
        private static readonly HttpClient httpClient = new HttpClient();


        private static byte[] ComputeFingerprint(byte[] data)
        {
            return SHA256.HashData(data);
        }

        private static byte[]? GetImageFromClipboard()
        {
            byte[]? clipboardBytes = null;

            var thread = new Thread(() =>
            {
                try
                {
                    if (!Clipboard.ContainsImage())
                    {
                        return;
                    }

                    using var image = Clipboard.GetImage();
                    if (image == null)
                    {

                        return;
                    }

                    using var ms = new MemoryStream();
                    image.Save(ms, ImageFormat.Png);
                    clipboardBytes = ms.ToArray();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to read image from clipboard: {ex.Message}");
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            return clipboardBytes;
        }


        private async Task DoWorkAsync(byte[] imageBytes)
        {

            var promptString = new PromptDetails();
            var usingDesc = "\"You are an image describer. Produce a complete, literal description of the image and then careful inferences.  Note: for every human appearing in the image, you must include their sex, age, and state of dress as well as exactly what they're wearing. Say what the image is like and its medium (photo, digital art, painting, etc.). State file/format details if available. Describe colors, lighting, textures for everything. Give composition and exact positioning/layout of all objects including their orientation, which direction they're facing, relative and absolute sizing, rotation state. If any text appears, transcribe it fully and describe typography: font family guess, weight, style, effects, color, spacing, alignment. Provide enough detail to fully reproduce the image from scratch. For any beings/humans, do NOT skimp: include estimated sex, race, ethnicity, emotional state, attitude, viewpoint, sex, gender,  attractiveness estimate, defects and/or good features, stylistic ethnicity cues, style group culturally, job/role if suggested or deriveable, mood/attitude/emotions toward self, the scene, objects within it or the viewer/artist, their current actions in detail, appearance (hair color/style/length), facing direction and position, estimated relationship to others, goals/intentions and brief history/backstory hypotheses. Speculate wildly. Also include detailed description of their clothing, body, skin, form, shape, muscles, curves, features, emotion and artistically as well as very specifically and in detail, for every piece of clothing they have on, or not. generate a helpful set of instructions for the describer telling them what I need: I need what the image is like, its format (photo, digital art, painting etc), the colors, lighting, textures of everything. Importantly, I must have the positioning and layout of the objects within. I must have all details about text, font, style, if any is present. I need full details for a complete reproduction of the image. AND I need emotions if any. For any beings or humans, I need age, sex, apparent gender, attractiveness, ethnicity, style group, job, apparent mood, attitude, emotions, history, appearance, hair color, direction and posisition, estimated relationship to the others, goals etc. Do NOT skimp on this part. Output dense details, not necessarily in sentence form, no newlines, usefully densely descriptive and specific, non-repetitive. don't waste time with preludes or summaries or warnings/guides to interpret. Put the overall description at the start, giving the overall description of the scene, then fill in more and more with specific details as you go. include both practical facts about the image as well as overall judgements and descriptions. The output should be long, detailed, precise, and omit nothing. You will be judged by how accurately you estimate and describe EVERYTHING as well as you can. ";
            var qwenDescription = await DescribeImageWithLocalQwen(imageBytes, usingDesc);
            if (string.IsNullOrEmpty(qwenDescription.Trim()))
            {
                return;
            }
            var pd = new PromptDetails();
            pd.ReplacePrompt(qwenDescription, "qwen", TransformationType.InitialPrompt);
            var generatorTasks = _generators!.Select(async generator =>
            {
                var theCopy = pd.Copy();

                try
                {
                    var result = await generator.ProcessPromptAsync(generator, theCopy);
                    await _imageManager!.ProcessAndSaveAsync(result, generator);
                    Logger.Log($"Finished {generator.GetType().Name} in {result.CreateTotalMs + result.DownloadTotalMs} ms, {result.PromptDetails.Show()}");

                    return result;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Task faulted for {generator.GetType().Name}: {ex.Message}");

                    var res = new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = ex.Message,
                        PromptDetails = theCopy
                    };

                    return res;
                }
            }).ToArray();

            _stats!.PrintStats();
            var results = await Task.WhenAll(generatorTasks);

            try
            {
                var res = await ImageCombiner.CreateRoundtripLayoutImageAsync(imageBytes, results, qwenDescription, _settings);
                // we want to use RenderImageDescribeRendersHorizontally here!
                Logger.Log($"Combined images saved to: {res}");
                ImageCombiner.OpenImageWithDefaultApplication(res);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to combine images: {ex.Message}");
            }
        }

        //  prompt the user to copy an image to the clipboard.
        //  then send that to local model qwen with description text.
        //  then send that out to all the image generators again.
        public async Task<bool> RunAsync(Settings settings, int concurrency, MultiClientRunStats stats)
        {
            _settings = settings;
            _concurrency = concurrency;
            _stats = stats;
            var getter = new GeneratorGroups(settings, concurrency, stats);
            _generators = getter.GetAll();

            _imageManager = new ImageManager(settings, stats);
            while (true)
            {
                var heldNow = GetImageFromClipboard();
                if (heldNow == null)
                {
                    Console.WriteLine("\tcopy an image to the clipboard; y to continue, q to quit.");
                    var input = Console.ReadLine().Trim();

                    if (input == "y")
                    {
                        continue;
                    }
                    else if (input == "q")
                    {
                        break;
                    }
                    else
                    {
                        Console.WriteLine("ok, skipping.");
                    }
                }
                else
                {
                    Console.WriteLine($"\tnew clipboard image detected. {heldNow.Length} bytes. Starting describe => multiimage workflow.");
                    await DoWorkAsync(heldNow);
                }
            }
            return true;
        }

        public static async Task<string> DescribeImageWithLocalQwen(byte[] imageBytes, string prompt)
        {
            try
            {
                string base64Image = Convert.ToBase64String(imageBytes);

                var requestBody = new OllamaChatRequest
                {
                    Model = "qwen2.5vl:7b",
                    Stream = false,
                    KeepAlive = "1h",
                    Messages = new List<OllamaMessage>
                    {
                        new OllamaMessage
                        {
                            Role = "user",
                            Content = prompt,
                            Images = new List<string> { base64Image }
                        }
                    }
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                string jsonBody = JsonSerializer.Serialize(requestBody, jsonOptions);
                var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("http://127.0.0.1:11434/api/chat", content);
                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync();
                var ollamaResponse = JsonSerializer.Deserialize<DescribeImageResponse>(responseString, jsonOptions);

                return ollamaResponse?.Message?.Content ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error describing image with Ollama: {ex.Message}\r\n{ex}");
                return string.Empty;
            }
        }

        private class OllamaChatRequest
        {
            [JsonPropertyName("model")]
            public required string Model { get; set; }

            [JsonPropertyName("stream")]
            public required bool Stream { get; set; }

            [JsonPropertyName("keep_alive")]
            public required string KeepAlive { get; set; }

            [JsonPropertyName("messages")]
            public required List<OllamaMessage> Messages { get; set; } = new();
        }

        private class OllamaMessage
        {
            [JsonPropertyName("role")]
            public required string Role { get; set; }

            [JsonPropertyName("content")]
            public required string Content { get; set; }

            [JsonPropertyName("images")]
            public required List<string> Images { get; set; } = new();
        }

        private class DescribeImageResponse
        {
            [JsonPropertyName("model")]
            public required string Model { get; set; }

            [JsonPropertyName("created_at")]
            public required DateTime CreatedAt { get; set; }

            [JsonPropertyName("message")]
            public required OllamaResponseMessage Message { get; set; } = new() { Role = string.Empty, Content = string.Empty };

            [JsonPropertyName("done")]
            public required bool Done { get; set; }
        }

        private class OllamaResponseMessage
        {
            [JsonPropertyName("role")]
            public required string Role { get; set; }

            [JsonPropertyName("content")]
            public required string Content { get; set; } = string.Empty;
        }


    }
}
