using System;

namespace IdeogramAPIClient
{
    public class IdeogramFile
    {
        public IdeogramFile(byte[] content, string fileName, string contentType = "application/octet-stream")
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            FileName = string.IsNullOrWhiteSpace(fileName) ? "file" : fileName;
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        }

        public byte[] Content { get; }

        public string FileName { get; }

        public string ContentType { get; }
    }
}

