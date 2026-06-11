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
                ImageGeneratorApiType.GptImage1Mini => ".png",
                ImageGeneratorApiType.GptImage2 => ".png",
                ImageGeneratorApiType.GoogleNanoBanana => ".png",
                ImageGeneratorApiType.GoogleNanoBananaPro => ".png",
                ImageGeneratorApiType.GoogleImagen4 => ".png",
                ImageGeneratorApiType.IdeogramV3 => ".png",
                ImageGeneratorApiType.IdeogramV4 => ".png",
                ImageGeneratorApiType.BFLFlux2Pro => ".png",
                ImageGeneratorApiType.BFLFlux2ProPreview => ".png",
                ImageGeneratorApiType.BFLFlux2Max => ".png",
                ImageGeneratorApiType.BFLFlux2Flex => ".png",
                ImageGeneratorApiType.BFLFlux2Klein4b => ".png",
                ImageGeneratorApiType.BFLFlux2Klein9b => ".png",
                ImageGeneratorApiType.BFLFluxKontextPro => ".png",
                ImageGeneratorApiType.BFLFluxKontextMax => ".png",
                ImageGeneratorApiType.RecraftV4 => ".png",
                ImageGeneratorApiType.RecraftV4Pro => ".png",
                ImageGeneratorApiType.RecraftV41 => ".png",
                ImageGeneratorApiType.RecraftV41Pro => ".png",
                ImageGeneratorApiType.GrokImagine => ".png",
                ImageGeneratorApiType.GrokImaginePro => ".png",
                ImageGeneratorApiType.GrokImagineVideo => ".mp4",
                _ => throw new ArgumentException("Unknown image generator type while picking file extension:", nameof(generator))
            };
        }
    }
}
