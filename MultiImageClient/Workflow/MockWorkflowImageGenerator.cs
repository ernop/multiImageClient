using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
            var c1 = new Rgba32(hash[0], hash[1], hash[2]);
            var c2 = new Rgba32(hash[3], hash[4], hash[5]);
            var c3 = new Rgba32(hash[6], hash[7], hash[8]);

            using var image = new Image<Rgba32>(768, 768, c1);
            image.Mutate(ctx =>
            {
                ctx.Fill(c2, new Rectangle(96, 96, 576, 576));
                ctx.Fill(c3, new Rectangle(192, 192, 384, 384));
                ctx.Fill(new Rgba32(255, 255, 255), new Rectangle(282, 282, 204, 204));
            });

            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }
    }
}
