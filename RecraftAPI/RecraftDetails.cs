using MultiImageClient;

using System;

namespace RecraftAPIClient
{
    public class RecraftDetails : IDetails
    {
        private static Random _Random = new Random();
        public RecraftImageSize size { get; set; }
        public string style { get; set; } = "";
        public string substyle { get; set; } = "";
        public RecraftDetails() { }

        
    }
}
