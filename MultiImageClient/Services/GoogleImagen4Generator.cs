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
        private bool _addWatermark;
        private string _location;
        private string _projectId;
        private string _googleServiceAccountKeyPath;
        private GoogleCredential _credential;

        public GoogleImagen4Generator(string apiKey, int maxConcurrency,
            MultiClientRunStats stats, string name = "", string aspectRatio = "SQUARE", 
            string safetyFilterLevel = "BLOCK_NONE", bool addWatermark = false,
            string location = "us-central1", string projectId = "google-cloud-project-id",
            string googleServiceAccountKeyPath = null)
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
                // Fallback to Application Default Credentials if path is not provided
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
            _addWatermark = addWatermark;
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
            return new List<string> { "google", "imagen4", namePart };
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
                            { "safetyFilterLevel", Google.Protobuf.WellKnownTypes.Value.ForString(_safetyFilterLevel) },
                            { "personGeneration", Google.Protobuf.WellKnownTypes.Value.ForString("ALLOW_ADULT") },
                            { "addWatermark", Google.Protobuf.WellKnownTypes.Value.ForBool(_addWatermark) }
                        }
                    }
                };

                var instances = new List<Google.Protobuf.WellKnownTypes.Value> { instance };

                // Imagen 4 does not use a separate 'config' field in parameters.
                // Parameters are directly specified in the instance.
                var parameters = new Google.Protobuf.WellKnownTypes.Value(); // No parameters needed for now.

                var endpoint = EndpointName.FromProjectLocationPublisherModel(_projectId, _location, "google", "imagen-4.0-generate-001");
                
                var response = await _predictionServiceClient.PredictAsync(endpoint, instances, parameters);
                //var ff = response.Predictions.First().StructValue;
                var generatedImageStruct = response.Predictions.First().StructValue.Fields["generatedImage"].StructValue;
                var responseJson = generatedImageStruct.Fields["bytesBase64Encoded"].StringValue;

                if (string.IsNullOrEmpty(responseJson))
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

                var base64Images = new List<string> { responseJson };

                return new TaskProcessResult 
                { 
                    IsSuccess = true, 
                    Base64ImageDatas = base64Images,
                    ContentType = "image/png",
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
