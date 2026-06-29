using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

#nullable enable

namespace MultiImageClient
{
    public sealed class PromptGenerationRunner
    {
        private readonly Settings _settings;

        public PromptGenerationRunner(Settings settings)
        {
            _settings = settings;
        }

        public async Task<ProviderGenerationResult> GenerateAsync(ProviderPreset preset, string prompt)
        {
            var generator = preset.CreateGenerator();
            var keyProblem = ProviderKeyValidator.DescribeKeyProblem(generator.ApiType, _settings);
            if (keyProblem != null)
            {
                return ProviderGenerationResult.Failed(preset, generator, prompt, keyProblem);
            }

            var promptDetails = new PromptDetails();
            promptDetails.ReplacePrompt(prompt, prompt, TransformationType.InitialPrompt);

            try
            {
                var result = await generator.ProcessPromptAsync(generator, promptDetails);
                if (!result.IsSuccess)
                {
                    return ProviderGenerationResult.Failed(preset, generator, prompt, result.ErrorMessage ?? "Generation failed.", result);
                }

                var images = await ExtractImagesAsync(result);
                if (images.Count == 0)
                {
                    return ProviderGenerationResult.Failed(preset, generator, prompt, "Generation succeeded but returned no image bytes.", result);
                }

                return ProviderGenerationResult.Succeeded(preset, generator, prompt, result, images);
            }
            catch (Exception ex)
            {
                return ProviderGenerationResult.Failed(preset, generator, prompt, ex.Message);
            }
        }

        private static async Task<IReadOnlyList<GeneratedImageArtifact>> ExtractImagesAsync(TaskProcessResult result)
        {
            if (!string.IsNullOrWhiteSpace(result.Url))
            {
                var sw = Stopwatch.StartNew();
                var bytes = await ImageSaving.DownloadImageAsync(result);
                result.DownloadTotalMs = sw.ElapsedMilliseconds;
                return bytes.Length == 0
                    ? Array.Empty<GeneratedImageArtifact>()
                    : new[] { new GeneratedImageArtifact(0, bytes, result.ContentType ?? "image/png", "") };
            }

            return result.Base64ImageDatas
                .Select((image, index) => new GeneratedImageArtifact(
                    index,
                    Convert.FromBase64String(image.bytesBase64),
                    result.ContentType ?? "image/png",
                    image.newPrompt ?? ""))
                .ToList();
        }
    }

    public sealed class ProviderGenerationResult
    {
        private ProviderGenerationResult(
            ProviderPreset preset,
            IImageGenerator generator,
            string prompt,
            TaskProcessResult? taskResult,
            IReadOnlyList<GeneratedImageArtifact> images,
            string error)
        {
            Preset = preset;
            Generator = generator;
            Prompt = prompt;
            TaskResult = taskResult;
            Images = images;
            Error = error;
        }

        public ProviderPreset Preset { get; }
        public IImageGenerator Generator { get; }
        public string Prompt { get; }
        public TaskProcessResult? TaskResult { get; }
        public IReadOnlyList<GeneratedImageArtifact> Images { get; }
        public string Error { get; }
        public bool IsSuccess => string.IsNullOrWhiteSpace(Error);

        public static ProviderGenerationResult Succeeded(
            ProviderPreset preset,
            IImageGenerator generator,
            string prompt,
            TaskProcessResult taskResult,
            IReadOnlyList<GeneratedImageArtifact> images)
        {
            return new ProviderGenerationResult(preset, generator, prompt, taskResult, images, "");
        }

        public static ProviderGenerationResult Failed(
            ProviderPreset preset,
            IImageGenerator generator,
            string prompt,
            string error,
            TaskProcessResult? taskResult = null)
        {
            return new ProviderGenerationResult(preset, generator, prompt, taskResult, Array.Empty<GeneratedImageArtifact>(), error);
        }
    }

    public sealed record GeneratedImageArtifact(int Index, byte[] Bytes, string ContentType, string RevisedPrompt);
}
