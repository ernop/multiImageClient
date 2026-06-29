#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed class ComfyUIFlux2KleinGenerator : IImageGenerator
    {
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly string _baseUrl;
        private readonly string _workflowPath;
        private readonly int _pollIntervalMs;
        private readonly int _timeoutSeconds;
        private readonly string _name;

        public ComfyUIFlux2KleinGenerator(
            string baseUrl,
            string workflowPath,
            int maxConcurrency,
            MultiClientRunStats stats,
            string name = "uncensored",
            int pollIntervalMs = 1000,
            int timeoutSeconds = 900)
        {
            _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            _workflowPath = workflowPath ?? string.Empty;
            _pollIntervalMs = Math.Max(250, pollIntervalMs);
            _timeoutSeconds = Math.Max(30, timeoutSeconds);
            _name = string.IsNullOrWhiteSpace(name) ? "uncensored" : name.Trim();
            _stats = stats;
            _semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Min(_timeoutSeconds, 120)) };
        }

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.LocalFlux2Klein;

        public string GetFilenamePart(PromptDetails pd) => $"local_flux2_klein_4b_{_name}";

        public List<string> GetRightParts()
        {
            return new List<string>
            {
                "local ComfyUI",
                "FLUX.2 Klein 4B",
                _name,
            };
        }

        public string GetGeneratorSpecPart() => $"Local FLUX.2 Klein 4B [{_name}]";

        public decimal GetCost() => 0m;

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.LocalImageGenerationRequestCount++;
                ValidateConfig();

                var prompt = promptDetails.Prompt ?? string.Empty;
                promptDetails.RuntimeMeta["size"] = "1024x1024";
                promptDetails.RuntimeMeta["endpoint"] = _baseUrl;
                promptDetails.RuntimeMeta["workflow"] = _workflowPath;
                promptDetails.RuntimeMeta["model"] = "FLUX.2 Klein 4B local ComfyUI";

                var workflow = LoadWorkflow(prompt);
                var promptId = await QueuePromptAsync(workflow);
                Logger.Log($"\t-> Local FLUX.2 Klein queued ComfyUI prompt_id={promptId}: {prompt}");

                var images = await WaitForImagesAsync(promptId);
                sw.Stop();

                if (images.Count == 0)
                {
                    _stats.LocalImageGenerationErrorCount++;
                    return Fail("ComfyUI completed but produced no output images.", promptDetails, generator, sw.ElapsedMilliseconds);
                }

                _stats.LocalImageGenerationSuccessCount++;
                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = images.Select(bytes => new CreatedBase64Image
                    {
                        bytesBase64 = Convert.ToBase64String(bytes),
                        newPrompt = prompt,
                    }).ToList(),
                    ContentType = "image/png",
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _stats.LocalImageGenerationErrorCount++;
                Logger.Log($"\t<- Local FLUX.2 Klein FAIL: {ex.Message}");
                return Fail(ex.Message, promptDetails, generator, sw.ElapsedMilliseconds);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private TaskProcessResult Fail(string message, PromptDetails pd, IImageGenerator generator, long elapsedMs)
        {
            return new TaskProcessResult
            {
                IsSuccess = false,
                ErrorMessage = message,
                PromptDetails = pd,
                ImageGenerator = ApiType,
                ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                CreateTotalMs = elapsedMs,
            };
        }

        private void ValidateConfig()
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                throw new InvalidOperationException("settings.json: ComfyUIBaseUrl is empty. Set it to http://127.0.0.1:8188 after starting ComfyUI.");
            }

            if (string.IsNullOrWhiteSpace(_workflowPath))
            {
                throw new InvalidOperationException("settings.json: ComfyUIFlux2KleinWorkflowPath is empty. Save a ComfyUI API workflow JSON with {{PROMPT}} in the prompt field.");
            }

            if (!File.Exists(_workflowPath))
            {
                throw new FileNotFoundException($"ComfyUI FLUX.2 Klein workflow file not found: {_workflowPath}", _workflowPath);
            }
        }

        private JObject LoadWorkflow(string prompt)
        {
            var workflow = JObject.Parse(File.ReadAllText(_workflowPath));
            var replacedPrompt = ReplaceStringTokens(workflow, "{{PROMPT}}", prompt);
            ReplaceStringTokens(workflow, "{{SEED}}", Random.Shared.NextInt64(1, long.MaxValue).ToString());

            if (!replacedPrompt)
            {
                throw new InvalidOperationException(
                    $"Workflow '{_workflowPath}' does not contain a {{PROMPT}} placeholder. Export the ComfyUI workflow in API format and put {{PROMPT}} in the positive prompt text.");
            }

            return workflow;
        }

        private static bool ReplaceStringTokens(JToken token, string marker, string replacement)
        {
            var replaced = false;
            if (token is JValue value && value.Type == JTokenType.String)
            {
                var s = value.Value<string>();
                if (s != null && s.Contains(marker, StringComparison.Ordinal))
                {
                    value.Value = s.Replace(marker, replacement, StringComparison.Ordinal);
                    return true;
                }
                return false;
            }

            foreach (var child in token.Children())
            {
                replaced |= ReplaceStringTokens(child, marker, replacement);
            }

            return replaced;
        }

        private async Task<string> QueuePromptAsync(JObject workflow)
        {
            var body = new JObject
            {
                ["prompt"] = workflow,
                ["client_id"] = Guid.NewGuid().ToString("N"),
            };

            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/prompt",
                new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"));
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ComfyUI /prompt failed ({(int)response.StatusCode}): {responseText}");
            }

            var json = JObject.Parse(responseText);
            var promptId = json.Value<string>("prompt_id");
            if (string.IsNullOrWhiteSpace(promptId))
            {
                throw new InvalidOperationException($"ComfyUI /prompt response had no prompt_id: {responseText}");
            }

            return promptId;
        }

        private async Task<List<byte[]>> WaitForImagesAsync(string promptId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(_timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var history = await GetHistoryAsync(promptId);
                var entry = ExtractHistoryEntry(history, promptId);
                if (entry != null)
                {
                    var images = await DownloadImagesFromHistoryAsync(entry);
                    if (images.Count > 0)
                    {
                        return images;
                    }
                }

                await Task.Delay(_pollIntervalMs);
            }

            throw new TimeoutException($"Timed out waiting {_timeoutSeconds}s for ComfyUI prompt_id={promptId}.");
        }

        private async Task<JObject> GetHistoryAsync(string promptId)
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/history/{Uri.EscapeDataString(promptId)}");
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ComfyUI /history/{promptId} failed ({(int)response.StatusCode}): {text}");
            }

            return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
        }

        private static JObject? ExtractHistoryEntry(JObject history, string promptId)
        {
            if (history.TryGetValue(promptId, out var keyed) && keyed is JObject keyedObject)
            {
                return keyedObject;
            }

            return history["outputs"] is JObject ? history : null;
        }

        private async Task<List<byte[]>> DownloadImagesFromHistoryAsync(JObject historyEntry)
        {
            var images = new List<byte[]>();
            var outputs = historyEntry["outputs"] as JObject;
            if (outputs == null)
            {
                return images;
            }

            foreach (var output in outputs.Properties())
            {
                var imageArray = output.Value["images"] as JArray;
                if (imageArray == null)
                {
                    continue;
                }

                foreach (var imageToken in imageArray.OfType<JObject>())
                {
                    var filename = imageToken.Value<string>("filename");
                    var type = imageToken.Value<string>("type") ?? "output";
                    var subfolder = imageToken.Value<string>("subfolder") ?? "";
                    if (string.IsNullOrWhiteSpace(filename))
                    {
                        continue;
                    }

                    images.Add(await DownloadComfyImageAsync(filename, subfolder, type));
                }
            }

            return images;
        }

        private async Task<byte[]> DownloadComfyImageAsync(string filename, string subfolder, string type)
        {
            var url = $"{_baseUrl}/view?filename={Uri.EscapeDataString(filename)}"
                + $"&subfolder={Uri.EscapeDataString(subfolder)}"
                + $"&type={Uri.EscapeDataString(type)}";
            var response = await _httpClient.GetAsync(url);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
            {
                var text = Encoding.UTF8.GetString(bytes);
                throw new HttpRequestException($"ComfyUI /view failed ({(int)response.StatusCode}): {text}");
            }

            return bytes;
        }
    }
}
