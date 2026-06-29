#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiImageClient
{
    public static class PortraitFixturePrompts
    {
        private static readonly (string Key, string Label)[] Ethnicities =
        {
            ("south_korean", "South Korean"),
            ("japanese", "Japanese"),
            ("chinese", "Chinese"),
            ("english", "English"),
            ("egyptian", "Egyptian"),
            ("african", "African"),
            ("russian", "Russian"),
            ("polish", "Polish"),
        };

        private static readonly (string Singular, string Plural)[] Genders =
        {
            ("man", "men"),
            ("woman", "women"),
        };
        private static readonly string[] Ages = { "18", "28", "42" };

        public static IReadOnlyList<string> BuildAll()
        {
            return Ethnicities
                .SelectMany(ethnicity => Genders, (ethnicity, gender) => (ethnicity, gender))
                .SelectMany(x => Ages, (x, age) => (x.ethnicity, x.gender, age))
                .Select(x => BuildPrompt(x.ethnicity.Label, x.gender.Plural, x.age))
                .ToList();
        }

        public static IReadOnlyList<string> RandomSample(int count)
        {
            var all = BuildAll().ToList();
            for (var i = all.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (all[i], all[j]) = (all[j], all[i]);
            }
            return all.Take(Math.Min(count, all.Count)).ToList();
        }

        private static string BuildPrompt(string ethnicity, string genderPlural, string age)
        {
            return $"A natural full-body street photo of two {age}-year-old {ethnicity} {genderPlural} "
                + "walking together along a palm-lined street in Florida. "
                + "They seem relaxed, cheerful, and comfortable with each other, like a casual photo they might post on Instagram. "
                + "Realistic candid photography, clear full normal daytime lighting, bright exposure, casual outfits, clear head-to-toe view. "
                + "Do not make the image dim, murky, grimy, muddy, gloomy, shadow-choked, underexposed, dusk-like, night-like, or dark.";
        }
    }
}
