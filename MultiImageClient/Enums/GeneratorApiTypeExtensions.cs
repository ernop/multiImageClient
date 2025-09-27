using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public static class GeneratorApiTypeExtensions
    {
        public static string GetFileExtension(this ImageGeneratorApiType generator)
        {
            return generator switch
            {
                ImageGeneratorApiType.Ideogram => ".png",
                ImageGeneratorApiType.BFLv11 => ".png",
                ImageGeneratorApiType.BFLv11Ultra => ".png",
                ImageGeneratorApiType.Dalle3 => ".png",
                ImageGeneratorApiType.Recraft => ".png", //actually, I should use the value read from head since it sometimes shows up as .svg.
                ImageGeneratorApiType.GptImage1 => ".png",
                ImageGeneratorApiType.GoogleNanoBanana => ".png",
                ImageGeneratorApiType.GoogleImagen4 => ".png",
                ImageGeneratorApiType.IdeogramV3 => ".png",
                _ => throw new ArgumentException("Unknown image generator type while picking file extension:", nameof(generator))
            };
        }
    }
}
