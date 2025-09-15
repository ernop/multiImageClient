using RecraftAPIClient;

using SixLabors.ImageSharp;

using System;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class RecraftGenerator : IImageGenerator
    {
        private SemaphoreSlim _recraftSemaphore;
        private RecraftClient _recraftClient;
        private HttpClient _httpClient;
        private MultiClientRunStats _stats;
        private static Random _Random = new Random();

        public RecraftGenerator(string apiKey, int maxConcurrency, MultiClientRunStats stats)
        {
            _recraftClient = new RecraftClient(apiKey);
            _recraftSemaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient();
            _stats = stats;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var res = $"";
            return res;
            //components.Add(result.PromptDetails.RecraftDetails.style.ToString());
            //components.Add(result.PromptDetails.RecraftDetails.substyle.ToString());
        }

        public Bitmap GetLabelBitmap(int width)
        {
            throw new NotImplementedException();
        }

        public async Task<TaskProcessResult> ProcessPromptAsync( PromptDetails promptDetails)
        {
            await _recraftSemaphore.WaitAsync();
            try
            {
                _stats.RecraftImageGenerationRequestCount++;
                var usingPrompt = promptDetails.Prompt;
                if (usingPrompt.Length > 1000)
                {
                    usingPrompt = usingPrompt.Substring(0, 990);
                    Logger.Log("Truncating the prompt for Recraft.");
                }
                var recraftDetails = GetRandomRecraftStyleAndSubstyleDetails();

                var generationResult = await _recraftClient.GenerateImageAsync(usingPrompt, recraftDetails);
                Logger.Log($"\tFrom Recraft: {promptDetails.Show()} '{generationResult.Created}'");
                _stats.RecraftImageGenerationSuccessCount++;
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

        public static RecraftDetails GetRandomRecraftStyleAndSubstyleDetails()
        {
            var details = new RecraftDetails
            {
                size = RecraftImageSize._1536x1024
            };

            // Randomly select one of the three main styles
            var styles = Enum.GetValues(typeof(RecraftStyle)).Cast<RecraftStyle>().ToList();
            details.style = styles[_Random.Next(styles.Count)].ToString();

            // Based on chosen style, select appropriate substyle

            bool coinFlip = true;

            switch (details.style)
            {
                case "digital_illustration":
                    var digitalStyles = Enum.GetValues(typeof(RecraftDigitalIllustrationSubstyles))
                        .Cast<RecraftDigitalIllustrationSubstyles>().ToList();
                    if (coinFlip)
                    {
                        details.substyle = digitalStyles[_Random.Next(digitalStyles.Count)].ToString();
                    }
                    break;

                case "realistic_image":
                    var realisticStyles = Enum.GetValues(typeof(RecraftRealisticImageSubstyles))
                        .Cast<RecraftRealisticImageSubstyles>().ToList();
                    if (coinFlip)
                    {
                        details.substyle = realisticStyles[_Random.Next(realisticStyles.Count)].ToString();
                    }
                    break;

                case "vector_illustration":
                    var vectorStyles = Enum.GetValues(typeof(RecraftVectorIllustrationSubstyles))
                        .Cast<RecraftVectorIllustrationSubstyles>().ToList();
                    if (coinFlip)
                    {
                        details.substyle = vectorStyles[_Random.Next(vectorStyles.Count)].ToString();
                    }
                    break;
            }

            return details;
        }
        public string GetFullStyleName(string style, string substyle)
        {
            switch (style)
            {
                case "digital_illustration":
                    return $"{nameof(RecraftStyle.digital_illustration)} - {substyle}";
                case "realistic_image":
                    return $"{nameof(RecraftStyle.realistic_image)} - {substyle}";
                case "vector_illustration":
                    return $"{nameof(RecraftStyle.vector_illustration)} - {substyle}";
                case "any":
                    return "Any Style";
                default:
                    return "Unknown";
            }
        }
    }
}
