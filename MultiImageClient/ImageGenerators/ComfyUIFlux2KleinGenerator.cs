#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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
        private readonly int _width;
        private readonly int _height;
        private readonly string _diffusionModelName;
        private readonly string _checkpointName;
        private readonly string _vaeName;
        private readonly string _textEncoderName;
        private readonly string _textEncoder2Name;
        private readonly string _loraName;
        private readonly double _loraModelStrength;
        private readonly double _loraClipStrength;
        private readonly ImageGeneratorApiType _apiType;
        private readonly string _modelName;
        private readonly string _filenamePrefix;
        private readonly string _workflowPathSettingName;
        private readonly bool _warnWhenUncensoredWithoutAblatedEncoder;

        public ComfyUIFlux2KleinGenerator(
            string baseUrl,
            string workflowPath,
            int maxConcurrency,
            MultiClientRunStats stats,
            string name = "uncensored",
            int pollIntervalMs = 1000,
            int timeoutSeconds = 900,
            Flux2KleinResolution resolution = Flux2KleinResolution._1024x1024,
            string diffusionModelName = "",
            string checkpointName = "",
            string vaeName = "",
            string textEncoderName = "",
            string textEncoder2Name = "",
            string loraName = "",
            double loraModelStrength = 0.8,
            double loraClipStrength = 0.8,
            ImageGeneratorApiType apiType = ImageGeneratorApiType.LocalFlux2Klein,
            string modelName = "FLUX.2 Klein 4B",
            string filenamePrefix = "local_flux2_klein_4b",
            string workflowPathSettingName = "ComfyUIFlux2KleinWorkflowPath",
            bool warnWhenUncensoredWithoutAblatedEncoder = true)
        {
            _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            _workflowPath = workflowPath ?? string.Empty;
            _pollIntervalMs = Math.Max(250, pollIntervalMs);
            _timeoutSeconds = Math.Max(30, timeoutSeconds);
            _name = string.IsNullOrWhiteSpace(name) ? "uncensored" : name.Trim();
            (_width, _height) = resolution.GetDimensions();
            _stats = stats;
            _semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Min(_timeoutSeconds, 120)) };
            _diffusionModelName = diffusionModelName?.Trim() ?? "";
            _checkpointName = checkpointName?.Trim() ?? "";
            _vaeName = vaeName?.Trim() ?? "";
            _textEncoderName = textEncoderName?.Trim() ?? "";
            _textEncoder2Name = textEncoder2Name?.Trim() ?? "";
            _loraName = loraName?.Trim() ?? "";
            _loraModelStrength = loraModelStrength;
            _loraClipStrength = loraClipStrength;
            _apiType = apiType;
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "local image model" : modelName.Trim();
            _filenamePrefix = string.IsNullOrWhiteSpace(filenamePrefix) ? "local_comfyui" : filenamePrefix.Trim();
            _workflowPathSettingName = string.IsNullOrWhiteSpace(workflowPathSettingName)
                ? "ComfyUIWorkflowPath"
                : workflowPathSettingName.Trim();
            _warnWhenUncensoredWithoutAblatedEncoder = warnWhenUncensoredWithoutAblatedEncoder;
        }

        public ImageGeneratorApiType ApiType => _apiType;

        public string GetFilenamePart(PromptDetails pd) => $"{_filenamePrefix}_{_name}_{_width}x{_height}";

        public List<string> GetRightParts()
        {
            return new List<string>
            {
                "local ComfyUI",
                _modelName,
                $"{_width}x{_height}",
                _name,
            };
        }

        public string GetGeneratorSpecPart() => $"Local {_modelName} {_width}x{_height} [{_name}]";

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
                promptDetails.RuntimeMeta["size"] = $"{_width}x{_height}";
                promptDetails.RuntimeMeta["endpoint"] = _baseUrl;
                promptDetails.RuntimeMeta["workflow"] = _workflowPath;
                promptDetails.RuntimeMeta["model"] = $"{_modelName} local ComfyUI";
                AddRuntimeMetaIfSet(promptDetails, "diffusionModel", _diffusionModelName);
                AddRuntimeMetaIfSet(promptDetails, "checkpoint", _checkpointName);
                AddRuntimeMetaIfSet(promptDetails, "vae", _vaeName);
                AddRuntimeMetaIfSet(promptDetails, "lora", _loraName);
                if (!string.IsNullOrWhiteSpace(_loraName))
                {
                    promptDetails.RuntimeMeta["loraModelStrength"] = _loraModelStrength.ToString(CultureInfo.InvariantCulture);
                    promptDetails.RuntimeMeta["loraClipStrength"] = _loraClipStrength.ToString(CultureInfo.InvariantCulture);
                }

                var workflow = LoadWorkflow(prompt);

                // Surface which text encoder the workflow actually loads so every
                // run is verifiable. The "uncensored" label only means something
                // if the encoder is the ablated GGUF — warn loudly otherwise.
                var encoders = ExtractEncoderNames(workflow);
                var encoderLabel = encoders.Count > 0 ? string.Join("+", encoders) : "(unknown)";
                promptDetails.RuntimeMeta["textEncoder"] = encoderLabel;
                var looksAblated = encoders.Any(e => e.Contains("abl", StringComparison.OrdinalIgnoreCase));
                if (_warnWhenUncensoredWithoutAblatedEncoder
                    && _name.Contains("uncensored", StringComparison.OrdinalIgnoreCase)
                    && !looksAblated)
                {
                    Logger.Log($"\t[WARN] Local {_modelName} is labeled '{_name}' but its workflow text encoder is '{encoderLabel}', which does NOT look ablated/uncensored. Check {_workflowPath}.");
                }

                var promptId = await QueuePromptAsync(workflow);
                Logger.Log($"\t-> Local {_modelName} queued ComfyUI prompt_id={promptId} [{_width}x{_height}, encoder={encoderLabel}]: {prompt}");

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
                Logger.Log($"\t<- Local {_modelName} FAIL: {ex.Message}");
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
                throw new InvalidOperationException($"settings.json: {_workflowPathSettingName} is empty. Save a ComfyUI API workflow JSON with {{PROMPT}} in the prompt field.");
            }

            if (!File.Exists(_workflowPath))
            {
                throw new FileNotFoundException($"ComfyUI {_modelName} workflow file not found: {_workflowPath}", _workflowPath);
            }
        }

        private JObject LoadWorkflow(string prompt)
        {
            var workflow = JObject.Parse(File.ReadAllText(_workflowPath));
            var replacedPrompt = ReplaceStringTokens(workflow, "{{PROMPT}}", prompt);
            ReplaceStringTokens(workflow, "{{SEED}}", Random.Shared.NextInt64(1, long.MaxValue).ToString());
            ApplyConfiguredModelTokens(workflow);

            if (!replacedPrompt)
            {
                throw new InvalidOperationException(
                    $"Workflow '{_workflowPath}' does not contain a {{PROMPT}} placeholder. Export the ComfyUI workflow in API format and put {{PROMPT}} in the positive prompt text.");
            }

            var unresolved = FindUnresolvedPlaceholders(workflow);
            if (unresolved.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Workflow '{_workflowPath}' still contains unresolved placeholder(s): {string.Join(", ", unresolved)}. "
                    + "Set the matching ComfyUI* field in settings.json or remove the placeholder from the workflow.");
            }

            ApplyResolution(workflow, _width, _height);

            return workflow;
        }

        private void ApplyConfiguredModelTokens(JObject workflow)
        {
            var replacements = new Dictionary<string, string>
            {
                ["{{CHECKPOINT}}"] = _checkpointName,
                ["{{CKPT}}"] = _checkpointName,
                ["{{UNET}}"] = _diffusionModelName,
                ["{{DIFFUSION_MODEL}}"] = _diffusionModelName,
                ["{{VAE}}"] = _vaeName,
                ["{{CLIP}}"] = _textEncoderName,
                ["{{CLIP1}}"] = _textEncoderName,
                ["{{TEXT_ENCODER}}"] = _textEncoderName,
                ["{{TEXT_ENCODER1}}"] = _textEncoderName,
                ["{{CLIP2}}"] = _textEncoder2Name,
                ["{{TEXT_ENCODER2}}"] = _textEncoder2Name,
                ["{{LORA}}"] = _loraName,
                ["{{LORA_STRENGTH_MODEL}}"] = _loraModelStrength.ToString(CultureInfo.InvariantCulture),
                ["{{LORA_STRENGTH_CLIP}}"] = _loraClipStrength.ToString(CultureInfo.InvariantCulture),
            };

            foreach (var pair in replacements)
            {
                if (string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                ReplaceStringTokens(workflow, pair.Key, pair.Value);
            }
        }

        // Force the chosen resolution onto whatever latent-image node the saved
        // workflow uses, so the enum wins over the hard-coded width/height in the
        // JSON. Targets any EmptyLatentImage-style node (class_type ending in
        // "LatentImage", e.g. EmptyFlux2LatentImage).
        private static void ApplyResolution(JObject workflow, int width, int height)
        {
            foreach (var node in workflow.Values().OfType<JObject>())
            {
                var classType = node.Value<string>("class_type");
                if (classType == null || !classType.EndsWith("LatentImage", StringComparison.Ordinal))
                {
                    continue;
                }

                if (node["inputs"] is JObject inputs)
                {
                    inputs["width"] = width;
                    inputs["height"] = height;
                }
            }
        }

        // Collect the clip/text-encoder filenames referenced by any CLIP loader
        // node (CLIPLoader, CLIPLoaderGGUF, DualCLIPLoader, ...) so callers can
        // confirm which encoder a run actually used.
        private static List<string> ExtractEncoderNames(JObject workflow)
        {
            var names = new List<string>();
            foreach (var node in workflow.Values().OfType<JObject>())
            {
                var classType = node.Value<string>("class_type") ?? string.Empty;
                if (!classType.Contains("CLIP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (node["inputs"] is not JObject inputs)
                {
                    continue;
                }

                foreach (var prop in inputs.Properties())
                {
                    if (prop.Name.StartsWith("clip_name", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.Type == JTokenType.String)
                    {
                        var value = prop.Value.Value<string>();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            names.Add(value!);
                        }
                    }
                }
            }

            return names;
        }

        private static SortedSet<string> FindUnresolvedPlaceholders(JToken token)
        {
            var placeholders = new SortedSet<string>(StringComparer.Ordinal);
            CollectUnresolvedPlaceholders(token, placeholders);
            return placeholders;
        }

        private static void CollectUnresolvedPlaceholders(JToken token, SortedSet<string> placeholders)
        {
            if (token is JValue value && value.Type == JTokenType.String)
            {
                var s = value.Value<string>();
                if (s != null)
                {
                    foreach (Match match in Regex.Matches(s, @"\{\{[A-Z0-9_]+\}\}"))
                    {
                        placeholders.Add(match.Value);
                    }
                }
                return;
            }

            foreach (var child in token.Children())
            {
                CollectUnresolvedPlaceholders(child, placeholders);
            }
        }

        private static void AddRuntimeMetaIfSet(PromptDetails promptDetails, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                promptDetails.RuntimeMeta[key] = value;
            }
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

            var endpoint = $"{_baseUrl}/prompt";
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            string? responseText = null;
            try
            {
                response = await _httpClient.PostAsync(
                    endpoint,
                    new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"));
                responseText = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                GenerationTrace.RecordProviderCall(
                    "comfyui",
                    "http",
                    "POST",
                    endpoint,
                    startedAtUtc,
                    request: body,
                    response: responseText,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: ex,
                    metadata: new { model = _modelName, operation = "queue" });
                throw;
            }

            var providerError = response.IsSuccessStatusCode
                ? null
                : new HttpRequestException($"ComfyUI /prompt failed ({(int)response.StatusCode}): {responseText}");
            GenerationTrace.RecordProviderCall(
                "comfyui",
                "http",
                "POST",
                endpoint,
                startedAtUtc,
                request: body,
                response: responseText,
                statusCode: (int)response.StatusCode,
                error: providerError,
                metadata: new { model = _modelName, operation = "queue" });
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
            var endpoint = $"{_baseUrl}/history/{Uri.EscapeDataString(promptId)}";
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            string? text = null;
            try
            {
                response = await _httpClient.GetAsync(endpoint);
                text = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                GenerationTrace.RecordProviderCall(
                    "comfyui",
                    "http",
                    "GET",
                    endpoint,
                    startedAtUtc,
                    request: new { promptId },
                    response: text,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: ex,
                    metadata: new { model = _modelName, operation = "history" });
                throw;
            }

            var providerError = response.IsSuccessStatusCode
                ? null
                : new HttpRequestException($"ComfyUI /history/{promptId} failed ({(int)response.StatusCode}): {text}");
            GenerationTrace.RecordProviderCall(
                "comfyui",
                "http",
                "GET",
                endpoint,
                startedAtUtc,
                request: new { promptId },
                response: text,
                statusCode: (int)response.StatusCode,
                error: providerError,
                metadata: new { model = _modelName, operation = "history" });
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
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            byte[]? bytes = null;
            try
            {
                response = await _httpClient.GetAsync(url);
                bytes = await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                GenerationTrace.RecordProviderCall(
                    "comfyui",
                    "http",
                    "GET",
                    url,
                    startedAtUtc,
                    request: new { filename, subfolder, type },
                    response: bytes == null ? null : BinaryResponseMetadata(response, bytes),
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: ex,
                    metadata: new { model = _modelName, operation = "view" });
                throw;
            }

            var responseMetadata = BinaryResponseMetadata(response, bytes);
            var errorText = response.IsSuccessStatusCode || !CanTraceAsText(response)
                ? null
                : Encoding.UTF8.GetString(bytes);
            var providerError = response.IsSuccessStatusCode
                ? null
                : new HttpRequestException($"ComfyUI /view failed ({(int)response.StatusCode}): {errorText ?? "[binary response]"}");
            GenerationTrace.RecordProviderCall(
                "comfyui",
                "http",
                "GET",
                url,
                startedAtUtc,
                request: new { filename, subfolder, type },
                response: new
                {
                    binary = responseMetadata,
                    errorBody = errorText,
                },
                statusCode: (int)response.StatusCode,
                error: providerError,
                metadata: new { model = _modelName, operation = "view" });
            if (!response.IsSuccessStatusCode)
            {
                var text = Encoding.UTF8.GetString(bytes);
                throw new HttpRequestException($"ComfyUI /view failed ({(int)response.StatusCode}): {text}");
            }

            return bytes;
        }

        private static object BinaryResponseMetadata(HttpResponseMessage? response, byte[] bytes)
        {
            return new
            {
                contentType = response?.Content.Headers.ContentType?.MediaType,
                byteLength = bytes.LongLength,
                format = GuessBinaryFormat(bytes),
            };
        }

        private static bool CanTraceAsText(HttpResponseMessage response)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            return string.IsNullOrWhiteSpace(mediaType)
                || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
        }

        private static string GuessBinaryFormat(byte[] bytes)
        {
            if (bytes.Length >= 4
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "png";
            }
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "jpeg";
            }
            if (bytes.Length >= 12
                && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            {
                return "webp";
            }
            return "unknown";
        }
    }
}
