using System;

namespace MultiImageClient
{
    public enum ImageGeneratorApiType
    {
        Midjourney = 1,
        // RETIRED: OpenAI shut down dall-e-3 on 2026-05-12 (model_not_found).
        // Kept only so old saved JSON logs still deserialize.
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

        // Ideogram 4.0 (released 2026-06-03): multipart /generate for text
        // prompts and /remix for one attached image. Current v4 rejects FLASH.
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

        // Local ComfyUI workflow: FLUX.2 Klein 4B with an uncensored/ablated
        // Qwen3-4B text encoder. This is local/open-weight, not BFL's API.
        LocalFlux2Klein = 30,

        // xAI Grok Imagine image editing: POST /v1/images/edits with one or
        // more source images plus edit instructions.
        GrokImagineEdit = 31,

        // Local ComfyUI workflow: Alibaba/Tongyi-MAI Z-Image or Z-Image-Turbo.
        // This is local/open-weight and uses the same ComfyUI queue/history path.
        LocalZImage = 32,

        // Consumer grok.com session endpoints (browser cookies, not api.x.ai).
        GrokWebImagine = 33,
        GrokWebImaginePro = 34,
        GrokWebImagineVideo = 35,
        GrokWebImagineEdit = 36,

        // OpenAI gpt-image-2 image editing: POST /v1/images/edits with one or
        // more source images plus edit instructions. Do NOT send
        // input_fidelity (rejected on this model, confirmed 2026-07-06).
        GptImage2Edit = 37,

        // Consumer meta.ai session endpoint (browser cookies, not an official
        // Muse Image API — none exists yet). Reverse-engineered persisted-query
        // GraphQL, best-effort. See MetaWebClient.
        MetaWebImagine = 38,

        // Remaining public BFL model endpoints. Appended to preserve numeric
        // identities in existing generation archives.
        BFLFlux2Klein9bPreview = 39,
        BFLFluxPro = 40,
        BFLFluxDev = 41,

        // Additional Recraft V4.1 model IDs. Appended to preserve numeric
        // identities in existing generation archives.
        RecraftV41Utility = 42,
        RecraftV41Vector = 43,

        // Krea's own Krea 2 foundation image model. These are direct
        // api.krea.ai endpoints, not third-party models aggregated by Krea.
        Krea2MediumTurbo = 44,
        Krea2Medium = 45,
        Krea2Large = 46,

        WorkflowMock = 1000,
    }
}
