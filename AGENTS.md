# MultiImageClient - Agent Entry Point

Start here. Read this file and the linked documents before doing anything.

## What This Is

C# desktop app that chains together image generation steps across multiple APIs (BFL/Flux, Ideogram, Recraft, DALL-E 3). Supports prompt composition, randomization, Claude rewrites, permutations. Includes an experimental Django gallery.

## Also Read

- [.cursorrules](.cursorrules) — communication style, XML docs policy, namespace rules, constants philosophy

## Related Projects

- **SocialAI** (`/proj/SocialAI/`) — Discord bot for Midjourney image capture
- **ideogramHistoryDownloader** (GitHub) — download Ideogram generation history (may be subsumed here)
- **cmdline-dalle3-csharp** (GitHub) — DALL-E 3 CLI (predecessor, may be subsumed here)
- **IdeogramApiCSharp** (GitHub) — Ideogram API client (predecessor)
- **myBrowser** (`/proj/myBrowser/`) — meta-project; see `capabilities.md` for cross-project skill inventory

---

# Repository Guidelines

## Project Structure & Module Organization
`MultiImageClient/` hosts the C# console orchestrator; `Program.cs` wires runs. `Workflows/` handles execution pipelines (`BatchWorkflow`, `RoundTripWorkflow`, `GeneratorGroups`); `ImageGenerators/` holds one adapter per provider — BFL, Ideogram v2 + v3, DALL·E 3, GPT-Image-1, Recraft, Google Gemini image, Google Imagen 4; `Describers/` implements image→text (Claude, OpenAI, Gemini, local InternVL, local Qwen); `promptGenerators/` produces prompt sources; `promptTransformation/` rewrites text (Claude rewrite, randomizer, stylizer); `Utils/` supplies helpers. Shared contracts and `Settings.cs` live in `ImageGenerationClasses/`. Provider-specific low-level clients sit in `BFLApi/`, `IdeogramAPI/`, and `RecraftAPI/`. `djangoManager/` contains the experimental Django gallery; it hasn't been touched in a year and is not actively developed. `do_flask_intern.py` is an optional local InternVL3 Flask server; `save_b64.py` decodes base64 responses. Generated artifacts collect in `saves/` and `output*.png` — ignore them in commits.

## Build, Test, and Development Commands
All projects target `.NET 9` (main app is `net9.0-windows` + WinForms for compositing). Verify the SDK with `dotnet --list-sdks`; if 9.x is missing, `winget install Microsoft.DotNet.SDK.9`. Restore with `dotnet restore MultiImageClient.sln`. Compile with `dotnet build MultiImageClient.sln`. Execute runs with `dotnet run --project MultiImageClient/MultiImageClient.csproj`; prompts come from `prompts.txt` and `settings.json` (the latter must be created by copying the template `settings - Fill this in and rename it.json`). On current `master` the build is clean (0 errors); ~90 warnings are all `NU190x` advisories for `Magick.NET-Q16-AnyCPU 14.8.2` — safe to bump to `14.12.0` when convenient. For the Django tooling, create a venv in `djangoManager/`, install `requirements.txt`, and launch `python djangoManager/imageMaker/manage.py runserver`. Run `dotnet format MultiImageClient.sln` before opening a PR.

## Coding Style & Naming Conventions
Use 4-space indentation and .NET naming: PascalCase for public types/methods, camelCase for locals, Async suffix for asynchronous methods. Favor explicit types for shared models; use `var` only when the type is obvious. Route new configuration through `ImageGenerationClasses/Settings.cs` instead of ad-hoc JSON parsing. Python utilities under `djangoManager/` should follow PEP 8 snake_case, with comments reserved for non-obvious prompt logic.

## Testing Guidelines
No dedicated test project exists yet. Manually validate new workflows by running representative prompts and inspecting generated assets and metadata. When adding automated coverage, create an xUnit project referenced by the solution and ensure `dotnet test` succeeds. Capture regression prompts in `prompts.txt` with notes after bug fixes.

## Commit & Pull Request Guidelines
Keep commits focused and use imperative, present-tense subjects as in history (`rename and genericize describers`). Include context in the body for prompt sets or configuration changes. Pull requests should outline workflow impacts, note which services (BFL, Ideogram, Recraft) are affected, call out required settings updates, and attach screenshots or sample outputs for UI or prompt adjustments.

## Configuration & Secrets
Copy `MultiImageClient/settings - Fill this in and rename it.json` to `MultiImageClient/settings.json` (already `.gitignore`d), populate only the provider keys for services you intend to use, and never commit secrets. `Settings.Validate()` hard-requires `LogFilePath`, `ImageDownloadBaseFolder`, and the three Google-Cloud fields (`GoogleCloudLocation`, `GoogleCloudProjectId`, `GoogleServiceAccountKeyPath`) even if Imagen 4 is not in the generator list — use placeholders if unused. Optional per-generator keys: `IdeogramApiKey`, `OpenAIApiKey` (DALL·E 3 + GPT-Image-1), `BFLApiKey`, `RecraftApiKey`, `GoogleGeminiApiKey` (NanoBanana), `GoogleCloudApiKey` (Vertex alternative), `AnthropicApiKey` (Claude rewrites & describer). Prefer user secrets or environment variables when scripting automation or sharing runs.
