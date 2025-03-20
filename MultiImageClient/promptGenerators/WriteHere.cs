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
        public override Func<string, string> CleanPrompt => (arg) => arg.Trim().Trim();
        public override bool AlsoDoVersionSkippingClaude => true;
        public override bool UseIdeogram => false;
        public override bool UseClaude => true;

        private IEnumerable<PromptDetails> GetPrompts()
        {
            List<string> prompts = new List<string>
{
    "A highly detailed digital painting, warmly lit, showing Charles—an extraordinarily thin young man with messy black hair—inside his cramped studio apartment at night, facing an old, grandfatherly figure in a red-and-white Santa suit who just appeared unexpectedly. Locks on the apartment door and simple, industrial-themed furnishings are visible, emphasizing the surreal intrusion of the old Santa amid the mundane setting.",
    "An intricate illustration of the North Pole’s List Room: a vast, stadium-like interior lined with tens of thousands of polished wooden desks in concentric rings. Enormous scrolls of paper hang from the high domed ceiling, each so thick they coil around metal bars. Scores of pointed-eared elves—dressed in red, green, and gold—hunch over their desks, writing and scrutinizing three-dimensional viewers that float above the angled surfaces, all under a diffuse, magical glow.",
    "A richly detailed image of Kelvin, a head elf with golden epaulettes and a serious expression, guiding Charles through the List Room. They stand at one particular desk, angled like an architect’s drafting table, where a flat, crystal-clear viewer shows a child walking along a city sidewalk. The elves around them wear festive but restrained outfits of red and green, and behind them loom the monumental scrolls and countless workstations.",
    "A large workshop filled with warm lamplight and rows of sturdy wooden benches, each bearing a strange metal iris connected to a glass pipe filled with gray magical ‘dough.’ Elves in festive attire carefully shape the dough into children’s gifts—a small plastic frog in one case—by hand. Tools, ribbons, and bright wrapping papers are scattered about, with half-finished trinkets and toys lending a tactile, handcrafted atmosphere.",
    "A meticulously rendered stable scene, where elegant, strong-limbed reindeer stand in neat rows, their antlers adorned with subtle silver bells. The wooden stable walls are hung with wreaths and holly. At the center, the gleaming red sleigh awaits, its runners reflecting the straw and hay below, while elves tend lovingly to the creatures and ensure everything is perfectly prepared.",
    "A grand set of private chambers designed for Santa: luxurious, slightly gaudy furnishings, plush rugs, and carved wood accents. Among these comforts stands Charles, now wearing the red-and-white suit that still doesn’t quite fit. Beside him is Matilda, a petite elf with long black hair, offering him exquisite notebooks and pens. On a side table is the mechanical viewer, dials and levers gleaming, ready to reveal the Earth from orbit.",
    "Inside the workshop again, this time focusing closely on a single elf holding a shape-shifting ball of gray dough, trying to form an impossible gift that would maximize a child’s happiness. The elf’s brow is furrowed in concentration, the device half-transformed into something ominous. In the background, Charles stands horrified, notebooks in hand, as colorful presents and decorations create a bizarre contrast to his moral dilemma.",
    "A dramatic nighttime rooftop scene in some metropolitan area on Earth. Two indistinguishable copies of Charles—both strong and fluid in motion—confront James, the old Santa now in a human disguise. The air crackles with tension. Below them, city lights and confused pedestrians blur. The style remains painterly and detailed, capturing the swift, inhuman fight through elongated shadows and dynamic poses.",
    "A vast emptiness where the North Pole once stood: the cavernous List Room and workshops are gone, leaving only a blank white expanse with faint echoes of candy canes, wreaths, and gold trim. A subtle shimmer in the air hints that the elves, once so numerous and devout, have departed entirely. The atmosphere is both peaceful and melancholic, shafts of pale light accenting the pristine emptiness.",
    "A serene, intimate nighttime bedroom in rural China. A small girl, Li Xiu Yang, lies asleep in a simple bed. Charles, now appearing gentler and more resolved, stands beside her, pressing a tiny silver marble to her forehead. Outside the window, the moonlight bathes quiet rooftops. The style remains consistent and painterly, every detail—her soft blanket, modest furniture, a stray toy on the floor—rendered lovingly as he bestows this final, hopeful gift."
};
            foreach (var prompt in prompts)
            {
                var pd = new PromptDetails();
                pd.ReplacePrompt(prompt, prompt, TransformationType.InitialPrompt);
                pd.IdentifyingConcept = prompt;
                yield return pd;
            }
        }
        public override IEnumerable<PromptDetails> Prompts => GetPrompts().OrderBy(el => Random.Shared.Next());
    }
}


