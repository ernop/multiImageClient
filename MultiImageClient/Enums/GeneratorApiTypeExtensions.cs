using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient.Enums
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
                _ => throw new ArgumentException("Unknown generator type", nameof(generator))
            };
        }
    }
}
