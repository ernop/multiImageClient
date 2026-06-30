# Local Z-Image via ComfyUI

This is the local/open-weight path for Alibaba/Tongyi-MAI `Z-Image` and
`Z-Image-Turbo`. It runs through your local ComfyUI server and does not use any
hosted provider API key.

## Model Pieces

Install or update ComfyUI, then install a Z-Image-compatible workflow or node
pack. The repo exposes the model weights under names such as:

- `Tongyi-MAI/Z-Image`
- `Tongyi-MAI/Z-Image-Turbo`
- `Tongyi-MAI/Z-Image-Edit`

`Z-Image-Turbo` is the practical default for fast local generation. It is the
distilled variant designed for a small number of denoising steps; the base
`Z-Image` checkpoint is more appropriate for slower quality runs or fine-tuning.

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
C# generator fails before queueing the ComfyUI prompt.

Then set these in `MultiImageClient/settings.json`:

```json
{
  "ComfyUIBaseUrl": "http://127.0.0.1:8188",
  "ComfyUIZImageWorkflowPath": "C:\\path\\to\\z-image-turbo-api.json",
  "ComfyUIZImageWorkflowName": "z-image-turbo",
  "ComfyUIZImageDiffusionModelName": "your-z-image-model.safetensors",
  "ComfyUIZImageVaeName": "your-vae.safetensors",
  "ComfyUIZImageTextEncoderName": "your-text-encoder.safetensors",
  "ComfyUIPollIntervalMs": 1000,
  "ComfyUITimeoutSeconds": 900
}
```

If your workflow hardcodes the model filenames, leave the `ComfyUIZImage*Name`
fields blank and only set `ComfyUIZImageWorkflowPath`.

## Running

Start ComfyUI first. Then run only the local Z-Image provider:

```powershell
dotnet run --project MultiImageClient/MultiImageClient.csproj -- `
  --provider-sample-showcase `
  --provider-sample-file test-new.txt `
  --limit 8 `
  --provider-sample-providers "z-image"
```

Use `--local-size WxH` to override the workflow's latent size. Valid sizes are
the same local ComfyUI set used by the FLUX.2 Klein path, for example
`1024x1024`, `1536x1024`, or `1024x1536`.

The normal save pipeline writes raw images, annotations, JSON logs, and the
contact sheet under `saves/<date>/`.
