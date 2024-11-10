using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.IO;
using System.Net;

namespace IdeogramAPIClient
{
    public class VisionResponse
    {
        [JsonProperty("descriptions")]
        public List<string> Descriptions { get; set; }
    }
}