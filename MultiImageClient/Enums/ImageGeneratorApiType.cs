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

        // xAI Grok Imagine VIDEO (grok-imagine-video). Asynchronous: start +
        // poll. Produces mp4, not png — the generator saves the clip itself
        // and returns a rendered "video card" for the combined grid.
        GrokImagineVideo = 24,

        // Ideogram 4.0 (released 2026-06-03): POST /v1/ideogram-v4/generate,
        // JSON body, 2K-native output, rendering_speed FLASH|TURBO|DEFAULT|QUALITY.
        IdeogramV4 = 25,

        // Recraft V4.1 family (2026). API model strings recraftv4_1 / recraftv4_1_pro.
        RecraftV41 = 26,
        RecraftV41Pro = 27,

        // Google Gemini 3 Pro Image ("Nano Banana Pro") — professional tier,
        // advanced reasoning, up to 4K. The flash tier (GoogleNanoBanana)
        // now maps to gemini-3.1-flash-image ("Nano Banana 2").
        GoogleNanoBananaPro = 28,

        // BFL flux-2-pro-preview: where BFL lands the latest [pro]
        // improvements first. Same API contract as flux-2-pro.
        BFLFlux2ProPreview = 29,
    }
}
