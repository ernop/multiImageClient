using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using System.IO;
using Google.Cloud.AIPlatform.V1;
using Google.Protobuf.WellKnownTypes;
using System.Linq;

namespace MultiImageClient
{
    public class GoogleImagen4Generator : IImageGenerator
    {
        private SemaphoreSlim _googleSemaphore;
        private PredictionServiceClient _predictionServiceClient;
        private string _apiKey;
        private MultiClientRunStats _stats;
        private string _name;
        private string _location;
        private string _projectId;
        private string _googleServiceAccountKeyPath;
        private GoogleCredential _credential;
        private GoogleImageGenerationOptions _options;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GoogleImagen4;

        public GoogleImagen4Generator(
            string apiKey, 
            int maxConcurrency,
            MultiClientRunStats stats, 
            string name, 
            string location, 
            string projectId,
            string googleServiceAccountKeyPath,
            GoogleImageGenerationOptions options = null)
        {
            _options = options ?? new GoogleImageGenerationOptions();
            _options.ValidateForImagen4();
            
            _apiKey = apiKey;
            _googleSemaphore = new SemaphoreSlim(maxConcurrency);
            _location = location;
            _projectId = projectId;
            _googleServiceAccountKeyPath = googleServiceAccountKeyPath;

            if (!string.IsNullOrEmpty(_googleServiceAccountKeyPath))
            {
                _credential = GoogleCredential.FromFile(_googleServiceAccountKeyPath)
                    .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
            } else {
                _credential = GoogleCredential.GetApplicationDefault()
                    .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
            }

            _predictionServiceClient = new PredictionServiceClientBuilder
            {
                Endpoint = $"{location}-aiplatform.googleapis.com",
                Credential = _credential
            }.Build();
            
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _stats = stats;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var namePart = string.IsNullOrEmpty(_name) ? "" : $"-{_name}";
            return $"google-imagen4{namePart}_{_options.ToFilenamePart()}";
        }

        public decimal GetCost()
        {
            // Imagen 4 pricing - higher resolutions cost more
            return _options.ImageSize switch
            {
                GoogleImageSize.Size1K => 0.04m,
                GoogleImageSize.Size2K => 0.08m,
                _ => 0.04m
            };
        }

        public List<string> GetRightParts()
        {
            var namePart = string.IsNullOrEmpty(_name) ? "" : _name;
            return new List<string> { "imagen4", namePart, _options.ImageSize.ToApiString(), _options.AspectRatio.ToApiString() };
        }

