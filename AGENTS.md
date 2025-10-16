# Repository Guidelines

## Project Structure & Module Organization
`MultiImageClient/` hosts the C# console orchestrator; `Program.cs` wires runs. `Workflows/` handles execution pipelines; `ImageGenerators/` holds provider adapters; `Describers/` and `promptGenerators/` craft prompts; `promptTransformation/` rewrites text; `Utils/` supplies helpers. Shared contracts live in `ImageGenerationClasses/`. Provider-specific clients sit in `BFLApi/`, `IdeogramAPI/`, and `RecraftAPI/`. `djangoManager/` contains the experimental Django gallery (`imageMaker/`, `static/`, `templates/`). Generated artifacts collect in `saves/` and `output*.png`—ignore them in commits.

## Build, Test, and Development Commands
Restore dependencies via `dotnet restore MultiImageClient.sln`. Compile with `dotnet build MultiImageClient.sln`. Execute runs using `dotnet run --project MultiImageClient/MultiImageClient.csproj`; prompts come from `prompts.txt` and `settings.json`. For the Django tooling, create a venv in `djangoManager/`, install `requirements.txt`, and launch `python djangoManager/imageMaker/manage.py runserver`. Run `dotnet format MultiImageClient.sln` before opening a PR.

## Coding Style & Naming Conventions
Use 4-space indentation and .NET naming: PascalCase for public types/methods, camelCase for locals, Async suffix for asynchronous methods. Favor explicit types for shared models; use `var` only when the type is obvious. Route new configuration through `ImageGenerationClasses/Settings.cs` instead of ad-hoc JSON parsing. Python utilities under `djangoManager/` should follow PEP 8 snake_case, with comments reserved for non-obvious prompt logic.

## Testing Guidelines
No dedicated test project exists yet. Manually validate new workflows by running representative prompts and inspecting generated assets and metadata. When adding automated coverage, create an xUnit project referenced by the solution and ensure `dotnet test` succeeds. Capture regression prompts in `prompts.txt` with notes after bug fixes.

## Commit & Pull Request Guidelines
Keep commits focused and use imperative, present-tense subjects as in history (`rename and genericize describers`). Include context in the body for prompt sets or configuration changes. Pull requests should outline workflow impacts, note which services (BFL, Ideogram, Recraft) are affected, call out required settings updates, and attach screenshots or sample outputs for UI or prompt adjustments.

## Configuration & Secrets
Copy `settings - Fill this in and rename it.json` to `settings.json`, populate provider keys locally, and never commit secrets. Prefer user secrets or environment variables when scripting automation or sharing runs.
