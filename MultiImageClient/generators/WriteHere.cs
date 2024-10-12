using CommandLine;

using Microsoft.VisualBasic;

using MultiClientRunner;

using Newtonsoft.Json.Linq;

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Xml.Linq;

using static System.Formats.Asn1.AsnWriter;

namespace MultiClientRunner
{
    /// If you have a file of a bunch of prompts, you can use this to load them rather than using some kind of custom iteration system.
    public class WriteHere : AbstractPromptGenerator
    {

        public WriteHere(Settings settings) : base(settings)
        {
        }
        public override string Name => nameof(WriteHere);

        public override int ImageCreationLimit => 350;
        public override int CopiesPer => 4;
        public override bool RandomizeOrder => true;
        public override string Prefix => "";
        public override IEnumerable<string> Variants => new List<string> { "" };
        public override string Suffix => "";
        public override Func<string, string> CleanPrompt => (arg) => arg.Trim().Trim('"').Trim('\'').Trim();
        public override bool AlsoDoVersionSkippingClaude => true;
        public override bool UseIdeogram => false;
        public override bool UseClaude => true;

        private IEnumerable<PromptDetails> GetPrompts()
        {
            var prompts = new List<string>() {
                "A completely normal light switch for a 1990s american north eastern area wall, in the 'on' position",
                "a hybrid cat-turtle creature enjoying sunning herself on a log.",
                "A modern building with a reflective glass facade. The building's reflection captures another tower, possibly a skyscraper. The image has text overlay that reads 'How Buildings Learn' written in white font against a dark background. The text 'written and presented by Stewart Brand' is also visible at the bottom.",
                "Close-up of a super clear and sharp photo of a perfectly preserved tablet covered with many ancient, alien styles of kanji characters created with the finest jewels, obsidian, intensely emotional and creative, in a semi-grid, clean and pure incredible macro photograph.",
                "The streets of Singapore are bustling with people and various street food stalls, creating a lively and multicultural atmosphere. The area is full of guava, cheese, broccoli, kimchi, hardboiled eggs, vinegar, and durian. Each smell seems to waft through the crowd attacking people and animals who are particularly weak to it, striking them with brutal overwhelming negative sensations. This is a biological weapon attack! the image is a schema overhead view with detailed performance analysis\r\nA light switch that is clearly in the \"ON\" not \"OFF\" position. Please describe this close-up 3d depth image in high detail exactly describing the light switch panel and its material and color, the switch which sticks out, its position and what the position represents.",
                "A 4x4 grid of super magiscule, block font dense intense incredibly meaningful, profound, ethereal, and subtle KANJI characters. The characters are illustrated in super high resolution, partially 3d style, feeling like they almost emerge from the flat screen, in a super clear image utilizing one or more of: subtle coloration, unusual line thicknesses or variations, unusual kerning, pen and ink, hand-drawn custom artisinal creative characters, and/or super evocative, personalized textures.",
                "Introducing the \"Rogue\" card, an exciting new addition between the Queen and Jack of Diamonds in a standard playing card deck. The Rogue card features a dashing figure with a masked face, holding a small, curved dagger. The figure is adorned with a cape and a diamond-encrusted hat, symbolizing the connection to the Diamonds suit. The single-letter symbol for the Rogue card is \"X\", which is displayed in each corner along with the suit of Diamonds. The card's design maintains the simplicity of traditional playing cards, with the \"X\" and diamonds in the corners and a captivating, yet minimalist, pattern in the middle, featuring a subtle diamond-like motif. The Rogue card captures the essence of cunning and deception, adding an exciting twist to the classic deck.\r\n\U0001f955 attacking 🐢 with \U0001f98a",
"An immense tower of magical prortions, ancient, surrounded by a barren environment with just a hint of change and evolution, incredibly deep depth of field, epic ancient",

            };
            foreach (var prompt in prompts)
            {
                var pd = new PromptDetails();
                pd.ReplacePrompt(prompt, "initial prompt", prompt);
                pd.OriginalPromptIdea = prompt;
                yield return pd;
            }
        }
        public override IEnumerable<PromptDetails> Prompts => GetPrompts();
    }
}

