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
        public required string ImageGeneratorDescription { get; set; }
        public TextGeneratorApiType TextGenerator { get; set; }
        public long CreateTotalMs { get; set; } = 0;
        public long DownloadTotalMs { get; set; } = 0;

        // The model identity the provider REPORTED serving this result with,
        // when the transport exposes one (currently grok-web's imagine
        // WebSocket model_name/mode). Null elsewhere. Informational only:
        // lets runs say at runtime which server-side model produced the
        // images (e.g. after xAI's 2026-08-07 Imagine Image 2.0 rollout).
        public string ServedModelName { get; set; }
        public string ServedModelMode { get; set; }
        public string GeneratedMediaPath { get; set; }
        public string GeneratedMediaContentType { get; set; }
        public string GenerationAttemptId { get; set; } = "";
        private readonly Dictionary<int, Dictionary<SaveType, string>> _savedImagePaths =
            new Dictionary<int, Dictionary<SaveType, string>>();
        private readonly Dictionary<int, string> _savedRawImagePaths = new Dictionary<int, string>();
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

        public void SetSavedRawImagePath(int n, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _savedRawImagePaths[n] = path;
            }
        }

        public string GetSavedRawImagePath(int n)
            => _savedRawImagePaths.TryGetValue(n, out var path) ? path : "";

        public IReadOnlyList<string> GetSavedRawImagePaths()
            => _savedRawImagePaths
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => kvp.Value)
                .ToList();

        public void SetSavedImagePaths(int n, Dictionary<SaveType, string> paths)
        {
            _savedImagePaths[n] = new Dictionary<SaveType, string>(paths);
            if (paths.TryGetValue(SaveType.Raw, out var rawPath))
            {
                SetSavedRawImagePath(n, rawPath);
            }
        }

        public IReadOnlyDictionary<int, Dictionary<SaveType, string>> GetSavedImagePaths()
            => _savedImagePaths;

        public void ReleaseImageData()
        {
            _ImageBytes.Clear();
            _ImageBytes.TrimExcess();
            Base64ImageDatas = Array.Empty<CreatedBase64Image>();
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
