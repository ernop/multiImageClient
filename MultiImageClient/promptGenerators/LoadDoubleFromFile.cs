using MultiImageClient;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography.X509Certificates;

namespace MultiImageClient
{
    /// If you have a file of a bunch of prompts, you can use this to load them rather than using some kind of custom iteration system.
    public class LoadDoubleFromFile : AbstractPromptSource
    {
        private string FilePath { get; set; }
        private bool _IncludePublic { get; set; }
        private bool _IncludePrivate { get; set; }
        public LoadDoubleFromFile(Settings settings, string path, bool includePublic, bool includePrivate) : base(settings)
        {
            _IncludePrivate = includePrivate;
            _IncludePublic = includePublic;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                FilePath = path;
            }
            else
            {
                FilePath = "";
            }
        }
        public override string Name => nameof(LoadDoubleFromFile);

        public override int ImageCreationLimit => 400;
        public override int CopiesPer => 2;
        public override int FullyResolvedCopiesPer => 2;
        public override bool RandomizeOrder => true;
        public override string Prefix => "";
        public override string Suffix => "";
        private IEnumerable<PromptDetails> GetPrompts()
        {
            var sourceFPs = new List<string>() { };
            if (_IncludePrivate)
            {
                sourceFPs.Add("D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrivatePrompts.txt");
                //sourceFPs.Add("D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrompts-private.txt");
            }
            if (_IncludePublic)
            {
                //sourceFPs.Add("D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrompts.txt");
                //sourceFPs.Add("D:\\proj\\prompts3.txt");
            }
                
            if (!string.IsNullOrEmpty(FilePath))
            {
                sourceFPs.Add(FilePath);
            }

            var sourcePrompts = new List<string>();
            foreach (var fp in sourceFPs)
            {
                var items = File.ReadAllLines(fp).ToList();
                foreach (var usePrompt in items)
                {
                    if ((usePrompt.Contains("{{") || usePrompt.Contains("[[")) && !(usePrompt.Contains("[[[") || usePrompt.Contains("}}}")))
                    {
                        continue;
                    }
                    if (string.IsNullOrEmpty(usePrompt))
                    {
                        continue;
                    }
                    sourcePrompts.Add(usePrompt);
                }
            }

            Logger.Log($"loaded {sourcePrompts.Count} prompts total.");
            var countToInclude = 4;
            var totalLengthCount = 3000;            

            for (var ii = 0; ii < ImageCreationLimit * 2; ii++)
            {
                var usingCountToInclude=Random.Shared.Next(1, countToInclude);
                var onlyFirstNChars = totalLengthCount / countToInclude;
                var allthem = new List<string>();
                for (var jj = 0; jj < usingCountToInclude; jj++)
                {
                    var randomIndex = Random.Shared.Next(0, sourcePrompts.Count);

                    if (string.IsNullOrEmpty(sourcePrompts[randomIndex]))
                    {
                        continue;
                    }
                    var theText = sourcePrompts[randomIndex];
                    if (theText.Length > onlyFirstNChars)
                    {
                        theText = theText.Substring(0, onlyFirstNChars)+"...";
                    }   
                    allthem.Add(theText);
                }

                var joined = string.Join("\r\n--- ", allthem.OrderBy(el=>el.Length));

                var combined = $"{joined}";

                Logger.Log(combined);
                var pd = new PromptDetails();
                pd.ReplacePrompt(combined, combined, TransformationType.InitialPrompt);

                yield return pd;
            }
        }

        public override IEnumerable<PromptDetails> Prompts => GetPrompts();
    }
}

