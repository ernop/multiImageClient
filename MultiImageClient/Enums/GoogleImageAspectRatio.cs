namespace MultiImageClient
{
    public enum GoogleImageAspectRatio
    {
        Ratio1x1,
        Ratio2x3,
        Ratio3x2,
        Ratio3x4,
        Ratio4x3,
        Ratio4x5,
        Ratio5x4,
        Ratio9x16,
        Ratio16x9,
        Ratio21x9
    }
    
    public static class GoogleImageAspectRatioExtensions
    {
        public static string ToApiString(this GoogleImageAspectRatio ratio)
        {
            return ratio switch
            {
                GoogleImageAspectRatio.Ratio1x1 => "1:1",
                GoogleImageAspectRatio.Ratio2x3 => "2:3",
                GoogleImageAspectRatio.Ratio3x2 => "3:2",
                GoogleImageAspectRatio.Ratio3x4 => "3:4",
                GoogleImageAspectRatio.Ratio4x3 => "4:3",
                GoogleImageAspectRatio.Ratio4x5 => "4:5",
                GoogleImageAspectRatio.Ratio5x4 => "5:4",
                GoogleImageAspectRatio.Ratio9x16 => "9:16",
                GoogleImageAspectRatio.Ratio16x9 => "16:9",
                GoogleImageAspectRatio.Ratio21x9 => "21:9",
                _ => "1:1"
            };
        }
    }
}
