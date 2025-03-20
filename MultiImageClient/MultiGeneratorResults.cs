using System.Collections.Generic;


namespace MultiImageClient
{
    public class MultiGeneratorResults
    {
        public Dictionary<ImageGeneratorApiType, TaskProcessResult> results { get; set; } = new Dictionary<ImageGeneratorApiType, TaskProcessResult>();
    }
}
