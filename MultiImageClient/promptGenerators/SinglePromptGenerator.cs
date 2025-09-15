using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;


namespace MultiImageClient
{
    /// If you have a file of a bunch of prompts, you can use this to load them rather than using some kind of custom iteration system.
    public class SinglePromptGenerator : AbstractPromptGenerator
    {
        private int _copiesPer;
        private int _fullyResolvedCopiesPer;
        private int _imageCreationLimit;
        private IList<string> _prompts;
        public SinglePromptGenerator(IList<string> prompts, int copiesPer, int fullyResolvedCopiesPer, int imageCreationLimit, Settings settings) : base(settings)
        {
            _copiesPer = copiesPer;
            _fullyResolvedCopiesPer = fullyResolvedCopiesPer;
            _imageCreationLimit = imageCreationLimit;
            _prompts = prompts;
        }
        public override string Name => nameof(SinglePromptGenerator);
        public override int ImageCreationLimit => _imageCreationLimit;
        public override int CopiesPer => _copiesPer;
        public override int FullyResolvedCopiesPer => _fullyResolvedCopiesPer;
        public override bool RandomizeOrder => false;
        public override string Prefix => "";
        public override IEnumerable<string> Variants => new List<string> { "" };
        public override string Suffix => "";
        public override bool SaveFinalPrompt => true;
        public override bool SaveInitialIdea => true;
        public override bool SaveFullAnnotation => true;
        public override IEnumerable<PromptDetails> Prompts
        {
            get
            {
                foreach (var prompt in _prompts)
                {   
                    var details = new PromptDetails();

                    details.ReplacePrompt(prompt, prompt, TransformationType.InitialPrompt);
                    yield return details;
                }
            }
        }
    }
}

