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

## Run Modes (CLI flags)
See `RunOptions.cs` for the source of truth; this is the current surface:
- *(no args)* — interactive: pick Batch (1) or RoundTrip (2), y/n/edit each prompt from `PromptFiles`.
- `--auto` — skip menu (defaults to Batch), auto-accept every prompt.
- `--workflow 1|2` — 1=Batch, 2=RoundTrip.
- `--limit N` — stop after N prompts.
- `--prompt "..."` — single inline prompt via `InlinePromptSource` instead of `PromptFiles`.
- `--fast` — one fixed gpt-image-2 low/1024x1024/moderation=low call per prompt. Cheap smoke-test config.
- `--quick-test` — like `--fast`, plus every streamed partial PNG is saved AND popped in the default viewer. Still asks y/n per prompt unless combined with `--auto`.
- `--backfill-dl` — one-shot: mirror every image under `Settings.ImageDownloadBaseFolder` into `Settings.FlatImageMirrorPath` and exit.
- `--repl` — **interactive prompt-by-prompt REPL with async dispatch**. Defaults: gpt-image-2 at 2048x2048 / high / moderation=low, up to 5 prompts in flight concurrently. Each line is either a prompt (fired asynchronously, stdin stays responsive) or a `:command`. Grids are built and saved but NOT opened in the viewer. Commands: `:size WxH`, `:quality low|medium|high`, `:moderation auto|low`, `:concurrency N`, `:gens list|add|remove|reset` (names: gpt2, dalle3, ideogram, recraft, bfl, google, imagen4), `:status`, `:wait`, `:last`, `:retry`, `:edit`, `:help`, `:quit`. Per-prompt override syntax: `[size=1024x1024,q=low] a red apple on a white plate`. Initial defaults can be pre-set from the command line via `--repl-size`, `--repl-quality`, `--repl-moderation`, `--repl-concurrency`. Implementation in `Workflows/ReplWorkflow.cs`.

## Coding Style & Naming Conventions
Use 4-space indentation and .NET naming: PascalCase for public types/methods, camelCase for locals, Async suffix for asynchronous methods. Favor explicit types for shared models; use `var` only when the type is obvious. Route new configuration through `ImageGenerationClasses/Settings.cs` instead of ad-hoc JSON parsing. Python utilities under `djangoManager/` should follow PEP 8 snake_case, with comments reserved for non-obvious prompt logic.

## Visual & Typography Policy (combined-image output, labels, UI text)
- **Never render text in gray.** No `MutedGray`, no `Color.FromRgb(x,x,x)` where R==G==B in the mid range, no "subtle" gray labels. If a secondary label needs to look secondary, reduce its font size and/or reuse the existing semantic color (e.g. `SuccessGreen`, `ErrorRed`, `Black`, `Gold`) — the contrast comes from size, not desaturation.
- When reserving vertical space for a text block, include room for descenders (e.g. `g`, `p`, `y`, `j`). `TextMeasurer.MeasureBounds` can under-report; add ~25% of font size as descender padding when stacking text bands.
- Padding above/below a standalone text panel (e.g. prompt panel below the grid) should be proportional to the font size used inside it, not a fixed `Padding * 3`.
- Secondary labels that sit beside a primary label (e.g. per-image timing next to the model name) should be bottom-aligned with the primary label so the smaller text hangs off the baseline of the larger one, not free-floating.

## Image Saving Policy
- **Never resize or re-encode the bytes returned by the image endpoint when saving a Raw variant.** `ImageSaving.SaveImageAsync` must write the API's PNG/JPEG/WEBP bytes verbatim via `File.WriteAllBytesAsync`. Thumbnail-scale downsizing for combined-grid display is fine — that's an in-memory copy used only for layout, never written over the Raw file.

## gpt-image-2 Endpoint Options (reference)
The `/v1/images/generations` endpoint with `model=gpt-image-2` accepts:
- `size`: `1024x1024`, `1536x1024`, `1024x1536`, `2048x2048`, `2048x1152`, `3840x2160`, `2160x3840`, or `auto`. Arbitrary resolutions are legal when edges are multiples of 16, max edge ≤ 3840, total pixels in [655 360, 8 294 400], and long:short ratio ≤ 3:1.
- `quality`: `low`, `medium`, `high`, `auto`.
- `moderation`: `auto` (default) or `low` (permissive — we use `low` for batch runs).
- `output_format`: `png` (default), `jpeg`, `webp`. `output_compression` (0–100) applies to jpeg/webp.
- `background`: `auto`, `transparent`, `opaque` — transparent is not supported by gpt-image-2 (png/webp only, in practice rejected on this model).
- `stream`: `true` — we always stream and consume SSE to surface partials + heartbeat.
- `partial_images`: 0–3 (we send 2).
- **Do NOT send `input_fidelity`** — the endpoint rejects it on gpt-image-2 (always high-fidelity).

Pricing is token-based ($30 / 1M output tokens). Rough per-image ceilings we report: low ≈ $0.02, medium ≈ $0.08, high ≈ $0.25.

## Testing Guidelines
No dedicated test project exists yet. Manually validate new workflows by running representative prompts and inspecting generated assets and metadata. When adding automated coverage, create an xUnit project referenced by the solution and ensure `dotnet test` succeeds. Capture regression prompts in `prompts.txt` with notes after bug fixes.

## Commit & Pull Request Guidelines
Keep commits focused and use imperative, present-tense subjects as in history (`rename and genericize describers`). Include context in the body for prompt sets or configuration changes. Pull requests should outline workflow impacts, note which services (BFL, Ideogram, Recraft) are affected, call out required settings updates, and attach screenshots or sample outputs for UI or prompt adjustments.

## Configuration & Secrets
Copy `MultiImageClient/settings - Fill this in and rename it.json` to `MultiImageClient/settings.json` (already `.gitignore`d), populate only the provider keys for services you intend to use, and never commit secrets. `Settings.Validate()` hard-requires only `LogFilePath` and `ImageDownloadBaseFolder`; every per-generator API key (and the Google Cloud trio: `GoogleCloudLocation`, `GoogleCloudProjectId`, `GoogleServiceAccountKeyPath`) is validated lazily by the generator that actually needs it, so unused generators can be left blank. Optional per-generator keys: `IdeogramApiKey`, `OpenAIApiKey` (DALL·E 3, GPT-Image-1, GPT-Image-2), `BFLApiKey`, `RecraftApiKey`, `GoogleGeminiApiKey` (NanoBanana), `GoogleCloudApiKey` (Vertex alternative), `AnthropicApiKey` (Claude rewrites & describer). Prompt file list lives in `PromptFiles` (array of absolute paths). `FlatImageMirrorPath` is an optional flat-folder mirror: if set, every saved raw/annotated/combined image is also copied to that single folder (best-effort, never fatal) — leave blank to disable. `TypedPromptsAppendFile` is an optional plain-text corpus: if set, any free-form prompt you type at the interactive batch loop is appended as one line to that file (embedded newlines collapsed, parent folder auto-created, never fatal) — handy for growing something like `2023-prompts.txt` over time. Prefer user secrets or environment variables when scripting automation or sharing runs.
