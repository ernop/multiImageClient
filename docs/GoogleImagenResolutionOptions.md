# Google Imagen API Resolution Options

## Overview

Google provides two distinct APIs for image generation, each with different resolution options:

1. **Google Imagen 4 (Vertex AI)** - Dedicated image generation model accessed via Vertex AI
2. **Google Gemini API (Nano Banana)** - Multimodal LLM with native image generation

---

## Imagen 4 (Vertex AI) - `imagen-4.0-generate-001`

### Supported Models
- `imagen-4.0-generate-001`
- `imagen-4.0-ultra-generate-001`

### Resolution Parameter: `sampleImageSize`

| Value | Description |
|-------|-------------|
| `"1K"` | ~1024px (default) |
| `"2K"` | ~2048px |

**Note:** 4K is NOT supported for Imagen 4 according to current documentation.

### API Request Structure (REST)

```json
{
  "instances": [
    {
      "prompt": "TEXT_PROMPT"
    }
  ],
  "parameters": {
    "sampleImageSize": "2K",
    "sampleCount": 1
  }
}
```

### Aspect Ratio Support
Imagen 4 also supports `aspectRatio` parameter with these values:
- `"1:1"`, `"2:3"`, `"3:2"`, `"3:4"`, `"4:3"`, `"4:5"`, `"5:4"`, `"9:16"`, `"16:9"`, `"21:9"`

---

## Gemini API (Native Image Generation - "Nano Banana")

### Resolution Parameter: `imageSize` (within `imageConfig`)

| Value | Description |
|-------|-------------|
| `"1K"` | ~1024px |
| `"2K"` | ~2048px |
| `"4K"` | ~4096px |

### Aspect Ratio Support
Same as Imagen 4:
- `"1:1"`, `"2:3"`, `"3:2"`, `"3:4"`, `"4:3"`, `"4:5"`, `"5:4"`, `"9:16"`, `"16:9"`, `"21:9"`

### API Request Structure (REST)

```json
{
  "contents": [
    {
      "parts": [
        { "text": "PROMPT" }
      ]
    }
  ],
  "generationConfig": {
    "responseModalities": ["TEXT", "IMAGE"],
    "imageConfig": {
      "aspectRatio": "16:9",
      "imageSize": "2K"
    }
  }
}
```

### Python SDK Example

```python
from google import genai
from google.genai import types

client = genai.Client()

response = client.models.generate_content(
    model="gemini-2.0-flash-exp",
    contents="Create an image of a dog",
    config=types.GenerateContentConfig(
        response_modalities=["TEXT", "IMAGE"],
        image_config=types.ImageConfig(
            aspect_ratio="16:9",
            image_size="2K"  # "1K", "2K", or "4K"
        ),
    ),
)
```

---

## Resolution vs Pixel Dimensions Summary

| Setting | Approximate Resolution |
|---------|----------------------|
| 1K | 1024 × 1024 (for 1:1) |
| 2K | 2048 × 2048 (for 1:1) |
| 4K | 4096 × 4096 (for 1:1, Gemini only) |

For non-square aspect ratios, the longer dimension is constrained to the K value while maintaining the aspect ratio.

---

## Pricing Considerations

Higher resolutions may incur higher costs:
- **Imagen 4**: ~$0.04 per image (standard resolution)
- **Gemini Flash Image**: Token-based pricing (~$30/1M output tokens, ~1290 tokens per image at 1K)

Higher resolutions likely consume more tokens for Gemini and may have different pricing tiers for Imagen.

---

## References

- [Vertex AI Set Output Resolution](https://cloud.google.com/vertex-ai/generative-ai/docs/image/set-output-resolution)
- [Gemini API Image Generation](https://ai.google.dev/gemini-api/docs/image-generation)
- [Configure Aspect Ratio](https://cloud.google.com/vertex-ai/generative-ai/docs/image/configure-aspect-ratio)
