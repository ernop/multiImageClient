#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public enum DirectImageProvider
    {
        ByteDanceSeedream,
        MiniMaxHailuo,
        Krea,
        Bria,
        Magnific,
        LumaPhoton,
        Runway,
        StabilityAi,
    }

    public class DirectImageApiGenerator : IImageGenerator
    {
        private readonly DirectImageProvider _provider;
        private readonly Settings _settings;
        private readonly MultiClientRunStats _stats;
        private readonly SemaphoreSlim _semaphore;
        private readonly HttpClient _httpClient;
        private readonly string _name;

        public ImageGeneratorApiType ApiType => ApiTypeFor(_provider);

        public DirectImageApiGenerator(
            DirectImageProvider provider,
            Settings settings,
            int maxConcurrency,
            MultiClientRunStats stats,
            string name = "")
        {
            _provider = provider;
            _settings = settings;
            _stats = stats;
            _name = name ?? string.Empty;
            _semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency));
            var timeout = settings.DirectImageApiTimeoutSeconds <= 0 ? 600 : settings.DirectImageApiTimeoutSeconds;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(timeout + 30)
            };
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var model = ModelLabel().Replace('/', '_').Replace(':', 'x');
            var size = SizeLabel().Replace(':', 'x').Replace('/', '_');
            var suffix = string.IsNullOrWhiteSpace(_name) ? "" : $"_{_name}";
            return $"{ApiType}_{model}_{size}{suffix}";
        }

        public List<string> GetRightParts()
        {
            var parts = new List<string>
            {
                ProviderLabel(),
                ModelLabel(),
                SizeLabel(),
            };
            if (!string.IsNullOrWhiteSpace(_name))
            {
                parts.Add(_name);
            }
            return parts;
        }

        public string GetGeneratorSpecPart()
        {
            if (!string.IsNullOrWhiteSpace(_name))
            {
                return _name;
            }
            return $"{ProviderLabel()} {ModelLabel()} {SizeLabel()}";
        }

        public decimal GetCost()
        {
            return _provider switch
            {
                DirectImageProvider.ByteDanceSeedream => 0.04m,
                DirectImageProvider.MiniMaxHailuo => 0.03m,
                DirectImageProvider.Krea => 0.04m,
                DirectImageProvider.Bria => 0.05m,
                DirectImageProvider.Magnific => 0.08m,
                DirectImageProvider.LumaPhoton => _settings.LumaPhotonModel == "photon-flash-1" ? 0.004m : 0.015m,
                DirectImageProvider.Runway => _settings.RunwayImageModel == "gen4_image_turbo" ? 0.02m : 0.08m,
                DirectImageProvider.StabilityAi => _settings.StabilityModel.Contains("turbo", StringComparison.OrdinalIgnoreCase) ? 0.04m : 0.08m,
                _ => 0m,
            };
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            _stats.DirectImageGenerationRequestCount++;

            try
            {
                var submit = await SubmitAsync(promptDetails.Prompt ?? string.Empty);
                var completed = submit;
                if (submit.PollUrl != null)
                {
                    completed = await PollAsync(submit);
                }

                var urls = ExtractImageUrls(completed.Json);
                var base64Images = ExtractBase64Images(completed.Json);

                if (completed.BinaryImageBytes != null && completed.BinaryImageBytes.Length > 0)
                {
                    base64Images.Add(Convert.ToBase64String(completed.BinaryImageBytes));
                }

                if (urls.Count == 0 && base64Images.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"{ProviderLabel()} completed but no image URL or base64 image was found in response: {Truncate(completed.RawBody, 700)}");
                }

                _stats.DirectImageGenerationSuccessCount++;
                sw.Stop();

                if (urls.Count > 0)
                {
                    var firstUrl = urls[0];
                    var contentType = await TryGetContentTypeAsync(firstUrl);
                    Logger.Log($"\t<- {ProviderLabel()} OK url in {sw.ElapsedMilliseconds} ms");
                    return new TaskProcessResult
                    {
                        IsSuccess = true,
                        Url = firstUrl,
                        ContentType = contentType,
                        PromptDetails = promptDetails,
                        ImageGenerator = ApiType,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                        CreateTotalMs = sw.ElapsedMilliseconds,
                    };
                }

                Logger.Log($"\t<- {ProviderLabel()} OK {base64Images.Count} inline image(s) in {sw.ElapsedMilliseconds} ms");
                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = base64Images.Select(b => new CreatedBase64Image
                    {
                        bytesBase64 = b,
                        newPrompt = promptDetails.Prompt,
                    }).ToList(),
                    ContentType = completed.ContentType ?? ContentTypeFromOutputFormat(),
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _stats.DirectImageGenerationErrorCount++;
                Logger.Log($"\t<- {ProviderLabel()} FAIL: {ex.Message}");
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<ProviderResult> SubmitAsync(string prompt)
        {
            return _provider switch
            {
                DirectImageProvider.ByteDanceSeedream => await SubmitSeedreamAsync(prompt),
                DirectImageProvider.MiniMaxHailuo => await SubmitHailuoAsync(prompt),
                DirectImageProvider.Krea => await SubmitKreaAsync(prompt),
                DirectImageProvider.Bria => await SubmitBriaAsync(prompt),
                DirectImageProvider.Magnific => await SubmitMagnificAsync(prompt),
                DirectImageProvider.LumaPhoton => await SubmitLumaAsync(prompt),
                DirectImageProvider.Runway => await SubmitRunwayAsync(prompt),
                DirectImageProvider.StabilityAi => await SubmitStabilityAsync(prompt),
                _ => throw new ArgumentOutOfRangeException(nameof(_provider), _provider, null),
            };
        }

        private async Task<ProviderResult> SubmitSeedreamAsync(string prompt)
        {
            Require(_settings.ByteDanceArkApiKey, "ByteDanceArkApiKey");
            var body = new JObject
            {
                ["model"] = _settings.SeedreamModel,
                ["prompt"] = prompt,
                ["size"] = _settings.SeedreamSize,
                ["watermark"] = _settings.SeedreamWatermark,
                ["response_format"] = "url",
            };
            var url = $"{TrimSlash(_settings.ByteDanceArkBaseUrl)}/images/generations";
            var request = JsonPost(url, body);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.ByteDanceArkApiKey);
            return await SendJsonAsync(request);
        }

        private async Task<ProviderResult> SubmitHailuoAsync(string prompt)
        {
            Require(_settings.MiniMaxApiKey, "MiniMaxApiKey");
            var body = new JObject
            {
                ["model"] = _settings.HailuoModel,
                ["prompt"] = prompt,
                ["aspect_ratio"] = _settings.HailuoAspectRatio,
                ["response_format"] = string.IsNullOrWhiteSpace(_settings.HailuoResponseFormat) ? "base64" : _settings.HailuoResponseFormat,
            };
            var request = JsonPost("https://api.minimax.io/v1/image_generation", body);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.MiniMaxApiKey);
            return await SendJsonAsync(request);
        }

        private async Task<ProviderResult> SubmitKreaAsync(string prompt)
        {
            Require(_settings.KreaApiKey, "KreaApiKey");
            var variant = string.Equals(_settings.KreaModelVariant, "large", StringComparison.OrdinalIgnoreCase)
                ? "large"
                : "medium";
            var body = new JObject
            {
                ["prompt"] = prompt,
                ["aspect_ratio"] = _settings.KreaAspectRatio,
                ["resolution"] = _settings.KreaResolution,
                ["creativity"] = _settings.KreaCreativity,
            };
            var request = JsonPost($"https://api.krea.ai/generate/image/krea/krea-2/{variant}", body);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.KreaApiKey);
            var result = await SendJsonAsync(request);
            var jobId = StringAt(result.Json, "job_id") ?? StringAt(result.Json, "id");
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new InvalidOperationException($"Krea did not return job_id: {Truncate(result.RawBody, 500)}");
            }
            result.PollUrl = $"https://api.krea.ai/jobs/{Uri.EscapeDataString(jobId)}";
            return result;
        }

        private async Task<ProviderResult> SubmitBriaAsync(string prompt)
        {
            Require(_settings.BriaApiKey, "BriaApiKey");
            var body = new JObject
            {
                ["prompt"] = prompt,
                ["aspect_ratio"] = _settings.BriaAspectRatio,
                ["resolution"] = _settings.BriaResolution,
                ["num_results"] = Math.Max(1, _settings.BriaNumResults),
            };
            var request = JsonPost($"{TrimSlash(_settings.BriaBaseUrl)}/image/generate", body);
            request.Headers.Add("api_token", _settings.BriaApiKey);
            request.Headers.UserAgent.ParseAdd("MultiImageClient/1.0");
            var result = await SendJsonAsync(request);
            result.PollUrl = StringAt(result.Json, "status_url");
            return result;
        }

        private async Task<ProviderResult> SubmitMagnificAsync(string prompt)
        {
            Require(_settings.MagnificApiKey, "MagnificApiKey");
            var body = new JObject
            {
                ["prompt"] = prompt,
                ["aspect_ratio"] = _settings.MagnificAspectRatio,
                ["model"] = _settings.MagnificModel,
                ["resolution"] = _settings.MagnificResolution,
            };
            var request = JsonPost("https://api.magnific.com/v1/ai/mystic", body);
            request.Headers.Add("x-magnific-api-key", _settings.MagnificApiKey);
            var result = await SendJsonAsync(request);
            var taskId = StringAt(result.Json, "task_id") ?? StringAt(result.Json, "taskId") ?? StringAt(result.Json, "id");
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                result.PollUrl = $"https://api.magnific.com/v1/ai/mystic/{Uri.EscapeDataString(taskId)}";
            }
            return result;
        }

        private async Task<ProviderResult> SubmitLumaAsync(string prompt)
        {
            Require(_settings.LumaApiKey, "LumaApiKey");
            var body = new JObject
            {
                ["prompt"] = prompt,
                ["model"] = _settings.LumaPhotonModel,
                ["aspect_ratio"] = _settings.LumaAspectRatio,
            };
            var request = JsonPost("https://api.lumalabs.ai/dream-machine/v1/generations/image", body);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.LumaApiKey);
            var result = await SendJsonAsync(request);
            var id = StringAt(result.Json, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException($"Luma did not return id: {Truncate(result.RawBody, 500)}");
            }
            result.PollUrl = $"https://api.lumalabs.ai/dream-machine/v1/generations/{Uri.EscapeDataString(id)}";
            return result;
        }

        private async Task<ProviderResult> SubmitRunwayAsync(string prompt)
        {
            Require(_settings.RunwayApiKey, "RunwayApiKey");
            var body = new JObject
            {
                ["model"] = _settings.RunwayImageModel,
                ["promptText"] = prompt,
                ["ratio"] = _settings.RunwayImageRatio,
            };
            var request = JsonPost("https://api.dev.runwayml.com/v1/text_to_image", body);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.RunwayApiKey);
            request.Headers.Add("X-Runway-Version", _settings.RunwayApiVersion);
            var result = await SendJsonAsync(request);
            var id = StringAt(result.Json, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException($"Runway did not return id: {Truncate(result.RawBody, 500)}");
            }
            result.PollUrl = $"https://api.dev.runwayml.com/v1/tasks/{Uri.EscapeDataString(id)}";
            return result;
        }

        private async Task<ProviderResult> SubmitStabilityAsync(string prompt)
        {
            Require(_settings.StabilityApiKey, "StabilityApiKey");
            var form = new MultipartFormDataContent
            {
                { new StringContent(prompt), "prompt" },
                { new StringContent("text-to-image"), "mode" },
                { new StringContent(_settings.StabilityModel), "model" },
                { new StringContent(_settings.StabilityAspectRatio), "aspect_ratio" },
                { new StringContent(_settings.StabilityOutputFormat), "output_format" },
            };
            if (!string.IsNullOrWhiteSpace(_settings.StabilityNegativePrompt))
            {
                form.Add(new StringContent(_settings.StabilityNegativePrompt), "negative_prompt");
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stability.ai/v2beta/stable-image/generate/sd3")
            {
                Content = form
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.StabilityApiKey);
            request.Headers.Accept.ParseAdd("image/*");

            using var response = await _httpClient.SendAsync(request);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Stability AI failed {(int)response.StatusCode} {response.ReasonPhrase}: {BytesToErrorString(bytes)}");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? ContentTypeFromOutputFormat();
            return new ProviderResult
            {
                Json = new JObject(),
                RawBody = $"<binary {bytes.Length} bytes>",
                BinaryImageBytes = bytes,
                ContentType = contentType,
            };
        }

        private async Task<ProviderResult> PollAsync(ProviderResult submit)
        {
            var deadline = DateTime.UtcNow.AddSeconds(_settings.DirectImageApiTimeoutSeconds <= 0 ? 600 : _settings.DirectImageApiTimeoutSeconds);
            var delay = TimeSpan.FromSeconds(2);
            var pollUrl = submit.PollUrl!;

            while (DateTime.UtcNow < deadline)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                AddPollingHeaders(request);
                var current = await SendJsonAsync(request);
                var state = PollState(current.Json);
                if (state == ProviderPollState.Completed)
                {
                    return current;
                }
                if (state == ProviderPollState.Failed)
                {
                    throw new InvalidOperationException($"{ProviderLabel()} generation failed: {Truncate(current.RawBody, 700)}");
                }

                await Task.Delay(delay);
            }

            throw new TimeoutException($"{ProviderLabel()} did not complete within {_settings.DirectImageApiTimeoutSeconds} seconds.");
        }

        private void AddPollingHeaders(HttpRequestMessage request)
        {
            switch (_provider)
            {
                case DirectImageProvider.Krea:
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.KreaApiKey);
                    break;
                case DirectImageProvider.Bria:
                    request.Headers.Add("api_token", _settings.BriaApiKey);
                    request.Headers.UserAgent.ParseAdd("MultiImageClient/1.0");
                    break;
                case DirectImageProvider.Magnific:
                    request.Headers.Add("x-magnific-api-key", _settings.MagnificApiKey);
                    break;
                case DirectImageProvider.LumaPhoton:
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.LumaApiKey);
                    break;
                case DirectImageProvider.Runway:
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.RunwayApiKey);
                    request.Headers.Add("X-Runway-Version", _settings.RunwayApiVersion);
                    break;
            }
        }

        private ProviderPollState PollState(JObject json)
        {
            var status = StringAt(json, "status") ?? StringAt(json, "state");
            if (string.IsNullOrWhiteSpace(status))
            {
                return HasImage(json) ? ProviderPollState.Completed : ProviderPollState.Pending;
            }

            status = status.Trim();
            if (status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("complete", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("done", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("SUCCEEDED", StringComparison.OrdinalIgnoreCase))
            {
                return ProviderPollState.Completed;
            }

            if (status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("canceled", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                return ProviderPollState.Failed;
            }

            return ProviderPollState.Pending;
        }

        private async Task<ProviderResult> SendJsonAsync(HttpRequestMessage request)
        {
            using var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{ProviderLabel()} failed {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(text, 1000)}");
            }
            var json = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
            return new ProviderResult
            {
                Json = json,
                RawBody = text,
                ContentType = response.Content.Headers.ContentType?.MediaType,
            };
        }

        private static HttpRequestMessage JsonPost(string url, JObject body)
        {
            return new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(body, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }),
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private List<string> ExtractImageUrls(JObject json)
        {
            var urls = new List<string>();

            if (_provider == DirectImageProvider.Krea && json["result"]?["urls"] is JArray kreaUrls)
            {
                urls.AddRange(kreaUrls.Values<string>().Where(IsHttpUrl));
            }
            if (_provider == DirectImageProvider.LumaPhoton)
            {
                var luma = json["assets"]?["image"]?.Value<string>();
                if (IsHttpUrl(luma)) urls.Add(luma!);
            }
            if (_provider == DirectImageProvider.Runway && json["output"] is JArray output)
            {
                urls.AddRange(output.Values<string>().Where(IsHttpUrl));
            }

            foreach (var key in new[] { "url", "image_url", "imageUrl", "imageURL", "image" })
            {
                urls.AddRange(FindStringsByKey(json, key).Where(IsHttpUrl));
            }

            return urls.Distinct().ToList();
        }

        private static List<string> ExtractBase64Images(JObject json)
        {
            var images = new List<string>();
            foreach (var key in new[] { "b64_json", "image_base64", "base64", "imageBase64", "image_base64_data" })
            {
                images.AddRange(FindStringsByKey(json, key).Where(LooksLikeBase64Image).Select(StripDataUriPrefix));
            }

            if (json["data"]?["image_base64"] is JArray minimaxArray)
            {
                images.AddRange(minimaxArray.Values<string>().Where(LooksLikeBase64Image).Select(StripDataUriPrefix));
            }
            if (json["data"]?["image_base64"] is JValue minimaxSingle)
            {
                var s = minimaxSingle.Value<string>();
                if (LooksLikeBase64Image(s)) images.Add(StripDataUriPrefix(s!));
            }

            return images.Distinct().ToList();
        }

        private static IEnumerable<string> FindStringsByKey(JToken token, string key)
        {
            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.Type == JTokenType.String)
                        {
                            var value = prop.Value.Value<string>();
                            if (!string.IsNullOrWhiteSpace(value)) yield return value!;
                        }
                        else if (prop.Value is JArray arr)
                        {
                            foreach (var value in arr.Values<string>())
                            {
                                if (!string.IsNullOrWhiteSpace(value)) yield return value!;
                            }
                        }
                    }

                    foreach (var nested in FindStringsByKey(prop.Value, key))
                    {
                        yield return nested;
                    }
                }
            }
            else if (token is JArray arr)
            {
                foreach (var child in arr)
                {
                    foreach (var nested in FindStringsByKey(child, key))
                    {
                        yield return nested;
                    }
                }
            }
        }

        private bool HasImage(JObject json)
        {
            return ExtractImageUrls(json).Count > 0 || ExtractBase64Images(json).Count > 0;
        }

        private async Task<string?> TryGetContentTypeAsync(string url)
        {
            try
            {
                using var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                return response.Content.Headers.ContentType?.MediaType;
            }
            catch
            {
                return null;
            }
        }

        private string ProviderLabel()
        {
            return _provider switch
            {
                DirectImageProvider.ByteDanceSeedream => "ByteDance Seedream",
                DirectImageProvider.MiniMaxHailuo => "MiniMax Hailuo",
                DirectImageProvider.Krea => "Krea",
                DirectImageProvider.Bria => "BRIA",
                DirectImageProvider.Magnific => "Magnific Mystic",
                DirectImageProvider.LumaPhoton => "Luma Photon",
                DirectImageProvider.Runway => "Runway Gen-4 Image",
                DirectImageProvider.StabilityAi => "Stability AI",
                _ => _provider.ToString(),
            };
        }

        private string ModelLabel()
        {
            return _provider switch
            {
                DirectImageProvider.ByteDanceSeedream => _settings.SeedreamModel,
                DirectImageProvider.MiniMaxHailuo => _settings.HailuoModel,
                DirectImageProvider.Krea => $"krea-2-{_settings.KreaModelVariant}",
                DirectImageProvider.Bria => "bria-fibo",
                DirectImageProvider.Magnific => $"mystic-{_settings.MagnificModel}",
                DirectImageProvider.LumaPhoton => _settings.LumaPhotonModel,
                DirectImageProvider.Runway => _settings.RunwayImageModel,
                DirectImageProvider.StabilityAi => _settings.StabilityModel,
                _ => _provider.ToString(),
            };
        }

        private string SizeLabel()
        {
            return _provider switch
            {
                DirectImageProvider.ByteDanceSeedream => _settings.SeedreamSize,
                DirectImageProvider.MiniMaxHailuo => _settings.HailuoAspectRatio,
                DirectImageProvider.Krea => $"{_settings.KreaAspectRatio} {_settings.KreaResolution}",
                DirectImageProvider.Bria => $"{_settings.BriaAspectRatio} {_settings.BriaResolution}",
                DirectImageProvider.Magnific => $"{_settings.MagnificAspectRatio} {_settings.MagnificResolution}",
                DirectImageProvider.LumaPhoton => _settings.LumaAspectRatio,
                DirectImageProvider.Runway => _settings.RunwayImageRatio,
                DirectImageProvider.StabilityAi => _settings.StabilityAspectRatio,
                _ => "",
            };
        }

        private string ContentTypeFromOutputFormat()
        {
            var format = _provider == DirectImageProvider.StabilityAi ? _settings.StabilityOutputFormat : "png";
            return format?.ToLowerInvariant() switch
            {
                "jpg" => "image/jpeg",
                "jpeg" => "image/jpeg",
                "webp" => "image/webp",
                _ => "image/png",
            };
        }

        private static ImageGeneratorApiType ApiTypeFor(DirectImageProvider provider)
        {
            return provider switch
            {
                DirectImageProvider.ByteDanceSeedream => ImageGeneratorApiType.ByteDanceSeedream,
                DirectImageProvider.MiniMaxHailuo => ImageGeneratorApiType.MiniMaxHailuoImage,
                DirectImageProvider.Krea => ImageGeneratorApiType.KreaImage,
                DirectImageProvider.Bria => ImageGeneratorApiType.BriaImage,
                DirectImageProvider.Magnific => ImageGeneratorApiType.MagnificMystic,
                DirectImageProvider.LumaPhoton => ImageGeneratorApiType.LumaPhoton,
                DirectImageProvider.Runway => ImageGeneratorApiType.RunwayGen4Image,
                DirectImageProvider.StabilityAi => ImageGeneratorApiType.StabilityAi,
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
            };
        }

        private static string? StringAt(JObject json, string key)
        {
            return FindStringsByKey(json, key).FirstOrDefault();
        }

        private static bool IsHttpUrl(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeBase64Image(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return true;
            return value.Length > 100 && !value.Contains("://") && value.All(c =>
                char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=' || c == '\r' || c == '\n');
        }

        private static string StripDataUriPrefix(string value)
        {
            var comma = value.IndexOf(',');
            if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            {
                return value.Substring(comma + 1);
            }
            return value;
        }

        private static string TrimSlash(string value)
        {
            return (value ?? string.Empty).Trim().TrimEnd('/');
        }

        private static void Require(string value, string settingName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Settings.{settingName} is not set.");
            }
        }

        private static string Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static string BytesToErrorString(byte[] bytes)
        {
            if (bytes.Length == 0) return "";
            try
            {
                return Truncate(Encoding.UTF8.GetString(bytes), 1000);
            }
            catch
            {
                return $"<binary {bytes.Length} bytes>";
            }
        }

        private enum ProviderPollState
        {
            Pending,
            Completed,
            Failed,
        }

        private class ProviderResult
        {
            public JObject Json { get; set; } = new JObject();
            public string RawBody { get; set; } = "";
            public string? PollUrl { get; set; }
            public byte[]? BinaryImageBytes { get; set; }
            public string? ContentType { get; set; }
        }
    }
}
