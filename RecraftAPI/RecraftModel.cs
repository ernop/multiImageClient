namespace RecraftAPIClient
{
    /// Recraft raster/vector generation model. The API string is
    /// the lowercase enum name (e.g. "recraftv3", "recraftv4", "recraftv4_1").
    /// V4.1 (2026) is the current flagship; the API default model is
    /// recraftv4_1 as of June 2026. The _vector/_utility variants are
    /// expressed via the style parameter on our side, not separate enum
    /// members, except where Recraft exposes them as distinct model ids.
    public enum RecraftModel
    {
        recraftv2,
        recraftv3,
        recraftv4,
        recraftv4pro,
        recraftv4_1,
        recraftv4_1_pro,
    }
}
