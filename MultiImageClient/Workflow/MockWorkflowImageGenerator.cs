using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;

using SkiaSharp;

namespace MultiImageClient
{
    public sealed class MockWorkflowImageGenerator : IImageGenerator
    {
        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.WorkflowMock;

        public string GetFilenamePart(PromptDetails pd) => "workflow-mock";

        public List<string> GetRightParts() => new()
        {
            "Workflow Mock",
            "local",
            "free",
        };

        public string GetGeneratorSpecPart() => "Workflow Mock local PNG";

        public decimal GetCost() => 0m;

        public Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            var bytes = CreatePng(promptDetails.Prompt ?? "");
            return Task.FromResult(new TaskProcessResult
            {
                IsSuccess = true,
                Base64ImageDatas = new[]
                {
                    new CreatedBase64Image
                    {
                        bytesBase64 = Convert.ToBase64String(bytes),
                        newPrompt = promptDetails.Prompt,
                    },
                },
                ContentType = "image/png",
                PromptDetails = promptDetails,
                ImageGenerator = ApiType,
                ImageGeneratorDescription = GetGeneratorSpecPart(),
            });
        }

        private static byte[] CreatePng(string prompt)
        {
            var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(prompt));
            var c1 = new SKColor(hash[0], hash[1], hash[2]);
            var c2 = new SKColor(hash[3], hash[4], hash[5]);
            var c3 = new SKColor(hash[6], hash[7], hash[8]);

            using var image = Raster.Create(768, 768, c1);
            using (var canvas = new SKCanvas(image))
            {
                canvas.FillRect(SKRect.Create(96, 96, 576, 576), c2);
                canvas.FillRect(SKRect.Create(192, 192, 384, 384), c3);
                canvas.FillRect(SKRect.Create(282, 282, 204, 204), SKColors.White);
            }

            return Raster.EncodePng(image);
        }
    }
}
