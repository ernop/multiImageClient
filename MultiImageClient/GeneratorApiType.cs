using System;

namespace MultiClientRunner
{
     public static class GeneratorApiTypeExtensions
    {
        public static string GetFileExtension(this GeneratorApiType generator)
        {
            return generator switch
            {
                GeneratorApiType.Ideogram => ".png",
                GeneratorApiType.BFL => ".jpg",
                GeneratorApiType.Dalle3 => ".png",
                _ => throw new ArgumentException("Unknown generator type", nameof(generator))
            };
        }
    }
}