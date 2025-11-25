# Google Imagen API Options Reference

## Overview

Google provides two distinct APIs for image generation, each with different options:

1. **Google Imagen 4 (Vertex AI)** - Dedicated image generation model accessed via Vertex AI
2. **Google Gemini API (Nano Banana)** - Multimodal LLM with native image generation

---

## All Available Parameters

### Imagen 4 (Vertex AI) Parameters

| Parameter | Type | Values | Default | Description |
|-----------|------|--------|---------|-------------|
| `sampleImageSize` | string | `"1K"`, `"2K"` | `"1K"` | Output resolution (4K NOT supported) |
| `aspectRatio` | string | `"1:1"`, `"2:3"`, `"3:2"`, `"3:4"`, `"4:3"`, `"4:5"`, `"5:4"`, `"9:16"`, `"16:9"`, `"21:9"` | `"1:1"` | Image aspect ratio |
| `numberOfImages` | int | 1-4 | 1 | Number of images to generate |
| `enhancePrompt` | boolean | true/false | true | LLM-based prompt rewriting |
| `personGeneration` | string | `"allow_adult"`, `"dont_allow"`, `"ALLOW_ALL"` | `"allow_adult"` | Controls person/face generation |
| `safetySetting` | string | `"block_low_and_above"`, `"block_medium_and_above"`, `"block_only_high"` | `"block_medium_and_above"` | Safety filter threshold |
| `addWatermark` | boolean | true/false | false | Add SynthID digital watermark |
| `seed` | uint32 | any | random | Deterministic generation (only when addWatermark=false and enhancePrompt=false) |
| `includeRaiReason` | boolean | true/false | true | Include RAI filter reason in response |
| `outputOptions.mimeType` | string | `"image/png"`, `"image/jpeg"` | `"image/png"` | Output format |
| `outputOptions.compressionQuality` | int | 0-100 | 75 | JPEG compression (only for JPEG) |

### Gemini API Parameters

| Parameter | Type | Values | Default | Description |
|-----------|------|--------|---------|-------------|
| `imageConfig.imageSize` | string | `"1K"`, `"2K"`, `"4K"` | `"1K"` | Output resolution |
| `imageConfig.aspectRatio` | string | `"1:1"`, `"2:3"`, `"3:2"`, `"3:4"`, `"4:3"`, `"9:16"`, `"16:9"`, `"21:9"` | auto | Image aspect ratio |

---

## Resolution Details

| Setting | Approximate Resolution | Imagen 4 | Gemini |
|---------|----------------------|----------|--------|
| 1K | ~1024 × 1024 (for 1:1) | ✅ | ✅ |
| 2K | ~2048 × 2048 (for 1:1) | ✅ | ✅ |
| 4K | ~4096 × 4096 (for 1:1) | ❌ | ✅ |

For non-square aspect ratios, the longer dimension is constrained to the K value.

---

## Person Generation Settings

| Value | Description |
|-------|-------------|
| `allow_adult` | Default. Allow generation of adults only. Celebrity generation is blocked. |
| `dont_allow` | Disable all people/faces in generated images |
| `ALLOW_ALL` | Most permissive - allows all person generation |

---

## Safety Filter Levels

| Value | Description |
|-------|-------------|
| `block_low_and_above` | Highest safety - most filtering, fewest images pass |
| `block_medium_and_above` | Default - balanced filtering |
| `block_only_high` | Lowest safety - least filtering, may increase objectionable content |

---

## Output Format Options

| Format | Pros | Cons |
|--------|------|------|
| PNG | Lossless, supports transparency | Larger file size |
| JPEG | Smaller file size, configurable quality | Lossy compression, no transparency |

JPEG compression quality: 0 (smallest/worst) to 100 (largest/best), default 75.

---

## API Request Examples

### Imagen 4 (Full Options)

```json
{
  "instances": [
    {
      "prompt": "A serene mountain landscape at sunset"
    }
  ],
  "parameters": {
    "sampleImageSize": "2K",
    "sampleCount": 1,
    "aspectRatio": "16:9",
    "enhancePrompt": false,
    "personGeneration": "allow_adult",
    "safetySetting": "block_only_high",
    "addWatermark": false,
    "seed": 12345,
    "includeRaiReason": true,
    "outputOptions": {
      "mimeType": "image/jpeg",
      "compressionQuality": 90
    }
  }
}
```

### Gemini API

```json
{
  "contents": [
    {
      "parts": [
        { "text": "A serene mountain landscape at sunset" }
      ]
    }
  ],
  "generationConfig": {
    "responseModalities": ["TEXT", "IMAGE"],
    "imageConfig": {
      "imageSize": "4K",
      "aspectRatio": "16:9"
    }
  }
}
```

---

## Pricing Considerations

### Imagen 4
- Base price: ~$0.04 per image at 1K
- 2K resolution: ~$0.08 per image (estimated 2x)

### Gemini (Token-based)
- $30 per 1M output tokens
- ~1290 tokens per 1K image
- Higher resolutions consume proportionally more tokens (4x for 2K, 16x for 4K)

---

## References

- [Vertex AI Image Generation](https://cloud.google.com/vertex-ai/generative-ai/docs/image/generate-images)
- [Imagen API Reference](https://cloud.google.com/vertex-ai/generative-ai/docs/model-reference/imagen-api)
- [Set Output Resolution](https://cloud.google.com/vertex-ai/generative-ai/docs/image/set-output-resolution)
- [Configure Safety Settings](https://cloud.google.com/vertex-ai/generative-ai/docs/image/configure-responsible-ai-safety-settings)
- [Gemini API Image Generation](https://ai.google.dev/gemini-api/docs/image-generation)
