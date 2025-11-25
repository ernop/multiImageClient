# Implementation Plan: Google Imagen Resolution Support

## Summary

Add support for 1K, 2K, and 4K resolution options to both Google image generators in the codebase:
- `GoogleGenerator` (Gemini/Nano Banana)
- `GoogleImagen4Generator` (Vertex AI Imagen 4)

## Existing Pattern Analysis

Looking at how other generators handle size/resolution:

| Generator | Size Parameter | Type |
|-----------|---------------|------|
| `RecraftGenerator` | `RecraftImageSize` | Enum with pixel dimensions |
| `IdeogramGenerator` | `IdeogramAspectRatio` | Enum with aspect ratios |
| `BFLGenerator` | `aspectRatio`, `width`, `height` | String + ints |
| `GptImageOneGenerator` | `size` | String ("1024x1024") |
| `Dalle3Generator` | `GeneratedImageSize` | OpenAI SDK enum |

The Google APIs use simple string values ("1K", "2K", "4K"), so we should create a clean enum for type safety.

---

## Implementation Steps

### Step 1: Create GoogleImageSize Enum

**File:** `MultiImageClient/Enums/GoogleImageSize.cs`

```csharp
namespace MultiImageClient
{
    public enum GoogleImageSize
    {
        Size1K,
        Size2K,
        Size4K  // Note: 4K only supported by Gemini, not Imagen 4
    }
    
    public static class GoogleImageSizeExtensions
    {
        public static string ToApiString(this GoogleImageSize size)
        {
            return size switch
            {
                GoogleImageSize.Size1K => "1K",
                GoogleImageSize.Size2K => "2K",
                GoogleImageSize.Size4K => "4K",
                _ => "1K"
            };
        }
    }
}
```

### Step 2: Update GoogleGenerator (Gemini/Nano Banana)

**File:** `MultiImageClient/ImageGenerators/GoogleGenerator.cs`

Changes needed:
1. Add `_imageSize` and `_aspectRatio` fields
2. Update constructor to accept these parameters
3. Modify the request body to include `imageConfig`
4. Update pricing based on resolution

```csharp
public class GoogleGenerator : IImageGenerator
{
    private SemaphoreSlim _googleSemaphore;
    private HttpClient _httpClient;
    private string _apiKey;
    private MultiClientRunStats _stats;
    private string _name;
    private ImageGeneratorApiType _apiType;
    private GoogleImageSize _imageSize;      // NEW
    private string _aspectRatio;              // NEW

    public GoogleGenerator(
        ImageGeneratorApiType apiType, 
        string apiKey, 
        int maxConcurrency,
        MultiClientRunStats stats, 
        string name = "",
        GoogleImageSize imageSize = GoogleImageSize.Size1K,    // NEW
        string aspectRatio = "1:1")                             // NEW
    {
        _apiKey = apiKey;
        _googleSemaphore = new SemaphoreSlim(maxConcurrency);
        _httpClient = new HttpClient();
        _name = string.IsNullOrEmpty(name) ? "" : name;
        _stats = stats;
        _apiType = apiType;
        _imageSize = imageSize;               // NEW
        _aspectRatio = aspectRatio;           // NEW
    }

    // In ProcessPromptAsync, update the request body:
    var requestBody = new
    {
        contents = new[]
        {
            new
            {
                parts = new[]
                {
                    new { text = promptDetails.Prompt }
                }
            }
        },
        generationConfig = new
        {
            responseModalities = new[] { "TEXT", "IMAGE" },
            imageConfig = new                                  // NEW
            {
                imageSize = _imageSize.ToApiString(),
                aspectRatio = _aspectRatio
            }
        }
    };

    // Update GetCost() to account for resolution
    public decimal GetCost()
    {
        if (_apiType == ImageGeneratorApiType.GoogleNanoBanana)
        {
            // Higher resolution = more tokens
            var baseTokens = 1290m;
            var multiplier = _imageSize switch
            {
                GoogleImageSize.Size1K => 1.0m,
                GoogleImageSize.Size2K => 4.0m,   // 2x2 = 4x pixels
                GoogleImageSize.Size4K => 16.0m,  // 4x4 = 16x pixels
                _ => 1.0m
            };
            return (30m / 1000000m) * baseTokens * multiplier;
        }
        // ... rest
    }

    // Update GetFilenamePart and GetGeneratorSpecPart to include resolution
    public string GetFilenamePart(PromptDetails pd)
    {
        return $"{_apiType}_{_imageSize.ToApiString()}_{_aspectRatio.Replace(":", "x")}";
    }
}
```

### Step 3: Update GoogleImagen4Generator

**File:** `MultiImageClient/ImageGenerators/GoogleImagen4Generator.cs`

Changes needed:
1. Add `_imageSize` field (only supports 1K and 2K)
2. Update constructor
3. Modify the request to include `sampleImageSize`

