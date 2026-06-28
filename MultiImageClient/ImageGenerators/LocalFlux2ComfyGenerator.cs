using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class LocalFlux2ComfyGenerator : IImageGenerator
    {
        private static readonly string[] ModelInputNames =
            { "unet_name", "ckpt_name", "model_name", "diffusion_model_name" };

        private static readonly string[] TextEncoderInputNames =
        {
            "clip_name", "clip_name1", "clip_name2", "clip_name3",
            "text_encoder_name", "text_encoder_name1", "text_encoder_name2",
            "t5_name", "qwen_name"
        };

        private static readonly string[] VaeInputNames = { "vae_name" };

        private readonly Settings _settings;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly HttpClient _httpClient;
        private readonly string _endpoint;
        private readonly string _name;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.LocalFlux2Uncensored;

        public LocalFlux2ComfyGenerator(Settings settings, int maxConcurrency, MultiClientRunStats stats, string name = "")
        {
            _settings = settings;
            _stats = stats;
            _name = string.IsNullOrWhiteSpace(name) ? "" : name;
            _semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            _endpoint = NormalizeEndpoint(settings.LocalFlux2ComfyEndpoint);
            EnsureEndpointAllowed(_endpoint, settings.LocalFlux2AllowRemoteEndpoint);

            var timeout = settings.LocalFlux2TimeoutSeconds <= 0 ? 900 : settings.LocalFlux2TimeoutSeconds;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(timeout + 30)
            };
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var suffix = string.IsNullOrEmpty(_name) ? "" : _name;
            return $"{ApiType}{suffix}_{_settings.LocalFlux2Height}x{_settings.LocalFlux2Width}";
        }

        public List<string> GetRightParts()
        {
            return new List<string>
            {
                "Local Flux2",
                "ComfyUI",
                $"{_settings.LocalFlux2Width}x{_settings.LocalFlux2Height}",
                EndpointHostLabel(_endpoint)
            };
        }

        public string GetGeneratorSpecPart()
        {
            return string.IsNullOrEmpty(_name) ? "local-flux2-uncensored" : _name;
        }

        public decimal GetCost()
        {
            return 0m;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            _stats.LocalFlux2RequestCount++;

            try
            {
                if (string.IsNullOrWhiteSpace(_settings.LocalFlux2WorkflowPath))
                {
                    throw new InvalidOperationException(
                        "Settings.LocalFlux2WorkflowPath is required for the local Flux2 ComfyUI generator.");
                }
                if (!File.Exists(_settings.LocalFlux2WorkflowPath))
                {
                    throw new FileNotFoundException(
                        $"Local Flux2 workflow file not found: {_settings.LocalFlux2WorkflowPath}");
                }

                var workflow = LoadApiWorkflow(_settings.LocalFlux2WorkflowPath);
                PatchWorkflow(workflow, promptDetails.Prompt);

                var promptId = await QueuePromptAsync(workflow);
                Logger.Log($"{promptDetails} Local Flux2 queued in ComfyUI: {promptId}");

                var history = await WaitForHistoryAsync(promptId);
                var images = await DownloadOutputImagesAsync(history);
                if (images.Count == 0)
                {
                    throw new InvalidOperationException("ComfyUI completed the workflow but returned no images.");
                }

                _stats.LocalFlux2SuccessCount++;
                Logger.Log($"{promptDetails} Local Flux2 generated {images.Count} image(s).");

                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = images.Select(i => new CreatedBase64Image
                    {
                        bytesBase64 = Convert.ToBase64String(i.Bytes),
                        newPrompt = promptDetails.Prompt
                    }).ToList(),
                    ContentType = images.First().ContentType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                _stats.LocalFlux2ErrorCount++;
                Logger.Log($"{promptDetails} Local Flux2 error: {ex.Message}");
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    ImageGenerator = ApiType,
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private JObject LoadApiWorkflow(string workflowPath)
        {
            var root = JObject.Parse(File.ReadAllText(workflowPath));
            if (root["prompt"] is JObject promptObject)
            {
                return (JObject)promptObject.DeepClone();
            }
            if (LooksLikeApiPrompt(root))
            {
                return (JObject)root.DeepClone();
            }
            if (root["nodes"] is JArray)
            {
                throw new InvalidOperationException(
                    "The configured ComfyUI workflow looks like UI format. Use ComfyUI's \"Save (API Format)\" export for LocalFlux2WorkflowPath.");
            }

            throw new InvalidOperationException(
                "The configured ComfyUI workflow is not an API-format prompt JSON object.");
        }

        private static bool LooksLikeApiPrompt(JObject root)
        {
            return root.Properties().Any(p =>
                p.Value is JObject node &&
                node["class_type"] != null &&
                node["inputs"] is JObject);
        }

        private void PatchWorkflow(JObject workflow, string prompt)
        {
            ApplyPromptText(workflow, prompt);
            ApplyImageSize(workflow, _settings.LocalFlux2Width, _settings.LocalFlux2Height);
            ApplySeedAndSamplerSettings(workflow);
            ApplyFilenamePrefix(workflow);

            ApplyNamedInput(workflow, _settings.LocalFlux2ModelName, ModelInputNames, IsModelLoaderNode);
            ApplyNamedInput(workflow, _settings.LocalFlux2TextEncoderName, TextEncoderInputNames, IsTextEncoderLoaderNode);
            ApplyNamedInput(workflow, _settings.LocalFlux2VaeName, VaeInputNames, IsVaeLoaderNode);

            ApplyExplicitOverrides(workflow);
        }

        private void ApplyPromptText(JObject workflow, string prompt)
        {
            if (!string.IsNullOrWhiteSpace(_settings.LocalFlux2PositivePromptNodeId))
            {
                SetNodeInput(workflow, _settings.LocalFlux2PositivePromptNodeId, "text", prompt);
            }
            else
            {
                var positive = WorkflowNodes(workflow)
                    .FirstOrDefault(n => HasInput(n.Node, "text") && IsTextEncodeNode(n.Node) && !LooksNegative(n.Node));
                if (positive.Node != null)
                {
                    SetInput(positive.Node, "text", prompt);
                }
                else
                {
                    throw new InvalidOperationException(
                        "Could not find a positive text prompt node in the ComfyUI workflow. Set LocalFlux2PositivePromptNodeId in settings.json.");
                }
            }

            if (!string.IsNullOrWhiteSpace(_settings.LocalFlux2NegativePrompt))
            {
                if (!string.IsNullOrWhiteSpace(_settings.LocalFlux2NegativePromptNodeId))
                {
                    SetNodeInput(workflow, _settings.LocalFlux2NegativePromptNodeId, "text", _settings.LocalFlux2NegativePrompt);
                }
                else
                {
                    var negative = WorkflowNodes(workflow)
                        .FirstOrDefault(n => HasInput(n.Node, "text") && IsTextEncodeNode(n.Node) && LooksNegative(n.Node));
                    if (negative.Node != null)
                    {
                        SetInput(negative.Node, "text", _settings.LocalFlux2NegativePrompt);
                    }
                }
            }
        }

        private static void ApplyImageSize(JObject workflow, int width, int height)
        {
            foreach (var (_, node) in WorkflowNodes(workflow))
            {
                if (HasInput(node, "width")) SetInput(node, "width", width);
                if (HasInput(node, "height")) SetInput(node, "height", height);
            }
        }

        private void ApplySeedAndSamplerSettings(JObject workflow)
        {
            var seed = _settings.LocalFlux2Seed ?? RandomNumberGenerator.GetInt32(1, int.MaxValue);
            foreach (var (_, node) in WorkflowNodes(workflow))
            {
                if (HasInput(node, "seed")) SetInput(node, "seed", seed);
                if (HasInput(node, "noise_seed")) SetInput(node, "noise_seed", seed);
                if (HasInput(node, "steps")) SetInput(node, "steps", _settings.LocalFlux2Steps);

                if (_settings.LocalFlux2Guidance > 0)
                {
                    if (HasInput(node, "guidance")) SetInput(node, "guidance", _settings.LocalFlux2Guidance);
                    if (HasInput(node, "cfg")) SetInput(node, "cfg", _settings.LocalFlux2Guidance);
                }
            }
        }

        private void ApplyFilenamePrefix(JObject workflow)
        {
            var prefix = $"MultiImageClient/local_flux2_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            foreach (var (_, node) in WorkflowNodes(workflow))
            {
                if (HasInput(node, "filename_prefix")) SetInput(node, "filename_prefix", prefix);
            }
        }

        private static void ApplyNamedInput(
            JObject workflow,
            string value,
            IEnumerable<string> inputNames,
            Func<JObject, bool> nodePredicate)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            var names = inputNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, node) in WorkflowNodes(workflow))
            {
                if (!nodePredicate(node)) continue;
                var inputs = node["inputs"] as JObject;
                if (inputs == null) continue;

                foreach (var prop in inputs.Properties().ToList())
                {
                    if (names.Contains(prop.Name))
                    {
                        inputs[prop.Name] = value;
                    }
                }
            }
        }

        private void ApplyExplicitOverrides(JObject workflow)
        {
            var overrides = _settings.LocalFlux2WorkflowInputOverrides;
            if (overrides == null) return;

            foreach (var nodeOverride in overrides)
            {
                if (string.IsNullOrWhiteSpace(nodeOverride.Key)) continue;
                if (workflow[nodeOverride.Key] is not JObject node || node["inputs"] is not JObject inputs)
                {
                    throw new InvalidOperationException(
                        $"LocalFlux2WorkflowInputOverrides references missing node id '{nodeOverride.Key}'.");
                }

                foreach (var inputOverride in nodeOverride.Value ?? new Dictionary<string, JToken>())
                {
                    inputs[inputOverride.Key] = inputOverride.Value?.DeepClone() ?? JValue.CreateNull();
                }
            }
        }

        private async Task<string> QueuePromptAsync(JObject workflow)
        {
            var body = new JObject
            {
                ["prompt"] = workflow,
                ["client_id"] = Guid.NewGuid().ToString("N")
            };
            using var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{_endpoint}/prompt", content);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ComfyUI /prompt failed: {(int)response.StatusCode} {response.ReasonPhrase}: {responseText}");
            }

            var parsed = JObject.Parse(responseText);
            var promptId = parsed["prompt_id"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(promptId))
            {
                throw new InvalidOperationException($"ComfyUI /prompt did not return prompt_id: {responseText}");
            }
            return promptId;
        }

        private async Task<JObject> WaitForHistoryAsync(string promptId)
        {
            var timeout = _settings.LocalFlux2TimeoutSeconds <= 0 ? 900 : _settings.LocalFlux2TimeoutSeconds;
            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            var pollDelay = TimeSpan.FromSeconds(1);

            while (DateTime.UtcNow < deadline)
            {
                using var response = await _httpClient.GetAsync($"{_endpoint}/history/{Uri.EscapeDataString(promptId)}");
                var responseText = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"ComfyUI /history failed: {(int)response.StatusCode} {response.ReasonPhrase}: {responseText}");
                }

                var historyRoot = string.IsNullOrWhiteSpace(responseText)
                    ? new JObject()
                    : JObject.Parse(responseText);
                if (historyRoot[promptId] is JObject item)
                {
                    var completed = item["status"]?["completed"]?.Value<bool>() == true;
                    if (completed)
                    {
                        var status = item["status"]?["status_str"]?.Value<string>();
                        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("success", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"ComfyUI workflow failed with status '{status}': {ExtractHistoryError(item)}");
                        }
                        return item;
                    }
                }

                await Task.Delay(pollDelay);
            }

            throw new TimeoutException($"Timed out waiting {timeout}s for ComfyUI prompt {promptId}.");
        }

        private async Task<List<DownloadedImage>> DownloadOutputImagesAsync(JObject historyItem)
        {
            var results = new List<DownloadedImage>();
            if (historyItem["outputs"] is not JObject outputs)
            {
                return results;
            }

            foreach (var output in outputs.Properties())
            {
                if (output.Value is not JObject outputObject) continue;
                if (outputObject["images"] is not JArray images) continue;

                foreach (var imageToken in images.OfType<JObject>())
                {
                    var filename = imageToken["filename"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(filename)) continue;

                    var type = imageToken["type"]?.Value<string>() ?? "output";
                    var subfolder = imageToken["subfolder"]?.Value<string>() ?? "";
                    var url = BuildViewUrl(filename, type, subfolder);

                    using var response = await _httpClient.GetAsync(url);
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = Encoding.UTF8.GetString(bytes);
                        throw new HttpRequestException($"ComfyUI /view failed: {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
                    }
                    if (bytes.Length == 0)
                    {
                        throw new InvalidOperationException($"ComfyUI /view returned empty bytes for {filename}.");
                    }

                    results.Add(new DownloadedImage
                    {
                        Bytes = bytes,
                        ContentType = response.Content.Headers.ContentType?.MediaType ?? "image/png"
                    });
                }
            }

            return results;
        }

        private string BuildViewUrl(string filename, string type, string subfolder)
        {
            var parts = new List<string>
            {
                $"filename={Uri.EscapeDataString(filename)}",
                $"type={Uri.EscapeDataString(type)}"
            };
            if (!string.IsNullOrEmpty(subfolder))
            {
                parts.Add($"subfolder={Uri.EscapeDataString(subfolder)}");
            }

            return $"{_endpoint}/view?{string.Join("&", parts)}";
        }

        private static IEnumerable<(string Id, JObject Node)> WorkflowNodes(JObject workflow)
        {
            foreach (var property in workflow.Properties())
            {
                if (property.Value is JObject node && node["inputs"] is JObject)
                {
                    yield return (property.Name, node);
                }
            }
        }

        private static bool IsTextEncodeNode(JObject node)
        {
            var classType = NodeClass(node);
            return classType.Contains("TextEncode", StringComparison.OrdinalIgnoreCase)
                || classType.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase)
                || classType.Contains("Conditioning", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModelLoaderNode(JObject node)
        {
            var classType = NodeClass(node);
            return classType.Contains("Loader", StringComparison.OrdinalIgnoreCase)
                && (classType.Contains("UNET", StringComparison.OrdinalIgnoreCase)
                    || classType.Contains("Unet", StringComparison.OrdinalIgnoreCase)
                    || classType.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase)
                    || classType.Contains("Diffusion", StringComparison.OrdinalIgnoreCase)
                    || classType.Contains("GGUF", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTextEncoderLoaderNode(JObject node)
        {
            var classType = NodeClass(node);
            return classType.Contains("Loader", StringComparison.OrdinalIgnoreCase)
                && (classType.Contains("CLIP", StringComparison.OrdinalIgnoreCase)
                    || classType.Contains("TextEncoder", StringComparison.OrdinalIgnoreCase)
                    || classType.Contains("DualCLIP", StringComparison.OrdinalIgnoreCase)
                    || classType.Contains("TripleCLIP", StringComparison.OrdinalIgnoreCase)
                    || classType.Contains("GGUF", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsVaeLoaderNode(JObject node)
        {
            return NodeClass(node).Contains("VAE", StringComparison.OrdinalIgnoreCase)
                && NodeClass(node).Contains("Loader", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksNegative(JObject node)
        {
            var title = node["_meta"]?["title"]?.Value<string>() ?? "";
            var text = node["inputs"]?["text"]?.Value<string>() ?? "";
            return title.Contains("negative", StringComparison.OrdinalIgnoreCase)
                || text.Contains("negative", StringComparison.OrdinalIgnoreCase);
        }

        private static string NodeClass(JObject node)
        {
            return node["class_type"]?.Value<string>() ?? "";
        }

        private static bool HasInput(JObject node, string inputName)
        {
            return node["inputs"] is JObject inputs && inputs.Property(inputName) != null;
        }

        private static void SetNodeInput(JObject workflow, string nodeId, string inputName, JToken value)
        {
            if (workflow[nodeId] is not JObject node || node["inputs"] is not JObject)
            {
                throw new InvalidOperationException($"ComfyUI workflow does not contain node id '{nodeId}'.");
            }
            SetInput(node, inputName, value);
        }

        private static void SetInput(JObject node, string inputName, JToken value)
        {
            if (node["inputs"] is not JObject inputs)
            {
                throw new InvalidOperationException("ComfyUI workflow node is missing an inputs object.");
            }
            inputs[inputName] = value;
        }

        private static string NormalizeEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = "http://127.0.0.1:8188";
            }
            return endpoint.Trim().TrimEnd('/');
        }

        private static void EnsureEndpointAllowed(string endpoint, bool allowRemote)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"LocalFlux2ComfyEndpoint is not a valid absolute URL: {endpoint}");
            }
            if (allowRemote) return;

            var host = uri.Host;
            var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.Equals("127.0.0.1")
                || host.Equals("::1")
                || host.Equals("[::1]");
            if (!local)
            {
                throw new InvalidOperationException(
                    $"Refusing to send local Flux2 prompts to non-local ComfyUI endpoint '{endpoint}'. Set LocalFlux2AllowRemoteEndpoint=true only for a trusted LAN host.");
            }
        }

        private static string EndpointHostLabel(string endpoint)
        {
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                ? $"{uri.Host}:{uri.Port}"
                : endpoint;
        }

        private static string ExtractHistoryError(JObject historyItem)
        {
            var messages = historyItem["status"]?["messages"] as JArray;
            if (messages == null) return "(no ComfyUI error details)";

            foreach (var message in messages.OfType<JArray>())
            {
                if (message.Count < 2) continue;
                var kind = message[0]?.Value<string>() ?? "";
                if (!kind.Contains("error", StringComparison.OrdinalIgnoreCase)) continue;
                return message[1]?.ToString(Formatting.None) ?? "(empty error)";
            }
            return "(no ComfyUI error details)";
        }

        private class DownloadedImage
        {
            public byte[] Bytes { get; set; }
            public string ContentType { get; set; }
        }
    }
}
