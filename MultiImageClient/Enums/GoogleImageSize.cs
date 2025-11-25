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
