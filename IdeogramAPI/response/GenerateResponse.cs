using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.IO;
using System.Net;

namespace IdeogramAPIClient
{
    public class GenerateResponse
    {
        [JsonProperty("created")]
        public DateTime Created { get; set; }

        [JsonProperty("data")]
        public List<ImageObject> Data { get; set; }
    }
}
