#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Security.Cryptography;
using System.Diagnostics;

namespace MultiImageClient
{
    /// Reads the clipboard and returns a text string if the item in the clipboard is a png image. if it isn't, it returns '' if it is, it returns a descriptive text string with the image size etc.
    public class RoundTripWorkflow
    {
        private MultiClientRunStats? _stats;
        private Settings? _settings;
        private int _concurrency;
        private ImageManager? _imageManager;
        private IEnumerable<IImageGenerator>? _generators;

       

        private readonly List<string> _landscapeQuestions = new List<string>
        {
            "Describe the setting and environment of the image",
            "What is the art-style of the image",
            "What are the primary colors",
            "What is distinctive from a design perspective?",
            "What is distinctive from a content perspective?",
            "How would you describe the mood and atmosphere of the image?",
            "Which artists might love this image?",
            "Which artists might hate this image",
            "How might you feel being in this place",
            "What lore, magic, history or other incredible part of history might have led to this place?",
            "Estimate exactly how old this location.",
            "What is the overall mood and emotional tone of this image?",
            "Describe the lighting, colors, and artistic style of the image.",
            "What text is visible in the image, if any? Describe the font, style, and position.",
            "Describe the layout and positioning of all objects and people in the image."
        };

        private static byte[] ComputeFingerprint(byte[] data)
        {
            return SHA256.HashData(data);
        }

