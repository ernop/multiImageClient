//using GenerativeAI.Types.RagEngine;

using IdeogramAPIClient;

//using OpenAI.Images;

//using RecraftAPIClient;

using System;
//using System.Collections.Generic;
//using System.Diagnostics.Metrics;
//using System.Drawing.Printing;
//using System.Linq;
//using System.Reflection.Metadata.Ecma335;
//using System.Runtime;
//using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace MultiImageClient
{

    public class Program
    {
        private static ClaudeService _ClaudeService { get; set; }
        static async Task Main(string[] args)
        {
            var settingsFilePath = "settings.json";
            var settings = Settings.LoadFromFile(settingsFilePath);
            var concurrency = 1;
            var stats = new MultiClientRunStats();


            var ideogramClient = new IdeogramClient(settings.IdeogramApiKey);
            var promptSource = new ReadAllPromptsFromFile(settings, "");


            //var claudeStep = new ClaudeRewriteStep("Please take the following topic and make it specific; cast the die, take a chance, and expand it to a longer, detailed, specific description of a scene with all the elements of it described. Describe how the thing looks, feels, appears, etc in high detail. Put the most important aspects first such as the overall description, then continue by expanding that and adding more detail, structure, theme. Be specific in whatevr you do. If it seems appropriate, if a man appears don't just say 'the man', but instead actually give him a name, traits, personality, etc. The goal is to deeply expand the world envisioned by the original topic creator. Overall, follow the implied theem and goals of the creator, but just expand it into much more specifics and concreate actualization. Never use phrases or words like 'diverse', 'vibrant' etc. Be very concrete and precise in your descriptions, similar to how ansel adams describing a new treasured species of bird would - detailed, caring, dense, clear, sharp, speculative and never wordy or fluffy. every single word you say must be relevant to the goal of increasing the info you share about this image or sitaution or scene. Be direct and clear.", "", claudeService, 0.4m, stats);



            var descSteps = "generate a helpful set of instructions for the describer telling them what I need: I need what the image is like, its format (photo, digital art, painting etc), the colors, lighting, textures of everything. Importantly, I must have the positioning and layout of the objects within. I must have all details about text, font, style, if any is present. I need full details for a complete reproduction of the image. AND I need emotions if any. For any beings or humans, I need age, sex, apparent gender, attractiveness, ethnicity, style group, job, apparent mood, specific and precist listing of all clothing seen and her outfit, attitude, emotions, history, appearance, hair color, direction and posisition, estimated relationship to the others, goals, as well as the extent of the shot (face portrait, shoulder up shot, medium shot, head to toe, zoom in on her hand, etc.) etc. Do NOT skimp on this part.  When you reply, directly start the description and output no newlines, just the information. You do not need to use full sentences unless you feel it would be helpful; include everything starting generally and with the overall image info and layout, then proceeding to deeper and deeper fine details. also include the overall emotional effect and feeling of the image";


            /// -----------------------  APPLYING PROMPTS TO SERVICES ------------------------

            while (true)
            {
                Console.WriteLine($"What do you want to do: \n\n1. Batch Workflow (make a bunch images for each prompt you choose or write yourself)\r\n2. Image2desc2image take an image, then describe it, then batch that out into a bunch of images again.");
                var val = Console.ReadLine().Trim();
                if (val == "1")
                {
                    var bw = new BatchWorkflow();
                    await bw.RunAsync(promptSource, settings, concurrency, stats);
                    break;
                }
                else if (val == "2")
                {
                    var rw = new RoundTripWorkflow();
                    await rw.RunAsync(settings, concurrency, stats);
                    break;
                }
                else
                {
                    Console.WriteLine("not recognized.)");
                }
            }
        }
    }
}
