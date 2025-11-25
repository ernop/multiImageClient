namespace MultiImageClient
{
    /// <summary>
    /// Configuration options for Google image generation APIs (both Gemini and Imagen 4).
    /// Not all options are supported by all APIs - see individual property docs.
    /// </summary>
    public class GoogleImageGenerationOptions
    {
        /// <summary>
        /// Output image resolution. Gemini supports 1K/2K/4K, Imagen 4 only supports 1K/2K.
        /// Default: Size1K
        /// </summary>
        public GoogleImageSize ImageSize { get; set; } = GoogleImageSize.Size1K;
        
        /// <summary>
        /// Aspect ratio of generated images.
        /// Supported: 1:1, 2:3, 3:2, 3:4, 4:3, 4:5, 5:4, 9:16, 16:9, 21:9
        /// Default: Ratio1x1
        /// </summary>
        public GoogleImageAspectRatio AspectRatio { get; set; } = GoogleImageAspectRatio.Ratio1x1;
        
        /// <summary>
        /// Controls generation of people/faces in images.
        /// Imagen 4 only. Default: AllowAdult
        /// </summary>
        public GooglePersonGeneration PersonGeneration { get; set; } = GooglePersonGeneration.AllowAdult;
        
        /// <summary>
        /// Safety filter threshold level.
        /// Imagen 4 only. Default: BlockMediumAndAbove
        /// </summary>
        public GoogleSafetyFilterLevel SafetyFilterLevel { get; set; } = GoogleSafetyFilterLevel.BlockMediumAndAbove;
        
        /// <summary>
        /// Output image format.
        /// Imagen 4 only. Default: Png
        /// </summary>
        public GoogleOutputMimeType OutputMimeType { get; set; } = GoogleOutputMimeType.Png;
        
        /// <summary>
        /// JPEG compression quality (0-100). Only applies when OutputMimeType is Jpeg.
        /// Imagen 4 only. Default: 75
        /// </summary>
        public int CompressionQuality { get; set; } = 75;
        
        /// <summary>
        /// Whether to use LLM-based prompt enhancement for higher quality images.
        /// Imagen 4 only. Default: false (to preserve exact prompts)
        /// </summary>
        public bool EnhancePrompt { get; set; } = false;
        
        /// <summary>
        /// Whether to add a SynthID digital watermark to generated images.
        /// When true, seed parameter is ignored.
        /// Imagen 4 only. Default: false
        /// </summary>
        public bool AddWatermark { get; set; } = false;
        
        /// <summary>
        /// Random seed for deterministic output. Only works when AddWatermark=false and EnhancePrompt=false.
        /// Set to null for random generation.
        /// Imagen 4 only. Default: null
        /// </summary>
        public uint? Seed { get; set; } = null;
        
        /// <summary>
        /// Number of images to generate per request (1-4).
        /// Imagen 4 only. Default: 1
        /// </summary>
        public int NumberOfImages { get; set; } = 1;
        
        /// <summary>
        /// Whether to include RAI (Responsible AI) filter reason in responses.
        /// Imagen 4 only. Default: true
        /// </summary>
        public bool IncludeRaiReason { get; set; } = true;
        
        /// <summary>
        /// Creates a copy of this options object with potentially modified values.
        /// </summary>
        public GoogleImageGenerationOptions Clone()
        {
            return new GoogleImageGenerationOptions
            {
                ImageSize = this.ImageSize,
                AspectRatio = this.AspectRatio,
                PersonGeneration = this.PersonGeneration,
                SafetyFilterLevel = this.SafetyFilterLevel,
                OutputMimeType = this.OutputMimeType,
                CompressionQuality = this.CompressionQuality,
                EnhancePrompt = this.EnhancePrompt,
                AddWatermark = this.AddWatermark,
                Seed = this.Seed,
                NumberOfImages = this.NumberOfImages,
                IncludeRaiReason = this.IncludeRaiReason
            };
        }
        
        /// <summary>
        /// Validates options for Imagen 4 API compatibility.
        /// </summary>
        public void ValidateForImagen4()
        {
            if (ImageSize == GoogleImageSize.Size4K)
            {
                throw new System.ArgumentException("Imagen 4 does not support 4K resolution. Use 1K or 2K.");
            }
            
            if (NumberOfImages < 1 || NumberOfImages > 4)
            {
                throw new System.ArgumentException("NumberOfImages must be between 1 and 4.");
            }
            
            if (CompressionQuality < 0 || CompressionQuality > 100)
            {
                throw new System.ArgumentException("CompressionQuality must be between 0 and 100.");
            }
        }
        
        /// <summary>
        /// Gets a string representation of the key options for display/logging.
        /// </summary>
        public string ToDisplayString()
        {
            return $"{ImageSize.ToApiString()} {AspectRatio.ToApiString()}";
        }
        
        /// <summary>
        /// Gets a filename-safe string representation of key options.
        /// </summary>
        public string ToFilenamePart()
        {
            return $"{ImageSize.ToApiString()}_{AspectRatio.ToApiString().Replace(":", "x")}";
        }
    }
}
