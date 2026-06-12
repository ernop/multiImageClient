# Project Organization

Use this layout for new work unless there is a strong local reason not to.

## Source Code

- `MultiImageClient/`: main C# app and workflows.
- `ImageGenerationClasses/`: shared C# models and settings.
- `<Provider>API/`: provider-specific low-level clients such as `BFLApi/`, `IdeogramAPI/`, `RecraftAPI/`, and `XAIGrokAPI/`.
- `tools/`: reusable scripts and small utilities that are part of the project but not the main app.

## Documentation

- `readme.md`: user-facing project overview, quick start, and provider list.
- `AGENTS.md`: agent/project operating instructions.
- `docs/`: focused project documentation by topic.

Use lower-case, hyphenated doc names in `docs/`, for example:

```text
docs/grok-web-export-archive.md
docs/project-organization.md
```

Avoid adding broad one-off root-level docs unless they are entry points like `readme.md` or `AGENTS.md`.

## Config

- Keep committed examples/templates next to the code that consumes them.
- Keep local secrets and machine-specific settings out of git.
- For the main app, use `MultiImageClient/settings - Fill this in and rename it.json` as the committed template and `MultiImageClient/settings.json` as the ignored local file.
- New tool scripts should prefer explicit CLI arguments and optional sample config files over hardcoded local paths.

## User Data And Generated Artifacts

Do not commit personal or generated archives. Keep them outside the repo unless they are tiny, sanitized fixtures intentionally added for tests.

Examples that should stay outside git:

- Grok export zips and extracted archives,
- generated images and videos,
- prompt corpora containing personal content,
- downloader manifests/logs from a personal account,
- browser indexes containing personal prompts or IDs,
- source pin exports.

## Tooling

Reusable tool scripts go under `tools/<topic>/`.

Each tool folder should include a `README.md` explaining:

- what data it reads,
- what files it writes,
- whether outputs can contain personal data,
- normal command examples,
- retry or cleanup behavior.

Tool scripts should:

- accept paths via command-line arguments,
- avoid personal defaults,
- write generated/user data to caller-provided output folders,
- preserve source inputs where practical,
- log enough detail to audit long-running data work.
