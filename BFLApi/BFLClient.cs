using CommandLine;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace BFLAPIClient
{
    public class BFLClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string BaseUrl = "https://api.bfl.ml";
        public int DefaultPollingIntervalMs { get; set; } = 2000;

        public BFLClient(string apiKey, int defaultPollingIntervalMs = 2000)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("x-key", _apiKey);
            DefaultPollingIntervalMs = defaultPollingIntervalMs;
        }

        public async Task<GenerationResponse> GetResultAsync(string id)
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/v1/get_result?id={id}");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<GenerationResponse>(content);
        }

        private async Task<GenerationResponse> GenerateAndWaitForResultAsync<TRequest>(string endpoint, TRequest request)
        {
            var generationResponse = await GenerateAsync(endpoint, request);
            var id = generationResponse.Id;

            generationResponse = await GetResultAsync(id);

            while (generationResponse.Status == "Pending")
            {
                await Task.Delay(DefaultPollingIntervalMs);
                generationResponse = await GetResultAsync(id);
            }

            return generationResponse;
        }

        /// private since it's only called by the more convenient e.g. GenerateFluxPro11Async methods
        private async Task<GenerationResponse> GenerateAsync<TRequest>(string endpoint, TRequest request)
        {
            var serialized = JsonConvert.SerializeObject(request);
            var content = new StringContent(serialized, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{BaseUrl}/v1/{endpoint}", content);
            
            if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"422 Unprocessable Entity: {errorContent}", null, System.Net.HttpStatusCode.UnprocessableEntity);
            }
            if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"402 Payment Required: {errorContent}", null, System.Net.HttpStatusCode.PaymentRequired);
            }

            response.EnsureSuccessStatusCode();
            var responseContent = await response.Content.ReadAsStringAsync();
            var response2 = JsonConvert.DeserializeObject<GenerationResponse>(responseContent);
            return response2;
        }

        public Task<GenerationResponse> GenerateFluxPro11Async(FluxPro11Request request)
        {
            return GenerateAndWaitForResultAsync("flux-pro-1.1", request);
        }

        public Task<GenerationResponse> GenerateFluxProAsync(FluxProRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-pro", request);
        }

        public Task<GenerationResponse> GenerateFluxDevAsync(FluxDevRequest request)
        {
            return GenerateAndWaitForResultAsync("flux-dev", request);
        }

        [STAThread]
        public static async Task Main(string[] args)
        {
            await Parser.Default.ParseArguments<CommandLineOptions>(args)
                .WithParsedAsync(RunAsync);
        }

        private static async Task RunAsync(CommandLineOptions opts)
        {
            var client = new BFLClient(opts.ApiKey, opts.PollingInterval);

            GenerationResponse response;
            switch (opts.Model)
            {
                case "flux-pro-1.1":
                    var request11 = new FluxPro11Request
                    {
                        Prompt = opts.Prompt,
                        Width = opts.Width,
                        Height = opts.Height,
                        PromptUpsampling = opts.PromptUpsampling,
                        SafetyTolerance = opts.SafetyTolerance,
                        Seed = opts.Seed
                    };
                    response = await client.GenerateFluxPro11Async(request11);
                    break;
                case "flux-pro":
                    var requestPro = new FluxProRequest
                    {
                        Prompt = opts.Prompt,
                        Width = opts.Width,
                        Height = opts.Height,
                        NumSteps = opts.NumSteps,
                        PromptUpsampling = opts.PromptUpsampling,
                        Seed = opts.Seed,
                        Guidance = opts.Guidance,
                        Interval = opts.Interval,
                        SafetyTolerance = opts.SafetyTolerance
                    };
                    response = await client.GenerateFluxProAsync(requestPro);
                    break;
                case "flux-dev":
                    var requestDev = new FluxDevRequest
                    {
                        Prompt = opts.Prompt,
                        Width = opts.Width,
                        Height = opts.Height,
                        NumSteps = opts.NumSteps,
                        PromptUpsampling = opts.PromptUpsampling,
                        Seed = opts.Seed,
                        Guidance = opts.Guidance,
                        SafetyTolerance = opts.SafetyTolerance
                    };
                    response = await client.GenerateFluxDevAsync(requestDev);
                    break;
                default:
                    Console.WriteLine($"Invalid model: {opts.Model}");
                    return;
            }

            Console.WriteLine($"Status: {response.Status}");
            Console.WriteLine($"Image URL: {response.Result?.Sample}");
            Console.WriteLine($"Revised Prompt: {response.Result?.Prompt}");
        }
    }

    public class CommandLineOptions
    {
        [Option('k', "api-key", Required = true, HelpText = "API key for BFL.")]
        public string ApiKey { get; set; }

        [Option('m', "model", Required = true, HelpText = "Model to use (flux-pro-1.1, flux-pro, or flux-dev).")]
        public string Model { get; set; }

        [Option('p', "prompt", Required = true, HelpText = "Prompt for image generation.")]
        public string Prompt { get; set; }

        [Option('w', "width", Default = 1024, HelpText = "Width of the generated image.")]
        public int Width { get; set; }

        [Option('h', "height", Default = 1024, HelpText = "Height of the generated image.")]
        public int Height { get; set; }

        [Option("num-steps", HelpText = "Number of steps (for flux-pro and flux-dev).")]
        public int? NumSteps { get; set; }

        [Option("prompt-upsampling", Default = false, HelpText = "Enable prompt upsampling.")]
        public bool PromptUpsampling { get; set; }

        [Option('s', "seed", HelpText = "Seed for image generation.")]
        public int? Seed { get; set; }

        [Option('g', "guidance", HelpText = "Guidance value (for flux-pro and flux-dev).")]
        public float? Guidance { get; set; }

        [Option('i', "interval", HelpText = "Interval value (for flux-pro).")]
        public float? Interval { get; set; }

        [Option("safety-tolerance", Default = 6, HelpText = "Safety tolerance level.")]
        public int SafetyTolerance { get; set; }

        [Option("polling-interval", Default = 2000, HelpText = "Polling interval in milliseconds.")]
        public int PollingInterval { get; set; }
    }

    public class GenerationResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }
        
        [JsonProperty("result")]
        public GenerationResult Result { get; set; }
    }

    public class GenerationResult
    {
        /// The url pointing to the image
        [JsonProperty("sample")]
        public string Sample { get; set; }

        /// The revised prompt (?)
        [JsonProperty("prompt")]
        public string Prompt { get; set; }
    }
}