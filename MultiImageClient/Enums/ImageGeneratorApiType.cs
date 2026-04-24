using System;

namespace MultiImageClient
{
    public enum ImageGeneratorApiType
    {
        Midjourney = 1,
        Dalle3 = 2,
        Ideogram = 3,
        BFLv11 = 4,
        Recraft = 5,
        GptImage1 = 6,
        BFLv11Ultra = 7,
        GoogleNanoBanana = 8,
        GoogleImagen4 = 9,
        IdeogramV3 = 10,
        GptImage1Mini = 11,
        GptImage2 = 12,

        // FLUX.2 family (current BFL generation, launched 2025). Megapixel-priced.
        BFLFlux2Pro = 13,
        BFLFlux2Max = 14,
        BFLFlux2Flex = 15,
        BFLFlux2Klein4b = 16,
        BFLFlux2Klein9b = 17,

        // FLUX.1 Kontext — text + image editing
        BFLFluxKontextPro = 18,
        BFLFluxKontextMax = 19,

        // Recraft V4 (drop-in upgrade over V3)
        RecraftV4 = 20,
        RecraftV4Pro = 21,

        // xAI Grok Imagine (launched 2026-01-28). Two tiers:
        //   GrokImagine     -> grok-imagine-image        ($0.02/image, 300 rpm)
        //   GrokImaginePro  -> grok-imagine-image-pro    ($0.07/image,  30 rpm)
        GrokImagine = 22,
        GrokImaginePro = 23,
    }
}
