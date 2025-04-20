using CommandLine;

using Microsoft.VisualBasic;

using Newtonsoft.Json.Linq;

using RecraftAPIClient;

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

using static System.Formats.Asn1.AsnWriter;

namespace MultiImageClient
{
    /// If you have a file of a bunch of prompts, you can use this to load them rather than using some kind of custom iteration system.
    public class RecraftAllsStyles : AbstractPromptGenerator
    {

        public RecraftAllsStyles(Settings settings) : base(settings)
        {
        }
        public override string Name => nameof(WriteHere);

        public override int ImageCreationLimit => 350;
        public override int CopiesPer => 1;
        public override bool RandomizeOrder => false;
        public override string Prefix => "";
        public override IEnumerable<string> Variants => new List<string> { "" };
        public override string Suffix => "";
        public override bool SaveFullAnnotation => true;
        public override bool SaveInitialIdea => false;
        public override bool SaveFinalPrompt => false;
        public override bool SaveJustOverride => true;

        private IEnumerable<PromptDetails> GetPrompts()
        {
            var res = new List<PromptDetails>();
            foreach (RecraftStyle style in Enum.GetValues(typeof(RecraftStyle)))
            {
                var substyles = style switch
                {
                    RecraftStyle.realistic_image => Enum.GetValues(typeof(RecraftRealisticImageSubstyles)),
                    RecraftStyle.vector_illustration => Enum.GetValues(typeof(RecraftVectorIllustrationSubstyles)),
                    RecraftStyle.digital_illustration => Enum.GetValues(typeof(RecraftDigitalIllustrationSubstyles)),
                    _ => throw new ArgumentException($"Unknown style: {style}")
                };
                
                foreach (object substyle in substyles)
                {
                    var recraftDetails = new RecraftDetails
                    {
                        style = style.ToString(),
                        size = RecraftImageSize._1707x1024,
                    };
                    recraftDetails.substyle = substyle.ToString();
                    var usingSub = substyle.ToString().TrimStart('_');
                    recraftDetails.substyle = usingSub;
                    var pd = new PromptDetails();
                    var prompt = "A magnificent tower in an epic plain, ruins and hidden secrets, super detailed and high resolution, incredibly deep and profound, with hidden creatures and erosion, and a cute semi-hidden kitten.";
                    pd.ReplacePrompt(prompt, prompt , TransformationType.InitialPrompt);
                    
                    pd.IdentifyingConcept = $"{style}\t{usingSub}\tmagnificent tower in an epic plain";
                    
                    pd.RecraftDetails = recraftDetails;
                    Logger.Log($"Trying style, substyle: {style} {usingSub}");
                    res.Add(pd);
                }
            }
            return res;
        }
        
        public override IEnumerable<PromptDetails> Prompts => GetPrompts().OrderBy(el => Random.Shared.Next());
    }
}


