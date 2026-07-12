#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// IImageGenerator adapter for Meta AI's consumer web app (Muse Image),
    /// driven by browser cookies through MetaWebClient. The web-app sibling of
    /// the api.x.ai vs grok.com split: there is no official Muse Image API yet,
    /// so this is the only way to reach the model programmatically, and it is
    /// best-effort (see MetaWebClient for the doc_id/DGW caveats).
    public class MetaWebImagineGenerator : IImageGenerator
    {
        private readonly MetaWebClient _client;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly string _orientation;
        private readonly int _numImages;
        private readonly TimeSpan _timeout;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.MetaWebImagine;

        public MetaWebImagineGenerator(
            MetaWebClient client,
            int maxConcurrency,
            MultiClientRunStats stats,
            string orientation = "SQUARE",
            int numImages = 1,
            int timeoutMinutes = 8)
        {
            _client = client;
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _orientation = string.IsNullOrWhiteSpace(orientation) ? "SQUARE" : orientation.Trim().ToUpperInvariant();
            _numImages = Math.Max(1, numImages);
            _timeout = TimeSpan.FromMinutes(timeoutMinutes);
        }

        public string GetFilenamePart(PromptDetails pd)
            => $"metaweb_{_orientation.ToLowerInvariant()}";

        public List<string> GetRightParts()
        {
            return new List<string>
            {
                "Meta Web Imagine",
                "Muse Image",
                "meta.ai (cookie session)",
                $"orientation {_orientation}",
            };
        }

        public string GetGeneratorSpecPart()
            => $"Meta Web Imagine (Muse Image)  {_orientation}";

        public decimal GetCost() => 0m;

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.MetaWebImageGenerationRequestCount++;
                var prompt = promptDetails.Prompt ?? string.Empty;
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    Logger.Log($"\t-> Meta Web Imagine orientation={_orientation}: WARNING empty prompt text");
                }
                else
                {
                    var head = prompt.Length <= 120 ? prompt : prompt[..117] + "...";
                    Logger.Log($"\t-> Meta Web Imagine orientation={_orientation} [{prompt.Length} chars]: {head}");
                }

                var result = await _client.GenerateImageAsync(prompt, _orientation, _numImages, _timeout);
                sw.Stop();

                if (result.Images.Count == 0)
                {
                    _stats.MetaWebImageGenerationErrorCount++;
                    return Fail("Meta web returned no images.", promptDetails, generator, sw.ElapsedMilliseconds);
                }

                _stats.MetaWebImageGenerationSuccessCount++;
                Logger.Log($"\t<- Meta Web Imagine OK in {sw.ElapsedMilliseconds} ms; {result.Images.Count} image(s)");

                var contentType = GuessContentTypeFromBytes(result.Images[0]);
                var images = result.Images
                    .Select(bytes => new CreatedBase64Image
                    {
                        bytesBase64 = Convert.ToBase64String(bytes),
                        newPrompt = prompt,
                    })
                    .ToList();

                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = images,
                    ContentType = contentType,
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _stats.MetaWebImageGenerationErrorCount++;
                Logger.Log($"\t<- Meta Web Imagine FAIL: {ex.Message}");
                return Fail(ex.Message, promptDetails, generator, sw.ElapsedMilliseconds);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static string GuessContentTypeFromBytes(byte[] bytes)
        {
            if (bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }

            if (bytes.Length >= 12
                && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            {
                return "image/webp";
            }

            return "image/jpeg";
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
    }
}
