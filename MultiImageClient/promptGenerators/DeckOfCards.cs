using System;
using System.Collections.Generic;
using System.Linq;


namespace MultiImageClient
{
    public class DeckOfCards : AbstractPromptGenerator
    {
        public DeckOfCards(Settings settings) : base(settings)
        {
        }

        public override string Name => nameof(DeckOfCards);
        public override IEnumerable<string> Variants => new List<string> { "" };
        public override string Prefix => "Describe the design of new playing card: ";
        public override int ImageCreationLimit => 500;
        public override int CopiesPer => 1;
        public override bool RandomizeOrder => false;
        public override string Suffix => " Describe the design of this new card in detail, using about 120 words. Output a description of such a card, as prose, without newlines. Playing cards always have a white background. The number and type of card must appear in the corner, and they should use the color theme.";
        public override Func<string, string> CleanPrompt => (arg) => arg;

        private IEnumerable<PromptDetails> GetPrompts()
        {
            var ranks = "2 3 4 5 6 7 8 9 10 Jack Queen King Ace".Split(" ").ToList();
            var extraRanks = "0 Pokemon Elon Cross Shaman Fool Rook Knight Pawn Joker Time Pi Infinity Lava Ice Stone".Split(" ");
            ranks.AddRange(extraRanks);
            var suits = new List<string> { "Hearts", "Clubs", "Diamonds", "Spades" };

            var artists = new List<string> { "Sol LeWitt", "Victor Vasarely", "Chuck Close", "Frank Stella" };
            var themes = new List<string> { "Yellow & Turquoise", "Green", "Pure White and greyscale", "Natural Wood" };
            var emotionThemes = new List<string> { "Melancholy", "Schadenfreude ", "Nostalgia", "Awe" };

            foreach (var jobs in ranks)
            {
                for (var ii = 0; ii < suits.Count; ii++)
                {
                    var pd = new PromptDetails();
                    var prompt = $"The {jobs} of {suits[ii]} using the style of {artists[ii]} using the color {themes[ii]} and emotion: {emotionThemes[ii]}";
                    pd.ReplacePrompt(prompt, prompt, TransformationType.InitialPrompt);
                    pd.IdentifyingConcept = $"{artists[ii]}_{suits[ii]}_{jobs}.";
                    yield return pd;
                }
            }
        }


        public override IEnumerable<PromptDetails> Prompts => GetPrompts();

    }
}
