using CommandLine;
using System;
using System.Threading.Tasks;

namespace IdeogramAPIClient
{
    public class Program
    {
        static async Task<int> Main(string[] args)
        {
            try
            {
                return await Parser.Default.ParseArguments<CommandLineOptions>(args)
                    .MapResult(
                        async (CommandLineOptions opts) => await RunAsync(opts),
                        _ => Task.FromResult(1) // Return 1 for parsing errors
                    );
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unhandled exception: {ex.Message}");
                return 2; // Return 2 for unhandled exceptions
            }
        }

        private static async Task<int> RunAsync(CommandLineOptions opts)
        {
            var client = new IdeogramClient(opts.ApiKey);
            var ideogramDetails = new IdeogramDetails
            {
                AspectRatio = opts.AspectRatio,
                Model = opts.Model,
                MagicPromptOption = opts.MagicPromptOption,
                StyleType = opts.StyleType,
                NegativePrompt = opts.NegativePrompt
            };
            var req = new IdeogramGenerateRequest(opts.Prompt, ideogramDetails);
            
            try
            {
                var response = await client.GenerateImageAsync(req);
                if (response.Data != null && response.Data.Count > 0)
                {
                    Console.WriteLine($"Image URL: {response.Data[0].Url}");
                    Console.WriteLine($"Revised Prompt: {response.Data[0].Prompt}");
                    return 0; // Success
                }
                else
                {
                    Console.WriteLine("No images generated.");
                    return 3; // No images generated
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 4; // API or network error
            }
        }
    }

    public class CommandLineOptions
    {
        [Option('k', "api-key", Required = true, HelpText = "API key for Ideogram.")]
        public string ApiKey { get; set; }

        [Option('p', "prompt", Required = true, HelpText = "Prompt for image generation.")]
        public string Prompt { get; set; }

        [Option('a', "aspect-ratio", Default = IdeogramAspectRatio.ASPECT_1_1, HelpText = "Aspect ratio of the generated image.")]
        public IdeogramAspectRatio AspectRatio { get; set; }

        [Option('m', "model", Default = IdeogramModel.V_2, HelpText = "Model to use for image generation.")]
        public IdeogramModel Model { get; set; }

        [Option("magic-prompt", Default = IdeogramMagicPromptOption.ON, HelpText = "Magic prompt option.")]
        public IdeogramMagicPromptOption MagicPromptOption { get; set; }

        [Option('s', "style", Default = IdeogramStyleType.GENERAL, HelpText = "Style type for the generated image.")]
        public IdeogramStyleType StyleType { get; set; }

        [Option('n', "negative-prompt", Default = null, HelpText = "Negative prompt for image generation.")]
        public string NegativePrompt { get; set; }
    }
}