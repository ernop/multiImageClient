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
                ImageGeneratorApiType.BFL => ".jpg",
                ImageGeneratorApiType.Dalle3 => ".png",
                ImageGeneratorApiType.Recraft => ".png", //actually, I should use the value read from head since it sometimes shows up as .svg.
                _ => throw new ArgumentException("Unknown image generator type while picking file extension:", nameof(generator))
            };
        }
    }
}