```csharp
public class GoogleImagen4Generator : IImageGenerator
{
    // ... existing fields ...
    private GoogleImageSize _imageSize;    // NEW

    public GoogleImagen4Generator(
        string apiKey, 
        int maxConcurrency,
        MultiClientRunStats stats, 
        string name, 
        string aspectRatio, 
        string safetyFilterLevel, 
        string location, 
        string projectId,
        string googleServiceAccountKeyPath,
        GoogleImageSize imageSize = GoogleImageSize.Size1K)   // NEW
    {
        // ... existing initialization ...
        
        // Validate: Imagen 4 doesn't support 4K
        if (imageSize == GoogleImageSize.Size4K)
        {
            throw new ArgumentException("Imagen 4 does not support 4K resolution. Use 1K or 2K.");
        }
        _imageSize = imageSize;
    }

    public async Task<TaskProcessResult> ProcessPromptAsync(...)
    {
        // Update the instance to include sampleImageSize:
        var instance = new Google.Protobuf.WellKnownTypes.Value
        {
            StructValue = new Google.Protobuf.WellKnownTypes.Struct
            {
                Fields = 
                {
                    { "prompt", Google.Protobuf.WellKnownTypes.Value.ForString(promptDetails.Prompt) },
                    { "numberOfImages", Google.Protobuf.WellKnownTypes.Value.ForNumber(1) },
                    { "aspectRatio", Google.Protobuf.WellKnownTypes.Value.ForString(_aspectRatio) },
                    { "sampleImageSize", Google.Protobuf.WellKnownTypes.Value.ForString(_imageSize.ToApiString()) },  // NEW
                    // ... other fields ...
                }
            }
        };
        // ...
    }

    // Update pricing based on resolution
    public decimal GetCost()
    {
        return _imageSize switch
        {
            GoogleImageSize.Size1K => 0.04m,
            GoogleImageSize.Size2K => 0.08m,  // Estimated - higher res likely costs more
            _ => 0.04m
        };
    }
}
```

### Step 4: Update GeneratorGroups.cs

**File:** `MultiImageClient/Workflows/GeneratorGroups.cs`

```csharp
// Example usage with different resolutions:

// Gemini/Nano Banana with various resolutions
var google_banana_1k = new GoogleGenerator(
    ImageGeneratorApiType.GoogleNanoBanana, 
    _settings.GoogleGeminiApiKey, 
    _concurrency, 
    _stats, 
    name: "banana-1k",
    imageSize: GoogleImageSize.Size1K,
    aspectRatio: "16:9");

var google_banana_2k = new GoogleGenerator(
    ImageGeneratorApiType.GoogleNanoBanana, 
    _settings.GoogleGeminiApiKey, 
    _concurrency, 
    _stats, 
    name: "banana-2k",
    imageSize: GoogleImageSize.Size2K,
    aspectRatio: "16:9");

var google_banana_4k = new GoogleGenerator(
    ImageGeneratorApiType.GoogleNanoBanana, 
    _settings.GoogleGeminiApiKey, 
    _concurrency, 
    _stats, 
    name: "banana-4k",
    imageSize: GoogleImageSize.Size4K,
    aspectRatio: "1:1");

// Imagen 4 with various resolutions (no 4K support)
var googleimagen_1k = new GoogleImagen4Generator(
    _settings.GoogleGeminiApiKey, 
    _concurrency, 
    _stats, 
    name: "imagen4-1k", 
    aspectRatio: "16:9", 
    safetyFilterLevel: "BLOCK_NONE", 
    location: _settings.GoogleCloudLocation, 
    projectId: _settings.GoogleCloudProjectId, 
    googleServiceAccountKeyPath: _settings.GoogleServiceAccountKeyPath,
    imageSize: GoogleImageSize.Size1K);

var googleimagen_2k = new GoogleImagen4Generator(
    _settings.GoogleGeminiApiKey, 
    _concurrency, 
    _stats, 
    name: "imagen4-2k", 
    aspectRatio: "16:9", 
    safetyFilterLevel: "BLOCK_NONE", 
    location: _settings.GoogleCloudLocation, 
    projectId: _settings.GoogleCloudProjectId, 
    googleServiceAccountKeyPath: _settings.GoogleServiceAccountKeyPath,
    imageSize: GoogleImageSize.Size2K);
```

---

## Files to Create/Modify

| File | Action | Description |
|------|--------|-------------|
| `MultiImageClient/Enums/GoogleImageSize.cs` | **CREATE** | New enum for resolution options |
| `MultiImageClient/ImageGenerators/GoogleGenerator.cs` | MODIFY | Add imageSize & aspectRatio params |
| `MultiImageClient/ImageGenerators/GoogleImagen4Generator.cs` | MODIFY | Add sampleImageSize param |
| `MultiImageClient/Workflows/GeneratorGroups.cs` | MODIFY | Update instantiation examples |

---

## Testing Checklist

- [ ] Verify 1K resolution works for GoogleGenerator (Gemini)
- [ ] Verify 2K resolution works for GoogleGenerator (Gemini)
- [ ] Verify 4K resolution works for GoogleGenerator (Gemini)
- [ ] Verify 1K resolution works for GoogleImagen4Generator
- [ ] Verify 2K resolution works for GoogleImagen4Generator
- [ ] Verify 4K throws appropriate error for GoogleImagen4Generator
- [ ] Verify aspect ratio combinations work with different resolutions
- [ ] Verify cost calculations are updated appropriately
- [ ] Verify file naming includes resolution info

---

## Comparison with Other Generators

This implementation follows established patterns:

| Feature | GoogleGenerator | RecraftGenerator | IdeogramGenerator |
|---------|----------------|------------------|-------------------|
| Size enum | `GoogleImageSize` | `RecraftImageSize` | N/A (aspect only) |
| Aspect param | String | N/A (part of size) | Enum |
| Default size | 1K | 1024x1024 | N/A |
| Max size | 4K (Gemini) | 2048x1024 | 1536x1536 |

---

## Notes

1. **4K Limitation**: Only Gemini supports 4K; Imagen 4 maxes out at 2K
2. **Pricing**: Higher resolutions will increase costs - exact pricing TBD
3. **Quality vs Speed**: Larger images take longer to generate
4. **Aspect Ratio Interaction**: Resolution constrains the longer dimension while aspect ratio is maintained
