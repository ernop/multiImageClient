using RecraftAPIClient;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class RecraftService : IImageGenerationService
    {
        private SemaphoreSlim _recraftSemaphore;
        private RecraftClient _recraftClient;
        private HttpClient _httpClient;

        public RecraftService(string apiKey, int maxConcurrency)
        {
            _recraftClient = new RecraftClient(apiKey);
            _recraftSemaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient();
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails, MultiClientRunStats stats)
        {
            await _recraftSemaphore.WaitAsync();
            try
            {
                var recraftDetails = promptDetails.RecraftDetails;
                stats.RecraftImageGenerationRequestCount++;
                var usingPrompt = promptDetails.Prompt;
                if (usingPrompt.Length > 1000)
                {
                    usingPrompt = usingPrompt.Substring(0, 990);
                    Logger.Log("Truncating the prompt for Recraft.");
                }
                var generationResult = await _recraftClient.GenerateImageAsync(usingPrompt, recraftDetails);
                Logger.Log($"\tFrom Recraft: {promptDetails.Show()} '{generationResult.Created}'");
                stats.RecraftImageGenerationSuccessCount++;
                var theUrl = generationResult.Data[0].Url;

                var headResponse = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, theUrl));
                var contentType = headResponse.Content.Headers.ContentType?.MediaType;

                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Url = theUrl,
                    ContentType = contentType,
                    PromptDetails = promptDetails,
                    ImageGenerator = ImageGeneratorApiType.Recraft
                };
            }
            catch (Exception ex)
            {
                Logger.Log($"Recraft error: {ex.Message}");
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, ImageGenerator = ImageGeneratorApiType.Recraft };
            }
            finally
            {
                _recraftSemaphore.Release();
            }
        }
    }
}
