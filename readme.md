# MultiImageClient

## It lets you compose a bunch of image generation steps like randomize prompt,add styles etc, and then run them through BFL/Flux, Ideogram or De3.

example:

"a cat" =(randomizer step)=> "a cat, weather conditions: sleet, location: intergalactic crossroads" =(claude rewrite: dear claude, please put this in terms an insane medieval hierophant would use, emitting 100 obscure words)> '<insane prompt>' =(BFL Labs image generation)>

![image](https://github.com/user-attachments/assets/60bf7179-4f4b-4486-a74c-2142fd6a6916)

## how to use it

You do need to be able to run C# code. I use Visual Studio 2022 (NOT visual studio code) to do this. Once you download it, get it to build.

Then, experiment with modifying multiImageClient/Program.cs to do different runs.

There are various ways to customize your runs, from:
* programmatically generating prompts and manipulating them
* iterating through a bunch of prompts from a list
* etc.

## Future

I will add image2text steps, and other text rewrite steps (hopefully cheaper & more open than claude's current setup).
 

# Multi image client.  APIClients for BFL (Black Forest Lab), Ideogram, and Dalle3 (copied)

## The program controls "runs" against those endpoints

Set up multiple types of runs:

* based on a list of prompts in a file or in code
* can systematically expand the list / powerset / permutation etc.
* also can make more complex steps
* hooks for better "rewriters", that is, prompt pre-processing steps such as 'idea' => rewrite with claude/openAi => make image out of it
* repeated images, etc.

## Todos

* Working on a local django app to manage all image inputs and experiment with new combination UIs and things
* That is, from a gallery view, automatically see the related "image2text"s generated in teh past, and maybe drag and drop images to combine them, with a helpful rewriter step.

## Target product design

### Easily compose, order, text, and manage compositions of image to text and text to image operations

* Ideal goal would be to easily compose and control and view the results of complex combinations of actions like "text to image" via multiple endpoint services, "image to text" via claude, gpt, local prompt rewriters, "outer prompt personalities" i.e. a text instruction which are useful for evoking a certain type of image.

* An ideal workflow to aim at would be: for a given interesting prompt, experiment with all the ways you can expand it into various text blobs, then 



### Profiling Image and text APIs:
* for a given prompt text, create a combined image of what each current generator would generate, with annotations, for distribution. i.e. this is a sample of what bfl, de3 and ideogram would produce for a prompt, which is in the image, for distribution and understanding
* Also, eventually each image api will probably have a known "ability" area which you can get at with the right customized prompt. I.e. automatic assistance by adding text/options to queries to dalle3 would be specific to it directly and different for each type of endpoint
* image annotation - i hate seeing AI images that are floating around with no details on what prompt & settings create them. Find a way to bake that in, or have a sidecar file which can automatically be read.

### Assist in helping make a good image from a core idea
* Iterate testing

### Manage censorship
* no point in sending requests which will be blocked
* Local data on each system's censorship patterns to save time and effort

### Viewing & browsing history

* And ALSO to easily be able to view all the images, really well, jumping by tag, between related, generation histories, plus to easily test a prompt in all APIs
* When viewing be able to see the original image

## Scope

### Basic endpoints:

#### Text to image

* Ideogram (working)
* BFL (working)
* Dalle3 (working)
* Midjourney (no api)
* other local (FLUX, etc.) (not set up)

#### Text to image

* None beyond the existing de3 and ideogram rewriters
* many to add, including local
* i.e. a non-censored rewriter would be useful.

# How to Use This

1. Download the code
2. Open the solution in Visual Studio
3. Copy the sample `settings.json` file and rename it to the actual name so it can be found
4. Get an Ideogram API key (e.g., at https://ideogram.ai/manage-api), OpenAI Key, BFL key etc and put them into settings.
5. Fill in the API key in the settings file
6. Fill in the other settings like the folder to save the images to, the folders, etc.
7. Fill in your prompts file or modify the source 
8. Right now, the system is easiest to use if you just open it in Visual Studio and edit the code yourself and build. Eventually it might be nice to have entire run configuration details within non-c sharp files such as json or other complex configurators, so it would be distributable as a bare exe.

## Sample Settings File

There is a sample settings file in the repo but the name doesn't match. Copy that, rename it "settings.json" and then fill in your settings.

## Gallery of Ideogram

### Note: BFL is quite a bit better than Ideogram

https://photos.app.goo.gl/QJn5xPUNEg1uuNdaA

<img src="https://github.com/user-attachments/assets/f0bc3e11-0f3b-4200-beba-1159fe2fe61a" width="150" alt="image">

<img src="https://github.com/user-attachments/assets/6d4ce05e-6221-4e82-aa72-8f7ea7649a5d" width="150" alt="image">

<img src="https://github.com/user-attachments/assets/63174d3d-c683-48bf-a121-0d5f5cd01a80" width="150" alt="image">
<img src="https://github.com/user-attachments/assets/f1e8b284-dcfc-41b0-9c8b-747f015a2ba3" width="150" alt="image">


## A bit more detailed run


```c#
static async Task Main(string[] args)
{
    var settingsFilePath = "settings.json";
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

