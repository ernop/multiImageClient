using Newtonsoft.Json;
using System.Collections.Generic;

namespace IdeogramAPIClient
{
    public class IdeogramDescription
    {
        [JsonProperty("text")]
        public string Text { get; set; }
    }

    public class IdeogramDescribeResponse
    {
        [JsonProperty("descriptions")]
        public List<IdeogramDescription> Descriptions { get; set; }
    }
}
