using MultiImageClient;

namespace RecraftAPIClient
{
    public class RecraftDetails : IDetails
    {
        public RecraftImageSize size { get; set; }
        public string style { get; set; } = "";
        public string substyle { get; set; } = "";

        public RecraftDetails() { }

        public string GetFullStyleName()
        {
            switch (style)
            {
                case "digital_illustration":
                    return $"{nameof(RecraftStyle.digital_illustration)} - {substyle}";
                case "realistic_image":
                    return $"{nameof(RecraftStyle.realistic_image)} - {substyle}";
                case "vector_illustration":
                    return $"{nameof(RecraftStyle.vector_illustration)} - {substyle}";
                case "any":
                    return "Any Style";
                default:
                    return "Unknown";
            }
        }

        public string GetDescription()
        {
            return $"{size}-{style}-{substyle}";
        }

        public RecraftDetails(RecraftDetails other)
        {
            size = other.size;
            substyle = other.substyle;
            style = other.style;
        }
    }
}
