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
        private string _aspectRatio;
        private string _safetyFilterLevel;
        private string _location;
        private string _projectId;
        private string _googleServiceAccountKeyPath;
        private GoogleCredential _credential;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GoogleImagen4;

        public GoogleImagen4Generator(string apiKey, int maxConcurrency,
            MultiClientRunStats stats, string name, 
            string aspectRatio, 
            string safetyFilterLevel, 
            string location, 
            string projectId,
            string googleServiceAccountKeyPath)

        {
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
            _aspectRatio = aspectRatio;
            _safetyFilterLevel = safetyFilterLevel;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var namePart = string.IsNullOrEmpty(_name) ? "" : $"-{_name}";
            return $"google-imagen4{namePart}";
        }

        public decimal GetCost()
        {
            // Imagen 4 pricing (higher than Imagen 3)
            return 0.04m;
        }

        public List<string> GetRightParts()
        {
            var namePart = string.IsNullOrEmpty(_name) ? "" : _name;
            return new List<string> { "imagen4", namePart };
        }

        public string GetGeneratorSpecPart()
        {
            if (string.IsNullOrEmpty(_name))
            {
                return "google-imagen4";
            }
            else
            {
                return _name;
            }
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _googleSemaphore.WaitAsync();
            try
            {
                _stats.GoogleRequestCount++;

                // Google Gemini API endpoint for Imagen 4
                var apiUrl = $"https://{_location}-aiplatform.googleapis.com/v1/projects/{_projectId}/locations/{_location}/publishers/google/models/imagen-4.0-generate-001:predict";

                // Construct the instance for the predict request
                var instance = new Google.Protobuf.WellKnownTypes.Value
                {
                    StructValue = new Google.Protobuf.WellKnownTypes.Struct
                    {
                        Fields = 
                        {
                            { "prompt", Google.Protobuf.WellKnownTypes.Value.ForString(promptDetails.Prompt) },
                            { "numberOfImages", Google.Protobuf.WellKnownTypes.Value.ForNumber(1) },
                            { "aspectRatio", Google.Protobuf.WellKnownTypes.Value.ForString(_aspectRatio) },
                            { "enhancePrompt", Google.Protobuf.WellKnownTypes.Value.ForBool(false) },
                            { "includeRaiReason", Google.Protobuf.WellKnownTypes.Value.ForBool(true) },
                            { "safetyFilterLevel", Google.Protobuf.WellKnownTypes.Value.ForString(_safetyFilterLevel) },
                            { "safetySetting", Google.Protobuf.WellKnownTypes.Value.ForString("block_only_high") },
                            { "personGeneration", Google.Protobuf.WellKnownTypes.Value.ForString("ALLOW_ALL") },
                            { "addWatermark", Google.Protobuf.WellKnownTypes.Value.ForBool(false) }
                        }
                    }
                };

                var instances = new List<Google.Protobuf.WellKnownTypes.Value> { instance };

                // Imagen 4 does not use a separate 'config' field in parameters.
                // Parameters are directly specified in the instance.
                var parameters = new Google.Protobuf.WellKnownTypes.Value(); // No parameters needed for now.

                var endpoint = EndpointName.FromProjectLocationPublisherModel(_projectId, _location, "google", "imagen-4.0-generate-001");
                
                var response = await _predictionServiceClient.PredictAsync(endpoint, instances, parameters);
                
                var base64Images = new List<CreatedBase64Image>();
                string commonMimeType = "image/png"; // Default or first detected mime type

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
                                    var newPrompt = predictionFields["prompt"].StringValue;
                                    var currentMimeType = predictionFields["mimeType"].StringValue;

                                    if (!string.IsNullOrEmpty(imageData))
                                    {
                                        var bd = new CreatedBase64Image
                                        {
                                            bytesBase64 = imageData,
                                            newPrompt = newPrompt,
                                        };

                                        base64Images.Add(bd);
                                        if (!string.IsNullOrEmpty(currentMimeType) && (commonMimeType == "image/png"))
                                        {
                                            commonMimeType = currentMimeType; // Use the first valid mime type found if default
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
                    ErrorMessage = errorMessage, 
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
}
