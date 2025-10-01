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
        static async Task Main(string[] args)
        {
            var settingsFilePath = "settings.json";
            var settings = Settings.LoadFromFile(settingsFilePath);
            var concurrency = 1;
            var stats = new MultiClientRunStats();
            var promptSource = new ReadAllPromptsFromFile(settings, "");

            


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
