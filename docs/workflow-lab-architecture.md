# Workflow Lab Architecture

The Workflow Lab is the browser-driven loop for:

```text
original prompt -> multi-image generation -> image description -> remix prompt -> generation again
```

## Ownership Rules

- `GeneratorGroups` owns provider presets and stable provider preset IDs.
- `PromptGenerationRunner` owns executing a prompt against a provider preset and returning raw generated image artifacts.
- `MultiImageClient.Web` owns HTTP endpoints, durable workflow runs, graph nodes/edges, job status, and browser UI.
- `WorkflowStore` owns workflow-lab paths under `saves/workflow-lab`.
- `WorkflowLogService` owns the live browser console and append-only log file.
- The browser UI may select exposed provider presets and edit text, but it must not define provider-specific payloads, paths, or hidden pipeline rules.

## Current Stage Model

- `prompt`: root text or a later remix prompt.
- `image`: raw image artifact produced by a `GenerateImages` job.

Next planned stages:

- `description`: image-to-text result from a selected describer.
- `remix`: editable prompt composed from original text plus a selected description.

Every stage should declare parent node IDs and write graph edges. Client-only text copying is allowed as a convenience, but it is not workflow lineage.

## Prompt-Writing Defaults

Generated, suggested, fixture, or remix prompts for image models should include
the standing clarity preference unless the user explicitly asks for dark, night,
low-key, gloomy, or murky output:

```text
Clear, bright, full normal daytime lighting by default. Not dim, murky, grimy, muddy, gloomy, shadow-choked, underexposed, dusk-like, night-like, or dark. Prefer readable, coherent, visually organized images with clean composition, clear separation of subjects or groups, concise high-contrast text when text is needed, and attractive balanced color. Favor clarity over murky cinematic drama.
```

This is especially important for OpenAI image models such as `gpt-image-2`,
where prompts can otherwise drift into dark cinematic or apocalyptic lighting
even when the core subject is correct. Workflow stages should make this language
visible in generated or remixed prompt text rather than silently appending hidden
provider instructions.

## Provider Presets

Provider presets have stable IDs, for example:

- `openai.gpt-image-2.low.square`
- `xai.grok-imagine.high.2k.square`
- `recraft.v4-1.any.square`

UI labels may change, but preset IDs should remain stable so saved workflow runs can be interpreted later.

## Artifact Policy

Workflow Lab image nodes point to files owned by the workflow run folder. The web path should not also use the legacy annotated `ImageManager` save pipeline unless a stage explicitly asks for annotated exports.

The legacy CLI workflows may continue using `ImageManager`, `ImageSaving`, and `ImageCombiner`.

## Logging

The live console reads from:

```text
saves/workflow-lab/workflow-lab.log
```

Log messages should include enough context to correlate run, job, provider preset, and node IDs.
