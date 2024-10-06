using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;
using System.IO;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using Anthropic.SDK;

using System.Threading;
using IdeogramAPIClient;

namespace MultiClientRunner
{
    public class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Initializing MultiClientRunner...");
            var settingsFilePath = "settings.json";
            var settings = Settings.LoadFromFile(settingsFilePath);
            
            // Print settings
            Console.WriteLine("Current settings:");
            Console.WriteLine($"Image Download Base Folder: {settings.ImageDownloadBaseFolder}");
            Console.WriteLine($"Save Raw Image: {settings.SaveRawImage}");
            Console.WriteLine($"Save Annotated Image: {settings.SaveAnnotatedImage}");
            Console.WriteLine($"Save JSON Log: {settings.SaveJsonLog}");
            Console.WriteLine($"Enable Logging: {settings.EnableLogging}");
            Console.WriteLine($"Annotation Side: {settings.AnnotationSide}");
            Console.WriteLine($"Load Prompts From: {settings.LoadPromptsFrom}");
            // Add any other relevant settings here, but exclude API keys

            var ideogramClient = new IdeogramClient(settings.IdeogramApiKey);
            var anthropicApikeyAuth = new APIAuthentication(settings.AnthropicApiKey);
            var anthropicClient = new AnthropicClient(anthropicApikeyAuth);

            settings.Validate();
            var basePromptGenerator = new DeckOfCards(settings);

            var semaphore = new SemaphoreSlim(5);

            Console.WriteLine($"Starting prompt generation. Base generator: {basePromptGenerator.GetType().Name}");
            var tasks = new List<Task<IdeogramProcessResult>>();

            var claudeService = new ClaudeService(anthropicClient);

            var stats = new MultiClientRunStats();

            foreach (var promptDetails in basePromptGenerator.Run())
            {
                Console.WriteLine($"\n\n * Processing prompt: {promptDetails.Prompt}...");
                string claudeResponse;
                try
                {
                    claudeResponse = await claudeService.RewritePromptAsync(promptDetails.Prompt,stats);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An error occurred while having Claude rewrite the prompt. {ex.Message}");
                    continue;
                }

                Console.WriteLine($"\tClaude =>. {claudeResponse}");
                var step = new ImageConstructionStep("claude's rewritten version", claudeResponse);
                promptDetails.ImageConstructionSteps.Add(step);

                var ideogramDetails = new IdeogramDetails
                {
                    AspectRatio = IdeogramAspectRatio.ASPECT_2_3,
                    Model = IdeogramModel.V_2,
                    MagicPromptOption = IdeogramMagicPromptOption.OFF,
                    StyleType = IdeogramStyleType.GENERAL
                };
                promptDetails.IdeogramDetails = ideogramDetails;
                
                var request = new IdeogramGenerateRequest(claudeResponse, ideogramDetails);
                stats.IdeogramRequestCount++;
                tasks.Add(ProcessIdeogramPromptAsync(ideogramClient, promptDetails, request, semaphore, settings, stats));

                // Print metrics after each loop
                stats.PrintStats();
            }

            Console.WriteLine($"All tasks queued. Awaiting completion of {tasks.Count} tasks.");
            var results = await Task.WhenAll(tasks);

            int successCount = 0;
            int failureCount = 0;

            foreach (var result in results)
            {
                if (result.IsSuccess)
                {
                    successCount++;
                }
                else
                {
                    failureCount++;
                    Console.WriteLine($"Failed to generate image. Error: {result.ErrorMessage}");
                }
            }

            Console.WriteLine($"Processing complete. Successes: {successCount}, Failures: {failureCount}");
            Console.WriteLine("Final stats:");
            stats.PrintStats();
        }

        private static async Task<IdeogramProcessResult> ProcessIdeogramPromptAsync(IdeogramClient client, PromptDetails promptDetails, IdeogramGenerateRequest request, SemaphoreSlim semaphore, Settings settings, MultiClientRunStats stats)
        {
            await semaphore.WaitAsync();
            try
            {
                GenerateResponse response = await client.GenerateImageAsync(request);
                if (response?.Data?.Count > 0)
                {
                    Console.WriteLine($"\tIdeogram generation successful. Images: {response.Data.Count}");
                    await ImageAnnotation.SaveGeneratedImagesAsync(response, promptDetails, request, settings, stats);
                    return new IdeogramProcessResult { Response = response, IsSuccess = true };
                }
                else
                {
                    Console.WriteLine("\tIdeogram generation failed. No images returned.");
                    return new IdeogramProcessResult { IsSuccess = false, ErrorMessage = "No images generated" };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\tIdeogram generation error: {ex.GetType().Name}. Message: {ex.Message}");
                return new IdeogramProcessResult { IsSuccess = false, ErrorMessage = ex.Message };
            }
            finally
            {
                semaphore.Release();
            }
        }

    }
}