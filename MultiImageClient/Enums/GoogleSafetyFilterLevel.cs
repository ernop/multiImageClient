namespace MultiImageClient
{
    public enum GoogleSafetyFilterLevel
    {
        BlockLowAndAbove,      // Highest safety - most filtering
        BlockMediumAndAbove,   // Default - balanced filtering  
        BlockOnlyHigh,         // Lowest safety - least filtering (may increase objectionable content)
        BlockNone              // Disable safety filtering entirely (if supported)
    }
    
    public static class GoogleSafetyFilterLevelExtensions
    {
        public static string ToApiString(this GoogleSafetyFilterLevel level)
        {
            return level switch
            {
                GoogleSafetyFilterLevel.BlockLowAndAbove => "block_low_and_above",
                GoogleSafetyFilterLevel.BlockMediumAndAbove => "block_medium_and_above",
                GoogleSafetyFilterLevel.BlockOnlyHigh => "block_only_high",
                GoogleSafetyFilterLevel.BlockNone => "BLOCK_NONE",
                _ => "block_medium_and_above"
            };
        }
    }
}
