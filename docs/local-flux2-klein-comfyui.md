# Local FLUX.2 Klein 4B via ComfyUI

This is the local/open-weight path for FLUX.2 Klein 4B. It is separate from the
BFL hosted API and does not use `BFLApiKey`.

## Model Setup

Install or update ComfyUI, then install the FLUX.2 Klein 4B text-to-image
workflow from ComfyUI's workflow templates.

Model files expected by the standard ComfyUI FLUX.2 Klein 4B workflow:

- `ComfyUI/models/diffusion_models/flux-2-klein-4b-fp8.safetensors`
- `ComfyUI/models/vae/flux2-vae.safetensors`
- text encoder:
  - default: `ComfyUI/models/text_encoders/qwen_3_4b.safetensors`
  - uncensored path: replace that loader with an ablated Qwen3-4B text encoder,
    such as `qwen3-4b-abl-q4_0.gguf`, using a GGUF-compatible loader.

The ablated text encoder removes prompt-side filtering. It does not add visual
knowledge that the base diffusion model never learned, so explicit anatomy may
still need a compatible LoRA.

## Workflow Contract

Export the ComfyUI workflow in API format and save it somewhere stable, outside
`saves/`. In the positive prompt field, put this placeholder:

```text
{{PROMPT}}
```

Optional: put `{{SEED}}` in any string field where you want this client to insert
a random seed. If your seed field is numeric, leave it fixed or randomize it in
the ComfyUI workflow.

Then set these in `MultiImageClient/settings.json`:

```json
{
  "ComfyUIBaseUrl": "http://127.0.0.1:8188",
  "ComfyUIFlux2KleinWorkflowPath": "C:\\path\\to\\flux2-klein-4b-uncensored-api.json",
  "ComfyUIPollIntervalMs": 1000,
  "ComfyUITimeoutSeconds": 900
}
```

## Running

Start ComfyUI first. Then run only the local Klein provider:

```powershell
dotnet run --project MultiImageClient/MultiImageClient.csproj -- `
  --provider-sample-showcase `
  --provider-sample-file test-new.txt `
  --limit 8 `
  --provider-sample-providers "local,klein"
```

The normal save pipeline writes raw images, annotations, JSON logs, and the
contact sheet under `saves/<date>/`.
