# Local FLUX.2 Klein 4B via ComfyUI

This is the local/open-weight path for FLUX.2 Klein 4B. It is separate from the
BFL hosted API and does not use `BFLApiKey`.

## Model Pieces

Install or update ComfyUI, then install the FLUX.2 Klein 4B text-to-image
workflow from ComfyUI's workflow templates.

Model files expected by the standard ComfyUI FLUX.2 Klein 4B workflow:

- `ComfyUI/models/diffusion_models/flux-2-klein-4b-fp8.safetensors`
- `ComfyUI/models/vae/flux2-vae.safetensors`
- text encoder:
  - default: `ComfyUI/models/text_encoders/qwen_3_4b.safetensors`
  - custom GGUF/safetensors encoders require a matching ComfyUI loader node.

The diffusion model/UNET is the denoiser: it turns latent noise into an image
conditioned on text embeddings. The text encoder converts your prompt into those
conditioning embeddings. The VAE decodes final latents back into pixels. A LoRA
is a small adapter loaded on top of the diffusion model and sometimes the text
encoder; it shifts style, subject knowledge, composition habits, or domain
details without replacing the base model.

LoRA strength is normally split:

- `strength_model`: how hard the LoRA changes the diffusion/UNET behavior.
  Start around `0.5`-`0.9`; higher values can overpower composition or produce
  artifacts.
- `strength_clip`: how hard the LoRA changes prompt/text-encoder behavior.
  Start at the same value as model strength, then lower it if prompts become too
  literal or unstable.

## Workflow Contract

Export the ComfyUI workflow in API format and save it somewhere stable, outside
`saves/`. In the positive prompt field, put this placeholder:

```text
{{PROMPT}}
```

Optional: put `{{SEED}}` in any string field where you want this client to insert
a random seed. If your seed field is numeric, leave it fixed or randomize it in
the ComfyUI workflow.

Optional model placeholders can be placed directly in loader node string fields:

- UNET / diffusion loader: `{{UNET}}` or `{{DIFFUSION_MODEL}}`
- checkpoint loader: `{{CHECKPOINT}}` or `{{CKPT}}`
- VAE loader: `{{VAE}}`
- text encoder loader: `{{TEXT_ENCODER}}`, `{{TEXT_ENCODER1}}`, or `{{CLIP}}`
- second text encoder loader: `{{TEXT_ENCODER2}}` or `{{CLIP2}}`
- LoRA loader: `{{LORA}}`
- LoRA strengths: `{{LORA_STRENGTH_MODEL}}`, `{{LORA_STRENGTH_CLIP}}`

If any `{{...}}` placeholder remains unresolved after settings are applied, the
C# generator fails before queueing the ComfyUI prompt. That catches misspelled
settings or half-edited workflows early.

Then set these in `MultiImageClient/settings.json`:

```json
{
  "ComfyUIBaseUrl": "http://127.0.0.1:8188",
  "ComfyUIWorkflowPath": "C:\\path\\to\\flux2-klein-4b-api.json",
  "ComfyUIWorkflowName": "flux2-klein-custom",
  "ComfyUIDiffusionModelName": "flux-2-klein-4b-fp8.safetensors",
  "ComfyUIVaeName": "flux2-vae.safetensors",
  "ComfyUITextEncoderName": "qwen_3_4b.safetensors",
  "ComfyUILoraName": "your-adapter.safetensors",
  "ComfyUILoraModelStrength": 0.8,
  "ComfyUILoraClipStrength": 0.8,
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
