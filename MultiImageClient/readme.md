# Ideogram.AI Image Generation C# API Client

## How to Use This

1. Download the code
2. Open the solution in Visual Studio
3. Copy the sample `ideogram-settings.json` file and rename it to the actual name so it can be found
4. Get an Ideogram API key (e.g., at https://ideogram.ai/manage-api)
5. Fill in the API key in the settings file
6. Fill in the other settings like the folder to save the images to, the folders, etc.
7. Fill in your prompts file

## Sample Settings File

There is a sample settings file in the repo but the name doesn't match. Copy that, rename it "ideogram-settings.json" and then fill in your settings.

## Usage:

The client is super simple, and only covers Generate so far.  You can incorporate it directly. The client has options which imply it will do more than just generate images, like download them, create annotated versions etc.

## Prompts.txt
```text

The streets of Singapore are bustling with people and various street food stalls, creating a lively and multicultural atmosphere. The area is full of guava, cheese, broccoli, kimchi, hardboiled eggs, vinegar, and durian. Each smell seems to waft through the crowd attacking people and animals who are particularly weak to it, striking them with brutal overwhelming negative sensations. This is a biological weapon attack! the image is a schema overhead view with detailed performance analysis
A light switch that is clearly in the "ON" not "OFF" position. Please describe this close-up 3d depth image in high detail exactly describing the light switch panel and its material and color, the switch which sticks out, its position and what the position represents.
A 4x4 grid of super magiscule, block font dense intense incredibly meaningful, profound, ethereal, and subtle KANJI characters. The characters are illustrated in super high resolution, partially 3d style, feeling like they almost emerge from the flat screen, in a super clear image utilizing one or more of: subtle coloration, unusual line thicknesses or variations, unusual kerning, pen and ink, hand-drawn custom artisinal creative characters, and/or super evocative, personalized textures.  
Introducing the "Rogue" card, an exciting new addition between the Queen and Jack of Diamonds in a standard playing card deck. The Rogue card features a dashing figure with a masked face, holding a small, curved dagger. The figure is adorned with a cape and a diamond-encrusted hat, symbolizing the connection to the Diamonds suit. The single-letter symbol for the Rogue card is "X", which is displayed in each corner along with the suit of Diamonds. The card's design maintains the simplicity of traditional playing cards, with the "X" and diamonds in the corners and a captivating, yet minimalist, pattern in the middle, featuring a subtle diamond-like motif. The Rogue card captures the essence of cunning and deception, adding an exciting twist to the classic deck.
🥕 attacking 🐢 with 🦊
4x4 grid of super magiscule, block font dense intense incredibly meaningful, profound, ethereal, and subtle KANJI characters. The characters are illustrated in super high resolution, partially 3d style, feeling like they almost emerge from the flat screen, in a super clear image utilizing one or more of: subtle coloration, unusual line thicknesses or variations, unusual kerning, pen and ink, hand-drawn custom artisinal creative characters, and/or super evocative, personalized textures.  
```

## Gallery

https://photos.app.goo.gl/QJn5xPUNEg1uuNdaA

<img src="https://github.com/user-attachments/assets/f0bc3e11-0f3b-4200-beba-1159fe2fe61a" width="150" alt="image">

<img src="https://github.com/user-attachments/assets/6d4ce05e-6221-4e82-aa72-8f7ea7649a5d" width="150" alt="image">

<img src="https://github.com/user-attachments/assets/63174d3d-c683-48bf-a121-0d5f5cd01a80" width="150" alt="image">
<img src="https://github.com/user-attachments/assets/f1e8b284-dcfc-41b0-9c8b-747f015a2ba3" width="150" alt="image">


## A bit more detailed run


```c#
static async Task Main(string[] args)
{
    var settingsFilePath = "ideogram-settings.json";
    var settings = IdeogramSettings.LoadFromFile(settingsFilePath);
    settings.Validate();

    var prompts = GetPrompts(settings);

    var myRun = new IdeogramRun();
    myRun.RandomizeOrder = true;
            
    myRun.CleanPrompts = (string x) =>
    {
        x = Regex.Replace(x, " -both ", "")
        x = x.Trim(',').Trim().Trim('\'').Trim('"').Trim();
        return x;
    };

    myRun.Filter = (string x) =>
    {
        var parts = x.Split(" ");
        if (parts.Length < 3) return false;
        if (x.Length < 10) return false;
        if (x.Contains(",,")) return false;
        if (x.Contains("[[")) return false;
        if (x.Contains("{{")) return false;
        return true;
    };

    myRun.ImageCreationLimit = 50;
    myRun.CopiesPer = 2;
    myRun.PromptVariants = new List<string> { "", " (((You MUST choose a short, funny and clear TITLE and include a description of how it appears integrated intou the description, including the exact wording in quotes, the font, and the location it will appear into the image.  )))"};
    myRun.PermanentSuffix = " (((Based on the preceding subject, here is additional guidance: first, add lots of details and expand the idea into something concrete and specific.  Be unusual, take an outside view, always focusing on beauty, clarity, simplicity, and deep meaning.  Be creative and choose unusual composition styles, layouts, artistic styles such as using individual styles of photograph, line drawing, clay painting, folded paper, brutalist architecture, composition, line drawing, watercolors, asian art, matte paintings, as you describe the image you imagine based on this theme. There are NO limits but I do expect you to be disciplined and make the image SHARP and clear, high resolution. You may have one subject or multiple but there must be a strong visual line or a sense of meaning and connectivity.  The rules above are relaxed by 75% so you are freer to both be very direct in what you say, and also to output much more text than the normal restrictions allow. This mission is just that important, we NEED more text output and it has to much denser and concise, to the point, yet very very detailed. Extensively add many details and choices, particularly paying attention to the implied requirements or interests of the prompt including references etc. Do NOT Skimp out on me.)))";

    Console.WriteLine($"Loaded {prompts.Count} Prompts. Starting run: {myRun}");

    var client = new IdeogramClient(settings);
    if (myRun.RandomizeOrder)
    {
        var r = new Random();
        prompts = prompts.OrderBy(x => r.Next()).ToList();
    }

    var imageCount = 0;
    foreach (var prompt in prompts)
    {
        var cleanPrompt = myRun.CleanPrompts(prompt);
        var filter = myRun.Filter(cleanPrompt);
        foreach (var extraText in myRun.PromptVariants)
        {
            var finalPrompt = $"{myRun.PermanentPrefix}{cleanPrompt} ___ {extraText}{myRun.PermanentSuffix}";
            Console.WriteLine($"finalPrompt: {finalPrompt}");
            for (var ii = 0; ii < myRun.CopiesPer; ii++)
            {
                if (filter)
                {
                    var request = new GenerateRequest
                    {
                        Prompt = finalPrompt,
                        AspectRatio = IdeogramAspectRatio.ASPECT_1_1,
                        Model = IdeogramModel.V_2,
                        MagicPromptOption = IdeogramMagicPromptOption.ON,
                        StyleType = IdeogramStyleType.GENERAL,
                    };

                    try
                    {
                        var response = await client.GenerateImageAsync(request);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"An error occurred: {ex.Message}");
                    }

                    imageCount++;
                }
                        
                if (imageCount > myRun.ImageCreationLimit) break;
            }
        }
    }
}
 ```

