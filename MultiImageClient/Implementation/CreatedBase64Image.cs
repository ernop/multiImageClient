namespace MultiImageClient
{
    /// some generators return multiple images per query. we sort of mix doing the query with getting the result including saving the new prompt
    public class CreatedBase64Image
    {
        public string bytesBase64 { get; set; }
        public string newPrompt { get; set; }
    }
}
