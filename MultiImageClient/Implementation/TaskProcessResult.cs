using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MultiImageClient
{
    public class TaskProcessResult
    {
        public bool IsSuccess { get; set; }
        public GenericImageGenerationErrorType GenericImageErrorType { get; set; } = 0;
        public GenericTextGenerationErrorType GenericTextErrorType { get; set; } = 0;

        public string ErrorMessage { get; set; }

        /// <summary>
        /// gpt-image-1 returns the data as base64 encoded string, so we have already decoded it and just have it here.
        /// so, sometimes guy won't have Url but will have the image data.
        /// </summary>
        public string Url { get; set; }
        public IEnumerable<string> Base64ImageDatas { get; set; } = new List<string>();
        public string ContentType { get; set; }
        public PromptDetails PromptDetails { get; set; }
        public ImageGeneratorApiType ImageGenerator { get; set; }
        public string ImageGeneratorDescription { get; set; }
        public TextGeneratorApiType TextGenerator { get; set; }
        public long CreateTotalMs { get; set; } = 0;
        public long DownloadTotalMs { get; set; } = 0;
        private byte[] _ImageBytes { get; set; }
        public void SetImageBytes(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                IsSuccess = false;
                GenericImageErrorType = GenericImageGenerationErrorType.NoImagesGenerated;
                ErrorMessage = "No image data.";
                return;
            }
            if (_ImageBytes != null && _ImageBytes.Length > 0)
            {
                Console.WriteLine("Already seat.");
                throw new Exception("Already set image bytes.");
            }
            _ImageBytes = imageBytes;
        }   


        public override string ToString()
        {
            if (GenericImageErrorType != 0)
                return $"Error: {GenericImageErrorType} {ErrorMessage}";
            if (GenericTextErrorType != 0)
                return $"Error: {GenericTextErrorType} {ErrorMessage}";

            return $"Success. {PromptDetails}";
        }

        internal byte[] GetImageBytes()
        {
            if (_ImageBytes == null)
            {
                throw new Exception("No image bytes set.");
            }
            return _ImageBytes;
        }
    }
}
