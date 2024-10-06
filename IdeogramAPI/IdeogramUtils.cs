using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;


namespace IdeogramAPIClient
{
    public static class IdeogramUtils
    {
        //public static void LogRequestAndResponse(string logFilePath, IdeogramGenerateRequest request, GenerateResponse response, string errorMessage = null, System.Net.HttpStatusCode? statusCode = null)
        //{
        //    var logEntry = new
        //    {
        //        Timestamp = DateTime.UtcNow,
        //        Request = request,
        //        Response = response,
        //        ErrorMessage = errorMessage,
        //        StatusCode = statusCode
        //    };

        //    var json = JsonConvert.SerializeObject(logEntry, Formatting.Indented);
        //    File.AppendAllText(logFilePath, json + Environment.NewLine + Environment.NewLine);
        //}

        public static string StringifyAspectRatio(IdeogramAspectRatio ratio)
        {
            return ratio switch
            {
                IdeogramAspectRatio.ASPECT_10_16 => "10x16",
                IdeogramAspectRatio.ASPECT_16_10 => "16x10",
                IdeogramAspectRatio.ASPECT_9_16 => "9x16",
                IdeogramAspectRatio.ASPECT_16_9 => "16x9",
                IdeogramAspectRatio.ASPECT_3_2 => "3x2",
                IdeogramAspectRatio.ASPECT_2_3 => "2x3",
                IdeogramAspectRatio.ASPECT_4_3 => "4x3",
                IdeogramAspectRatio.ASPECT_3_4 => "3x4",
                IdeogramAspectRatio.ASPECT_1_1 => "1x1",
                IdeogramAspectRatio.ASPECT_1_3 => "1x3",
                IdeogramAspectRatio.ASPECT_3_1 => "3x1",
                _ => throw new ArgumentOutOfRangeException(nameof(ratio), ratio, null),
            };
        }

    }
}