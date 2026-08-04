namespace RecraftAPIClient
{
    /// Recraft generation model. Enum names intentionally match the exact API
    /// model IDs because Recraft exposes raster, vector, Pro, and Utility as
    /// separate models rather than style options.
    public enum RecraftModel
    {
        recraftv2,
        recraftv3,
        recraftv4,
        recraftv4_pro,
        recraftv4_1,
        recraftv4_1_pro,
        recraftv4_1_vector,
        recraftv4_1_utility,
    }
}
