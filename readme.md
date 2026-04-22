# MultiImageClient

A C# console app that composes chains of image-generation and prompt-rewriting steps across many APIs. You build up a prompt through randomization, stylization and (optional) Claude rewrites, then fan it out to one or more image generators at once.

Example of one chain:

```
"a cat"
   ─[randomizer step]─▶ "a cat, weather conditions: sleet, location: intergalactic crossroads"
   ─[Claude rewrite: put this in the voice of an insane medieval hierophant]─▶ "<insane prompt>"
   ─[BFL/Flux image generation]─▶ <image>
```

![image](https://github.com/user-attachments/assets/60bf7179-4f4b-4486-a74c-2142fd6a6916)

## What it does

Two workflows are exposed in `Program.cs` at startup:

1. **BatchWorkflow** — take a list of prompts, optionally transform them (randomize, stylize, Claude-rewrite, add permanent suffix/prefix), and fan each one out to every configured image generator.
2. **RoundTripWorkflow** (image → describe → re-generate) — take an image, caption it with a describer, then generate a new image from that caption on every configured generator.

## Supported providers

Everything below is wired up in `MultiImageClient/ImageGenerators/`. Any provider is optional — if you don't want to use it, just don't put it in the generator list in `Workflows/GeneratorGroups.cs` and you won't need its API key.

| Provider | File | Uses setting |
|---|---|---|
| Black Forest Labs — Flux v1.1 / v1.1 Ultra | `BFLGenerator.cs` | `BFLApiKey` |
| Ideogram — v2, v2-turbo, v2a, v2a-turbo | `IdeogramGenerator.cs` | `IdeogramApiKey` |
| Ideogram — v3 | `IdeogramV3Generator.cs` | `IdeogramApiKey` |
| OpenAI — DALL·E 3 | `Dalle3Generator.cs` | `OpenAIApiKey` |
| OpenAI — GPT-Image-1 / GPT-Image-1 Mini | `GptImageOneGenerator.cs` | `OpenAIApiKey` |
| Recraft (many styles & substyles) | `RecraftGenerator.cs` | `RecraftApiKey` |
| Google Gemini image ("NanoBanana") | `GoogleGenerator.cs` | `GoogleGeminiApiKey` |
| Google Imagen 4 (Vertex AI) | `GoogleImagen4Generator.cs` | `GoogleCloudProjectId` + `GoogleCloudLocation` + `GoogleServiceAccountKeyPath` (+ `GoogleGeminiApiKey`) |

Prompt-rewriting / transformation steps (see `MultiImageClient/promptTransformation/`):

- `ClaudeRewriteStep` — uses `AnthropicApiKey`.
- `RandomizerStep`, `StylizerStep`, `ManualModificationStep` — no API key required.

Describers (for RoundTripWorkflow, see `MultiImageClient/Describers/`):

- Claude / OpenAI / Gemini image-to-text.
- Local InternVL (via `do_flask_intern.py` — a small Flask wrapper around `OpenGVLab/InternVL3-1B-Pretrained`) and local Qwen. These are optional and only used if you wire them into a run.

## Requirements

- **.NET 9 SDK.** Projects target `net9.0` (and `net9.0-windows` for the main app, because it uses Windows Forms for compositing). Check with `dotnet --list-sdks`; install with `winget install Microsoft.DotNet.SDK.9` if missing.
- Windows (the main project is `net9.0-windows` + `UseWindowsForms`). The three API-client projects are plain `net9.0` and portable, but `MultiImageClient.csproj` itself is Windows-only.
- **Visual Studio 2022** (17.9+) works, or just `dotnet` CLI.
- For the Imagen 4 path: a Google Cloud project with Vertex AI enabled and a service-account JSON key.
- For the local InternVL describer: Python 3.10+, PyTorch with CUDA, `transformers`, `flask`, `pillow`, `torchvision`.
- For the (experimental) Django gallery under `djangoManager/`: Python + `django mysqlclient requests ipdb aiohttp`, plus MySQL.

## Build & run

```powershell
# one-time
dotnet restore MultiImageClient.sln

# build
dotnet build MultiImageClient.sln

# run the main app (from repo root)
dotnet run --project MultiImageClient/MultiImageClient.csproj
```

At startup the program asks whether to run `BatchWorkflow` (1) or `RoundTripWorkflow` (2). Prompts come from `prompts.txt` next to the project (`MultiImageClient/prompts.txt`) — or wherever `LoadPromptsFrom` points — and from whatever you wire into `Program.cs`.

To customize a run, edit:

- `MultiImageClient/Program.cs` — top-level wiring.
- `MultiImageClient/Workflows/GeneratorGroups.cs` — the `myGenerators` list controls which image generators (and which parameter combinations) are actually hit for each prompt.
- `MultiImageClient/Workflows/BatchWorkflow.cs` or `RountripWorkflow.cs` — the workflow logic itself.

## Configuration

Copy `MultiImageClient/settings - Fill this in and rename it.json` to `MultiImageClient/settings.json` and fill in only the keys for the services you want to use. `settings.json` is already `.gitignore`d.

Fields validated at startup (required):

- `LogFilePath`
- `ImageDownloadBaseFolder`
- `GoogleCloudLocation`, `GoogleCloudProjectId`, `GoogleServiceAccountKeyPath` — **note:** these are required by `Settings.Validate()` even if you don't use Imagen 4. If you aren't using Imagen, just point them at placeholder values.

Optional (only needed for the matching generators):

- `IdeogramApiKey` — https://ideogram.ai/manage-api
- `OpenAIApiKey` — https://platform.openai.com/api-keys
- `BFLApiKey` — https://api.bfl.ai/
- `RecraftApiKey` — https://www.recraft.ai/
- `GoogleGeminiApiKey` — https://ai.google.dev/gemini-api/docs/api-key
- `GoogleCloudApiKey` — Vertex API key (alternative to the service-account path)
- `AnthropicApiKey` — https://console.anthropic.com/settings/keys

Cosmetic flags: `SaveJsonLog`, `EnableLogging`, `AnnotationSide`.

## Project layout

```
MultiImageClient/            main console app (net9.0-windows)
  Program.cs                 entry point
  Workflows/                 BatchWorkflow, RoundTripWorkflow, GeneratorGroups
  ImageGenerators/           one file per provider (BFL, Ideogram, Dalle3, Recraft, GPT-Image-1, Gemini, Imagen 4)
  Describers/                image-to-text (Claude, OpenAI, Gemini, local InternVL, local Qwen)
  promptTransformation/      Claude rewrite, randomizer, stylizer, manual edits
  promptGenerators/          prompt sources (from file, from code, scenes-from-story, etc.)
  Utils/                     ImageCombiner, ImageSaving, TextFormatting
  Enums/, Interfaces/, Implementation/, TextLLMs/

ImageGenerationClasses/      shared types + Settings.cs (net9.0 library)
BFLApi/                      BFL/Flux API client (net9.0)
IdeogramAPI/                 Ideogram API client (net9.0)
RecraftAPI/                  Recraft API client (net9.0)

do_flask_intern.py           optional local InternVL3-1B Flask server
save_b64.py                  helper to decode base64 payloads from provider responses
djangoManager/               experimental Django gallery (MySQL-backed, not actively developed)
IdeogramHistoryExtractor/    older scratch folder
```

## Gallery

If you haven't tried BFL/Flux, you should — better composition than DALL·E 3, 2× the pixels, not quite as strong as Ideogram on in-image text but generally much cleaner images.

- BFL gallery: https://photos.app.goo.gl/baJNz9SWX1fq1tT77
- Ideogram gallery: https://photos.app.goo.gl/QJn5xPUNEg1uuNdaA

<img src="https://github.com/user-attachments/assets/f0bc3e11-0f3b-4200-beba-1159fe2fe61a" width="150" alt="image">
<img src="https://github.com/user-attachments/assets/6d4ce05e-6221-4e82-aa72-8f7ea7649a5d" width="150" alt="image">
<img src="https://github.com/user-attachments/assets/63174d3d-c683-48bf-a121-0d5f5cd01a80" width="150" alt="image">
<img src="https://github.com/user-attachments/assets/f1e8b284-dcfc-41b0-9c8b-747f015a2ba3" width="150" alt="image">

## Design goals (aspirational)

- Easily compose, order, and manage combinations of image-to-text and text-to-image steps across endpoints.
- Given one interesting prompt, see side-by-side what every current generator would do with it, with annotations, as a distributable composite image.
- Per-provider "ability maps" — known strengths, known censorship patterns — so requests that will be blocked aren't sent in the first place.
- Viewing & browsing history with tag jumping and "re-run this exact prompt on every API" buttons.
- Persistent image-level metadata (prompt, settings, seed) baked into the file or a sidecar, so images shared out never lose their provenance.

## Todos / not done yet

- Django gallery is still an experiment; no active work.
- More / cheaper / less-censored prompt rewriters beyond Claude.
- Move run configuration out of `Program.cs` into JSON so the app is distributable as a bare exe.
