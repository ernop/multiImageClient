//using GenerativeAI.Types.RagEngine;


//using OpenAI.Images;

//using RecraftAPIClient;

using System;
//using System.Collections.Generic;
//using System.Diagnostics.Metrics;
//using System.Drawing.Printing;
using System.IO;
//using System.Linq;
//using System.Reflection.Metadata.Ecma335;
//using System.Runtime;
//using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace MultiImageClient
{
    public class PromptLogger
    {
        private const string LogFileName = "../../../prompt_log.json";

        public static void LogPrompt(string prompt)
        {
            var logEntry = new
            {
                time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                prompt = prompt
            };

            var jsonLine = JsonSerializer.Serialize(logEntry);

            try
            {
                File.AppendAllText(LogFileName, jsonLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to write to prompt log: {ex.Message}");
            }
        }
    }
}
