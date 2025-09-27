using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace IdeogramAPIClient
{
    public class IdeogramV3GenerateResponse
    {
        [JsonProperty("created")]
        public DateTime Created { get; set; }

        [JsonProperty("data")]
        public List<IdeogramV3ImageObject> Data { get; set; } = new List<IdeogramV3ImageObject>();
    }
}

