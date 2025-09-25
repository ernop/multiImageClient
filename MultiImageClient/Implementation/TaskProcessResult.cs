using Google.Protobuf;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MultiImageClient
{

    public class TaskProcessResult
    {
        public bool IsSuccess { get; set; }
        public GenericImageGenerationErrorType GenericImageErrorType { get; set; } = 0;
        public GenericTextGenerationErrorType GenericTextErrorType { get; set; } = 0;

        public string ErrorMessage { get; set; }
        
        // to make multi
        public string Url { get; set; }

        /// gpt-image-1 returns the data as base64 encoded string, so we have already decoded it and just have it here.
        /// so, sometimes guy won't have Url but will have the image data.
        public IEnumerable<CreatedBase64Image> Base64ImageDatas { get; set; } = new List<CreatedBase64Image>();
        public string ContentType { get; set; }
        public PromptDetails PromptDetails { get; set; }
        public ImageGeneratorApiType ImageGenerator { get; set; }
        public string ImageGeneratorDescription { get; set; }
        public TextGeneratorApiType TextGenerator { get; set; }
        public long CreateTotalMs { get; set; } = 0;
        public long DownloadTotalMs { get; set; } = 0;
        private Dictionary<int, byte[]> _ImageBytes { get; set; } = new Dictionary<int, byte[]>();
        public IEnumerable<byte[]> GetAllImages
        {
            get { return _ImageBytes.Values; }
        }
        
        public void SetImageBytes(int n, byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                IsSuccess = false;
                GenericImageErrorType = GenericImageGenerationErrorType.NoImagesGenerated;
                ErrorMessage = "No image data.";
                return;
            }
            if (_ImageBytes.ContainsKey(n))
            {
                throw new Exception("double");
            }

            _ImageBytes[n] = imageBytes;
        }   


        public override string ToString()
        {
            if (GenericImageErrorType != 0)
                return $"Error: {GenericImageErrorType} {ErrorMessage}";
            if (GenericTextErrorType != 0)
                return $"Error: {GenericTextErrorType} {ErrorMessage}";

            return $"Success. {PromptDetails}";
        }

        internal byte[] GetImageBytes(int n)
        {
            if (_ImageBytes == null)
            {
                throw new Exception("No image bytes set.");
            }
            return _ImageBytes[n];
        }
    }
}
