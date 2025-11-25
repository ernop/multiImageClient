namespace MultiImageClient
{
    public enum GooglePersonGeneration
    {
        AllowAdult,   // Default - allow generation of adults only (no celebrities)
        DontAllow,    // Disable people/faces in generated images
        AllowAll      // Allow all person generation (most permissive)
    }
    
    public static class GooglePersonGenerationExtensions
    {
        public static string ToApiString(this GooglePersonGeneration setting)
        {
            return setting switch
            {
                GooglePersonGeneration.AllowAdult => "allow_adult",
                GooglePersonGeneration.DontAllow => "dont_allow",
                GooglePersonGeneration.AllowAll => "ALLOW_ALL",
                _ => "allow_adult"
            };
        }
    }
}