        public string GetGeneratorSpecPart()
        {
            if (string.IsNullOrEmpty(_name))
            {
                return $"google-imagen4\n{_options.ToDisplayString()}";
            }
            else
            {
                return $"{_name}\n{_options.ToDisplayString()}";
            }
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _googleSemaphore.WaitAsync();
            try
            {
                _stats.GoogleRequestCount++;

                // Google Vertex AI endpoint for Imagen 4
                var apiUrl = $"https://{_location}-aiplatform.googleapis.com/v1/projects/{_projectId}/locations/{_location}/publishers/google/models/imagen-4.0-generate-001:predict";

                // Build the fields dictionary with all options
                var fields = new Dictionary<string, Google.Protobuf.WellKnownTypes.Value>
                {
                    { "prompt", Google.Protobuf.WellKnownTypes.Value.ForString(promptDetails.Prompt) },
                    { "numberOfImages", Google.Protobuf.WellKnownTypes.Value.ForNumber(_options.NumberOfImages) },
                    { "aspectRatio", Google.Protobuf.WellKnownTypes.Value.ForString(_options.AspectRatio.ToApiString()) },
                    { "sampleImageSize", Google.Protobuf.WellKnownTypes.Value.ForString(_options.ImageSize.ToApiString()) },
                    { "enhancePrompt", Google.Protobuf.WellKnownTypes.Value.ForBool(_options.EnhancePrompt) },
                    { "includeRaiReason", Google.Protobuf.WellKnownTypes.Value.ForBool(_options.IncludeRaiReason) },
                    { "safetySetting", Google.Protobuf.WellKnownTypes.Value.ForString(_options.SafetyFilterLevel.ToApiString()) },
                    { "personGeneration", Google.Protobuf.WellKnownTypes.Value.ForString(_options.PersonGeneration.ToApiString()) },
                    { "addWatermark", Google.Protobuf.WellKnownTypes.Value.ForBool(_options.AddWatermark) }
                };
                
                // Add seed if specified and watermark is disabled
                if (_options.Seed.HasValue && !_options.AddWatermark && !_options.EnhancePrompt)
                {
                    fields.Add("seed", Google.Protobuf.WellKnownTypes.Value.ForNumber(_options.Seed.Value));
                }
                
                // Add output options if not using defaults
                if (_options.OutputMimeType != GoogleOutputMimeType.Png)
                {
                    var outputOptions = new Google.Protobuf.WellKnownTypes.Struct();
                    outputOptions.Fields.Add("mimeType", Google.Protobuf.WellKnownTypes.Value.ForString(_options.OutputMimeType.ToApiString()));
                    
                    if (_options.OutputMimeType == GoogleOutputMimeType.Jpeg)
                    {
                        outputOptions.Fields.Add("compressionQuality", Google.Protobuf.WellKnownTypes.Value.ForNumber(_options.CompressionQuality));
                    }
                    
                    fields.Add("outputOptions", Google.Protobuf.WellKnownTypes.Value.ForStruct(outputOptions));
                }

                // Construct the instance for the predict request
                var instanceStruct = new Google.Protobuf.WellKnownTypes.Struct();
                foreach (var field in fields)
                {
                    instanceStruct.Fields.Add(field.Key, field.Value);
                }
                
                var instance = new Google.Protobuf.WellKnownTypes.Value
                {
                    StructValue = instanceStruct
                };

                var instances = new List<Google.Protobuf.WellKnownTypes.Value> { instance };
                var parameters = new Google.Protobuf.WellKnownTypes.Value();

                var endpoint = EndpointName.FromProjectLocationPublisherModel(_projectId, _location, "google", "imagen-4.0-generate-001");
                
                var response = await _predictionServiceClient.PredictAsync(endpoint, instances, parameters);
                
                var base64Images = new List<CreatedBase64Image>();
                string commonMimeType = _options.OutputMimeType.ToApiString();

                if (response?.Predictions != null && response.Predictions.Any())
                {
                    foreach (var prediction in response.Predictions)
                    {
                        if (prediction?.StructValue?.Fields != null)
                        {
                            var predictionFields = prediction.StructValue.Fields;
                            if (predictionFields.ContainsKey("bytesBase64Encoded") && predictionFields.ContainsKey("mimeType"))
                            {
                                var imageData = predictionFields["bytesBase64Encoded"].StringValue;
                                var newPrompt = predictionFields.ContainsKey("prompt") ? predictionFields["prompt"].StringValue : promptDetails.Prompt;
                                var currentMimeType = predictionFields["mimeType"].StringValue;

                                if (!string.IsNullOrEmpty(imageData))
                                {
                                    var bd = new CreatedBase64Image
                                    {
                                        bytesBase64 = imageData,
                                        newPrompt = newPrompt,
                                    };

                                    base64Images.Add(bd);
                                    if (!string.IsNullOrEmpty(currentMimeType))
                                    {
                                        commonMimeType = currentMimeType;
                                    }
                                }
                            }
                        }
                    }
                }

                if (base64Images.Count == 0)
                {
                    return new TaskProcessResult 
                    {
                        IsSuccess = false,
                        ErrorMessage = "No image data returned from Google Imagen 4 API",
                        PromptDetails = promptDetails,
                        ImageGenerator = ImageGeneratorApiType.GoogleImagen4,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                    };
                }

                return new TaskProcessResult 
                { 
                    IsSuccess = true, 
                    Base64ImageDatas = base64Images,
                    ContentType = commonMimeType,
                    ErrorMessage = "", 
                    PromptDetails = promptDetails, 
                    ImageGenerator = ImageGeneratorApiType.GoogleImagen4, 
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart() 
                };
            }
            catch (Exception ex)
            {
                var errorMessage = $"Google Imagen 4 Generator error: {ex.Message}";
                return new TaskProcessResult 
                { 
                    IsSuccess = false, 
                    ErrorMessage = errorMessage.Split("Support").FirstOrDefault(), 
                    PromptDetails = promptDetails, 
                    ImageGenerator = ImageGeneratorApiType.GoogleImagen4, 
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart() 
                };
            }
            finally
            {
                _googleSemaphore.Release();
            }
        }

        public void Dispose()
        {
            _googleSemaphore?.Dispose();
        }
    }

    // Shared response models for Google Imagen API
    public class GoogleImagenResponse
    {
        public GoogleImagenGeneratedImage[] GeneratedImages { get; set; }
    }

    public class GoogleImagenGeneratedImage
    {
        public string BytesBase64Encoded { get; set; }
        public string MimeType { get; set; }
    }

    // Response models for Gemini native image generation (Nano Banana)
    public class GeminiGenerateContentResponse
    {
        public GeminiCandidate[] candidates { get; set; }
    }

    public class GeminiCandidate
    {
        public GeminiContent content { get; set; }
    }

    public class GeminiContent
    {
        public GeminiPart[] parts { get; set; }
    }

    public class GeminiPart
    {
        public string text { get; set; }
        public GeminiInlineData inlineData { get; set; }
    }

    public class GeminiInlineData
    {
        public string mimeType { get; set; }
        public string data { get; set; }
    }
}
