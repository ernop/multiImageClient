using Newtonsoft.Json;
using System.Text;

namespace RecraftAPIClient
{
    public class RecraftClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string _baseUrl = "https://external.api.recraft.ai/v1";

        public RecraftClient(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }


        public async Task<GenerationResponse> GenerateImageAsync(string prompt, string artistic_level, string substyle, string style, RecraftImageSize size)
        {
            var stringSubstyle = "";
            string serialized = "";
            

            if (style == "any") {
                serialized = JsonConvert.SerializeObject(new
                {
                    prompt,
                    model = "recraftv3",
                    style = style,
                    size = size.ToString().TrimStart('_'),
                    response_format = "url"
                });
            }
            else
            {
                serialized = JsonConvert.SerializeObject(new
                {
                    prompt,
                    model = "recraftv3",
                    artistic_level = artistic_level,
                    style = style,
                    substyle = substyle,
                    size = size.ToString().TrimStart('_'),
                    response_format = "url"
                });
            }



            var content = new StringContent(
                serialized,
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync($"{_baseUrl}/images/generations", content);
            //var response = await _httpClient.PostAsync($"{_baseUrl}", content);
            await EnsureSuccessfulResponse(response);

            var responseContent = await response.Content.ReadAsStringAsync();

            return JsonConvert.DeserializeObject<GenerationResponse>(responseContent);
        }

        public async Task<StyleResponse> CreateStyleAsync(byte[] imageData, RecraftStyle style)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "file", "image.png");
            content.Add(new StringContent(style.ToString().ToLower()), "style");

            var response = await _httpClient.PostAsync($"{_baseUrl}/styles", content);
            await EnsureSuccessfulResponse(response);

            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<StyleResponse>(responseContent);
        }

        public async Task<ImageResponse> VectorizeImageAsync(byte[] imageData, string responseFormat = "url")
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "file", "image.png");
            if (responseFormat != "url")
            {
                content.Add(new StringContent(responseFormat), "response_format");
            }

            var response = await _httpClient.PostAsync($"{_baseUrl}/images/vectorize", content);
            await EnsureSuccessfulResponse(response);

            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<ImageResponse>(responseContent);
        }

        public async Task<ImageResponse> RemoveBackgroundAsync(byte[] imageData, string responseFormat = "url")
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "file", "image.png");
            if (responseFormat != "url")
            {
                content.Add(new StringContent(responseFormat), "response_format");
            }

            var response = await _httpClient.PostAsync($"{_baseUrl}/images/removeBackground", content);
            await EnsureSuccessfulResponse(response);

            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<ImageResponse>(responseContent);
        }

        public async Task<ImageResponse> ClarityUpscaleAsync(byte[] imageData, string responseFormat = "url")
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "file", "image.png");
            if (responseFormat != "url")
            {
                content.Add(new StringContent(responseFormat), "response_format");
            }

            var response = await _httpClient.PostAsync($"{_baseUrl}/images/clarityUpscale", content);
            await EnsureSuccessfulResponse(response);

            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<ImageResponse>(responseContent);
        }

        public async Task<ImageResponse> GenerativeUpscaleAsync(byte[] imageData, string responseFormat = "url")
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "file", "image.png");
            if (responseFormat != "url")
            {
                content.Add(new StringContent(responseFormat), "response_format");
            }

            var response = await _httpClient.PostAsync($"{_baseUrl}/images/generativeUpscale", content);
            await EnsureSuccessfulResponse(response);

            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<ImageResponse>(responseContent);
        }

        private async Task EnsureSuccessfulResponse(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"API request failed: {response.StatusCode} - {error}");
            }
        }
    }

    public class GenerationResponse
    {
        [JsonProperty("data")]
        public List<ImageData> Data { get; set; }

        [JsonProperty("created")]
        public long Created { get; set; }
    }

    public class StyleResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }
    }

    public class ImageResponse
    {
        [JsonProperty("image")]
        public ImageData Image { get; set; }
    }

    public class ImageData
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("b64_json")]
        public string Base64Json { get; set; }
    }
}