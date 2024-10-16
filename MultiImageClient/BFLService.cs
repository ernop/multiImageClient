﻿using BFLAPIClient;

using IdeogramAPIClient;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MultiClientRunner
{
    public class BFLService : IImageGenerationService
    {
        private SemaphoreSlim _bflSemaphore;
        private BFLClient _bflClient;

        public BFLService(string apiKey, int maxConcurrency)
        {
            _bflClient = new BFLClient(apiKey);
            _bflSemaphore = new SemaphoreSlim(maxConcurrency);
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails, MultiClientRunStats stats)
        {
            await _bflSemaphore.WaitAsync();
            try
            {
                var bflDetails = promptDetails.BFLDetails;
                var request = new FluxPro11Request
                {
                    Prompt = promptDetails.Prompt,
                    Width = bflDetails.Width,
                    Height = bflDetails.Height,
                    PromptUpsampling = bflDetails.PromptUpsampling,
                    SafetyTolerance = bflDetails.SafetyTolerance,
                    Seed = bflDetails.Seed
                };

                stats.BFLImageGenerationRequestcount++;

                Console.WriteLine($"\tTo BFL: {request.Prompt}");
                var generationResult = await _bflClient.GenerateFluxPro11Async(request);
                Console.WriteLine($"\tFrom BFL: '{generationResult.Status}'");
                if (generationResult.Status != "Ready")
                {
                    Console.WriteLine($"Non-ready status. {generationResult.Status} for {request.Prompt}");
                    return new TaskProcessResult { IsSuccess = false, ErrorMessage = generationResult.Status, PromptDetails = promptDetails, Generator = GeneratorApiType.BFL };
                }
                else
                {
                    Console.WriteLine($"BFL image generated: {generationResult.Result.Sample}");
                    var returnedPrompt = generationResult.Result.Prompt;
                    if (returnedPrompt != promptDetails.Prompt)
                    {
                        //BFL replaced the prompt.
                        promptDetails.ReplacePrompt(returnedPrompt, "BFL rewrite", returnedPrompt);
                    }
                        return new TaskProcessResult { IsSuccess = true, Url = generationResult.Result.Sample, PromptDetails = promptDetails, Generator= GeneratorApiType.BFL };
                }
                
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BFL error: {ex.Message}");
                return new TaskProcessResult { IsSuccess = false, ErrorMessage = ex.Message, PromptDetails = promptDetails, Generator = GeneratorApiType.BFL };
            }
            finally
            {
                _bflSemaphore.Release();
            }
        }
    }
}