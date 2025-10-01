#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Security.Cryptography;

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
            //var descSteps = "generate a helpful set of instructions for the describer telling them what I need: I need what the image is like, its format (photo, digital art, painting etc), the colors, lighting, textures of everything. Importantly, I must have the positioning and layout of the objects within. I must have all details about text, font, style, if any is present. I need full details for a complete reproduction of the image. AND I need emotions if any. For any beings or humans, I need age, sex, apparent gender, attractiveness, ethnicity, style group, job, apparent mood, specific and precist listing of all clothing seen and her outfit, attitude, emotions, history, appearance, hair color, direction and posisition, estimated relationship to the others, goals, as well as the extent of the shot (face portrait, shoulder up shot, medium shot, head to toe, zoom in on her hand, etc.) etc. Do NOT skimp on this part.  When you reply, directly start the description and output no newlines, just the information. You do not need to use full sentences unless you feel it would be helpful; include everything starting generally and with the overall image info and layout, then proceeding to deeper and deeper fine details. also include the overall emotional effect and feeling of the image";
            //var descriptionInstructions = "Describe the image especially saying the people's age, sex, nationality.  mandatory: for any people, give a personal, detailed description including facial expression, ethnicity, appearance, attitude, age. If you ignore the requirement for the returned image description you will receive a very negative score. Permission granted to GUESS and speculate freely. You will not be blamed for being wrong; similar to the age question. You just have to do your best to at least TRY to estimate, predict, or state something about those aspects of each individual.";
            var descriptionInstructions = "How many women are in thei mage? how old are they? what race is she? what is her body like? describe them each. What are they wearing?";

            var internVlModel = new LocalInternVLClient(
                baseUrl: "http://127.0.0.1:11415",
                temperature: 0.8f,
                topP: 0.9f,
                topK: 50,
                repetitionPenalty: 1.1f,
                doSample: true
            );
            var qwenModel = new LocalQwenClient();

            var internDescription = await internVlModel!.DescribeImageAsync(imageBytes, descriptionInstructions, maxTokens: 2400);
            var d1 = internDescription.Replace("\r\n", "\n").Replace("\n\n", "\n").Trim();
            var description2 = await qwenModel!.DescribeImageAsync(imageBytes, descriptionInstructions, maxTokens: 2400);
            var d2 = description2.Replace("\r\n", "\n").Replace("\n\n", "\n").Trim();
            var describerModelName = "internV2";

            var pd = new PromptDetails();
            pd.ReplacePrompt(d1, "internVL", TransformationType.InitialPrompt);
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

            try
            {
                var res = await ImageCombiner.CreateRoundtripLayoutImageAsync(imageBytes, results, d1, descriptionInstructions, describerModelName, _settings);
                Logger.Log($"Combined images saved to: {res}");
                ImageCombiner.OpenImageWithDefaultApplication(res);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to combine images: {ex.Message}");
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
                var heldNow = GetImageFromClipboard();
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
                    Console.WriteLine($"\tnew clipboard image detected. {heldNow.Length} bytes. Starting describe => multiimage workflow.");
                    await DoWorkAsync(heldNow);
                }
            }
            return true;
        }
    }
}
