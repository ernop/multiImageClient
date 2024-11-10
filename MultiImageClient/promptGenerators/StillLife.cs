using System;
using System.Collections.Generic;
using System.Linq;



namespace MultiImageClient
{
    public class StillLife : AbstractPromptGenerator
    {
        public StillLife(Settings settings) : base(settings)
        {
        }

        public override string Name => "Emotional Man";
        public override IEnumerable<string> Variants => new List<string> { "" };
        public override string Prefix => "";
        public override int ImageCreationLimit => 300;
        public override int CopiesPer => 3;
        public override bool RandomizeOrder => true;
        public override int FullyResolvedCopiesPer => 1;
        public override string Suffix => "";
        public override Func<string, string> CleanPrompt => (arg) => arg;

        private IEnumerable<PromptDetails> GetPrompts()
        {
            var emotions = "aggression,ambition,anger,anguish,anxiety,astonishment,awe,betrayal,blank,boredom,calm,camaraderie,condescension,contented,contentment,creativity,curiosity,defeat,despair,determination,diligence,disgust,dominance,ecstasy,enchantment,enlightenment,envy,exasperation,exhaustion,exhilaration,fascination,fear,foolishness,gloom,grandiosity,gratitude,greed,grumpiness,guilt,happiness,hatred,hope,hopelessness,hostility,humility,impatience,indignation,irritation,jealousy,loneliness,longing for love,love,loyalty,lust,melancholy,mischievousness,mournful,mourning,nostalgia,obliviousness,overwhelm,passion,perplexity,pitiful,pity,pomposity,pride,rebellion,regret,relief,remorse,resentment,revenge,reverence,sacrifice,sadness,serenity,shame,skepticism,skinship,stress,submission,surprise,sympathy,thrill,transcendence,triumph,trust,unease,unity,valor,veneration,vigilance,vulnerability,yearning,adoration,alienation,anticipation,apathy,bewilderment,catharsis,cynicism,deference,delight,detachment,disillusionment,empowerment,fervor,forlorn,fulfillment,indifference,jubilation,kinship,lethargy,liberation,malaise,pensiveness,petulance,prudence,redemption,solitude,tenacity,trepidation,vindication,zeal".Split(",", StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var emotion in emotions)
            {
                var pd = new PromptDetails();
                //var prompt = $"Your subject is the very concept of the feeling: {emotion}. Portray this concept thoroughly, and focus on this specific single concept and feeling, only.  Be OVERLY symbolic and abstract in the symbology, intensifying the effect. Your overall composition style should be eminently high class, suitable, no matter what, for display in the ultimate Louvre, even more elite than the current one. Identify the specific aspects of this feeling in highly technical terms, specifically excluding all nearby emotions. Then, compose a still life whose design, elements, style, colors, textures, orientation, complexity or simplicity, taste, the form of the artwork, the apparent age, tradition, or modernity of the image, its composition, and every other aspect of it fully embody the very specific aspects of this singular emotion in an extremely intense, poignant, deeply moving, and very clear, obvious and distinct way. There should be a deep and complex   relationship between the elements. Use specific styles, ranging from hypermodern to traditional, conservative, ancient, foreign, european, etc.  Include the following for things which appear: their orientation, light sources, facing direction, and interanl relationships as well as relation to the viewer, symbolisms, the way they are drawn, etc. Be extremely specific on exactly what should appear, and where, within the image. No human face appears at all. Exclude all textual elements. You may need to be very wordy with your output, more than normal, to make sure you cover all the required elements. This is required; you MUST output a very long and hyper specific prompt with specific artists and styles mentioned.";
                var prompt = $"Create a description of an ART work expressing strong {emotion} with a strong POLISH influences both in terms of product design, architecture, anthropomorphism, symbolism and religious, philosophical, and artistic traditions yet retaining your elite status. Be OVERLY symbolic and abstract in the symbology, intensifying the effect. Your overall composition style should be eminently high class, suitable, no matter what, for display in the ultimate Louvre, even more elite than the current one. Identify the specific aspects of this feeling in highly technical terms, specifically excluding all nearby emotions.   Detail specifics regarding the artwork's design and characteristics.:\r\nTechnical requirements:\r\n\r\nStyle: [Choose 1-2: Other styles than these, or Hyperrealism, Dutch Golden Age, Minimalist Modern, Pop Art, Baroque etc. But pick OTHER ones]\r\nPrimary artist influences: [e.g., Giorgio Morandi, Wayne Thiebaud, Juan Sánchez Cotán but do NOT pick them, pick other people of similar levels of fame or less]\r\nMedium: [Oil painting, Digital art, Photography, Watercolor]\r\nAspect ratio: [9:7]\r\nLighting: [Describe specific lighting direction, intensity, and quality]\r\n\r\nComposition elements:\r\n\r\nPrimary focal object: [Describe specific item, position, size]\r\nSupporting objects: [List 2-3 objects with exact positions]\r\nBackground: [Describe surface and environment]\r\nColor palette: [List specific colors, e.g., \"deep burgundy (#800020), cream white (#FFFDD0)\"]\r\n\r\nEmotional expression through:\r\n\r\nTexture: [e.g., \"smooth polished surfaces reflecting light\" or \"rough, weathered textures\"]\r\nComposition: [e.g., \"objects arranged in descending diagonal line\" or \"circular arrangement\"]\r\nSymbolism: [Describe specific symbolic meanings of chosen objects]\r\nMood: [Describe atmosphere through lighting and shadow]\r\n\r\nTechnical specifications:\r\n\r\nCamera angle: [e.g., \"45-degree elevated view,\" \"straight-on eye level\"]\r\nDepth of field: [Specify focus areas]\r\nDistance: [Specify viewing distance]\r\n\r\nStyle references:\r\n\r\nPrimary: [e.g., \"Chiaroscuro technique of Caravaggio\"]\r\nSecondary: [e.g., \"Color theory of Josef Albers\"]\r\n\r\nQuality requirements:\r\n\r\nPhotorealistic rendering\r\nHigh detail in textures\r\nSharp focus on key elements\r\nProfessional studio lighting quality\r\n8K resolution\r\nRay-traced shadows and reflections. Overall: do NOT just copy the examples given in this prompt. Generate new unique ones that clearly show {emotion} {emotion} {emotion} ";
                pd.ReplacePrompt(prompt, prompt, TransformationType.InitialPrompt);
                pd.IdentifyingConcept = $"{emotion}";
                yield return pd;
            }

        }
                public override IEnumerable<PromptDetails> Prompts => GetPrompts().OrderBy(el=>Random.Shared.Next());

    }
}
