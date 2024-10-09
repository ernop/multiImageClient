using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;

using BFLAPIClient;

using IdeogramAPIClient;

using MultiClientRunner;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MultiClientRunner
{
    public static class IdeogramService
    {
        private static SemaphoreSlim _ideogramSemaphore;
        private static IdeogramClient _ideogramClient;

        public static void Initialize(string apiKey, int maxConcurrency)
        {
            _ideogramClient = new IdeogramClient(apiKey);
            _ideogramSemaphore = new SemaphoreSlim(maxConcurrency);
        }

        public static async Task<TaskProcessResult> ProcessIdeogramPromptAsync(PromptDetails promptDetails, MultiClientRunStats stats)
        {
            await _ideogramSemaphore.WaitAsync();
            try
            {
                var ideogramDetails = promptDetails.IdeogramDetails;
                var request = new IdeogramGenerateRequest(promptDetails.Prompt, ideogramDetails);

                await _ideogramSemaphore.WaitAsync();
                try
                {
                    stats.IdeogramRequestCount++;
                    GenerateResponse response = await _ideogramClient.GenerateImageAsync(request);
                    if (response?.Data?.Count == 1)
                    {
                        foreach (var imageObject in response.Data)
                        {
                            //there is only actually one ever.
                            var returnedPrompt = imageObject.Prompt;
                            if (returnedPrompt != promptDetails.Prompt)
                            {
                                //BFL replaced the prompt.
                                promptDetails.ReplacePrompt(returnedPrompt, "Ideogram rewrite", returnedPrompt);
                            }
                            return new TaskProcessResult { Response = response, IsSuccess = true, Url = imageObject.Url, PromptDetails = promptDetails, Generator= GeneratorApiType.Ideogram};
                        }
                        throw new Exception("No images returned");
                    }
                    else if (response?.Data?.Count >=1)
                    {
                        throw new Exception("Multiple images returned? I can't handle this! Who knew!");
                    }
                    else
                    {
                        return new TaskProcessResult { IsSuccess = false, ErrorMessage = "No images generated", PromptDetails = promptDetails, Generator = GeneratorApiType.Ideogram };
                    }
                }
                catch (Exception ex)
                {
                    return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, Generator = GeneratorApiType.Ideogram };
                }
                finally
                {
                    _ideogramSemaphore.Release();
                }
            }
            finally
            {
                _ideogramSemaphore.Release();
            }
        }
    }
}