namespace MultiImageClient
{
    public enum GoogleOutputMimeType
    {
        Png,   // Default - lossless
        Jpeg   // Lossy, smaller file size, supports compression quality
    }
    
    public static class GoogleOutputMimeTypeExtensions
    {
        public static string ToApiString(this GoogleOutputMimeType mimeType)
        {
            return mimeType switch
            {
                GoogleOutputMimeType.Png => "image/png",
                GoogleOutputMimeType.Jpeg => "image/jpeg",
                _ => "image/png"
            };
        }
        
        public static string ToFileExtension(this GoogleOutputMimeType mimeType)
        {
            return mimeType switch
            {
                GoogleOutputMimeType.Png => ".png",
                GoogleOutputMimeType.Jpeg => ".jpg",
                _ => ".png"
            };
        }
    }
}
