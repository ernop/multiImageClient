using System;
using System.Collections.Generic;
using System.IO;

namespace MultiImageClient
{
    public class PromptFileSource : AbstractPromptSource
    {
        private readonly string _path;

        public PromptFileSource(Settings settings, string path) : base(settings)
        {
            _path = path;
        }

        public override string Name => nameof(PromptFileSource);
        public override int ImageCreationLimit => int.MaxValue;
        public override int CopiesPer => 1;
        public override int FullyResolvedCopiesPer => 1;
        public override bool RandomizeOrder => false;
        public override string Prefix => "";
        public override string Suffix => "";

        public override IEnumerable<PromptDetails> Prompts
        {
            get
            {
                var fullPath = Path.GetFullPath(_path);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException($"Prompt file does not exist: {fullPath}", fullPath);
                }

                var yielded = 0;
                foreach (var line in File.ReadLines(fullPath))
                {
                    var prompt = line.Trim();
                    if (string.IsNullOrWhiteSpace(prompt))
                    {
                        continue;
                    }

                    var pd = new PromptDetails();
                    pd.ReplacePrompt(prompt, prompt, TransformationType.InitialPrompt);
                    yielded++;
                    yield return pd;
                }

                if (yielded == 0)
                {
                    throw new InvalidOperationException($"Prompt file contained no non-blank lines: {fullPath}");
                }
            }
        }
    }
}