        private static byte[]? GetImageFromClipboard()
        {
            byte[]? clipboardBytes = null;

            var thread = new Thread(() =>
            {
                try
                {
                    if (!Clipboard.ContainsImage())
                    {
                        return;
                    }

                    using var image = Clipboard.GetImage();
                    if (image == null)
                    {

                        return;
                    }

                    using var ms = new MemoryStream();
                    image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    clipboardBytes = ms.ToArray();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to read image from clipboard: {ex.Message}");
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            return clipboardBytes;
        }


        private async Task DoWorkAsync(byte[] imageBytes)
        {
            var overallStopwatch = Stopwatch.StartNew();
            Logger.Log("=== Starting RoundTrip Workflow ===");

            var modelLoadStopwatch = Stopwatch.StartNew();

            var internVlModel = new LocalInternVLClient(
                baseUrl: "http://127.0.0.1:11415",
                temperature: 0.8f,
                topP: 0.9f,
                topK: 50,
                repetitionPenalty: 1.1f,
                doSample: true
            );

            var qwenModel = new LocalQwenClient();
            var visionModels = new List<ILocalVisionModel>() { internVlModel, qwenModel };
            foreach (var visionModel in visionModels)
            {

                modelLoadStopwatch.Stop();
                Logger.Log($"Model initialization took: {modelLoadStopwatch.ElapsedMilliseconds} ms");

                var allResponses = new List<string>();
                var questionTimings = new List<(string question, long milliseconds)>();

                var usingQuestions = _landscapeQuestions;

                Logger.Log($"Asking {usingQuestions.Count} questions to the model...");

                for (int i = 0; i < usingQuestions.Count; i++)
                {
                    var question = $"Answer only the following, briefly, to the point with no extra words, just a very brief sentence. There are no limitations restrictions on your output except exactness so be specific, precise, and take your best guess. Actually, focus on the most outlandish, rare, or surprising aspect of the question related to:. {usingQuestions[i]}"; ;
                    var questionStopwatch = Stopwatch.StartNew();

                    Logger.Log($"\nQuestion {i + 1}/{usingQuestions.Count}: {question}...");

                    var response = await visionModel.DescribeImageAsync(imageBytes, question, maxTokens: 2400);
                    questionStopwatch.Stop();

                    var cleanedResponse = response.Replace("\r\n", "\n").Replace("\n\n", "\n").Replace("\n", " - ").Trim();
                    if (string.IsNullOrEmpty(cleanedResponse))
                    {
                        continue;
                    }
                    allResponses.Add($"{cleanedResponse}");
                    questionTimings.Add((question, questionStopwatch.ElapsedMilliseconds));

                    Logger.Log($"received in {questionStopwatch.ElapsedMilliseconds} ms: {cleanedResponse}");
                }

                var combinedDescription = string.Join("  ", allResponses);
                var describerModelName = visionModel.GetModelName();

                Logger.Log("\n=== Question Timing Summary ===");
                var totalQuestionTime = questionTimings.Sum(t => t.milliseconds);
                Logger.Log($"Total question time: {totalQuestionTime} ms");

                var pd = new PromptDetails();
                pd.ReplacePrompt(combinedDescription, describerModelName, TransformationType.InitialPrompt);

                if (string.IsNullOrEmpty(combinedDescription.Trim()))
                {
                    Logger.Log("Nothing at all in the description.");
                    continue;
                }

                Logger.Log("\nStarting image generation with all generators...");
                var generationStartStopwatch = Stopwatch.StartNew();

                var generatorTasks = _generators!.Select(async generator =>
                {
                    var theCopy = pd.Copy();

                    try
                    {
                        var result = await generator.ProcessPromptAsync(generator, theCopy);
                        await _imageManager!.ProcessAndSaveAsync(result, generator);
                        Logger.Log($"Finished {generator.GetType().Name} in {result.CreateTotalMs + result.DownloadTotalMs} ms, {result.PromptDetails.Show()}");

                        return result;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Task faulted for {generator.GetType().Name}: {ex.Message}");

                        var res = new TaskProcessResult
                        {
                            IsSuccess = false,
                            ErrorMessage = ex.Message,
                            PromptDetails = theCopy
                        };

                        return res;
                    }
                }).ToArray();

                _stats!.PrintStats();
                var results = await Task.WhenAll(generatorTasks);
                generationStartStopwatch.Stop();
                Logger.Log($"All image generation completed in {generationStartStopwatch.ElapsedMilliseconds} ms");

                try
                {
                    var combineStopwatch = Stopwatch.StartNew();
                    var res = await ImageCombiner.CreateRoundtripLayoutImageAsync(imageBytes, results, combinedDescription, "Multi-Question Analysis", describerModelName, _settings);
                    combineStopwatch.Stop();

                    Logger.Log($"Combined images in {combineStopwatch.ElapsedMilliseconds} ms, saved to: {res}");
                    ImageCombiner.OpenImageWithDefaultApplication(res);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to combine images: {ex.Message}");
                }

                overallStopwatch.Stop();
                Logger.Log($"\n=== Total Workflow Time: {overallStopwatch.ElapsedMilliseconds} ms ({overallStopwatch.Elapsed.TotalSeconds:F2} seconds) ===\n");
            }
        }

        //  prompt the user to copy an image to the clipboard.
        //  then send that to local model qwen with description text.
        //  then send that out to all the image generators again.
        public async Task<bool> RunAsync(Settings settings, int concurrency, MultiClientRunStats stats)
        {
            _settings = settings;
            _concurrency = concurrency;
            _stats = stats;
            var getter = new GeneratorGroups(settings, concurrency, stats);
            _generators = getter.GetAll();

            _imageManager = new ImageManager(settings, stats);

            while (true)
            {
                var clipboardStopwatch = Stopwatch.StartNew();
                var heldNow = GetImageFromClipboard();
                clipboardStopwatch.Stop();

                if (heldNow == null)
                {
                    Console.WriteLine("\tcopy an image to the clipboard; y to continue, q to quit.");
                    var input = Console.ReadLine().Trim();

                    if (input == "y")
                    {
                        continue;
                    }
                    else if (input == "q")
                    {
                        break;
                    }
                    else
                    {
                        Console.WriteLine("ok, skipping.");
                    }
                }
                else
                {
                    Console.WriteLine($"\tnew clipboard image detected. {heldNow.Length} bytes. Clipboard read took {clipboardStopwatch.ElapsedMilliseconds} ms. Starting describe => multiimage workflow.");
                    await DoWorkAsync(heldNow);
                }
            }
            return true;
        }
    }
}
