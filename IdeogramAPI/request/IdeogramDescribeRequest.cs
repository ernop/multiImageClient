using System;
using System.IO;
using Newtonsoft.Json;

namespace IdeogramAPIClient
{
    public class IdeogramDescribeRequest
    {
        public byte[] ImageFile { get; set; } = Array.Empty<byte>();
        public string DescribeModelVersion { get; set; } = string.Empty;
    }
}
