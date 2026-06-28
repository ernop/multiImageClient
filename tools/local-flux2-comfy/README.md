# Local Flux2 Klein uncensored ComfyUI setup

`flux2 uncensored` is not a separate hosted image API. The available path is a
local Flux2 Klein ComfyUI workflow using an ablated/uncensored Qwen text encoder.
The text encoder repository is gated on Hugging Face and must be accepted by the
user before download.

## Start ComfyUI

From the repo root:

```bash
tools/local-flux2-comfy/setup_local_flux2_comfyui.sh
```

The script:

- clones or updates ComfyUI under `$HOME/ComfyUI` by default;
- creates a Python virtual environment and installs ComfyUI requirements;
- installs ComfyUI-Manager and ComfyUI-GGUF by default;
- binds ComfyUI to `127.0.0.1:8188` unless overridden;
- does not download the gated uncensored text encoder unless explicitly asked.

Useful environment variables:

```bash
COMFYUI_DIR="$HOME/ComfyUI"                 # install location
COMFYUI_HOST=127.0.0.1                      # keep local by default
COMFYUI_PORT=8188
DOWNLOAD_UNCENSORED_ENCODER=1               # after accepting Hugging Face terms
HF_TOKEN=hf_...                             # if your Hugging Face CLI is not already logged in
FLUX2_MODEL_REPO=... FLUX2_MODEL_FILE=...   # optional model download
FLUX2_VAE_REPO=... FLUX2_VAE_FILE=...       # optional VAE download
START_SERVER=0                              # install only, do not launch ComfyUI
```

## Workflow requirements

Build the Flux2 Klein workflow in ComfyUI, point it at the ablated text encoder,
then export it with **Save (API Format)**. Set:

```json
"LocalFlux2ComfyEndpoint": "http://127.0.0.1:8188",
"LocalFlux2WorkflowPath": "/home/you/ComfyUI/user/default/workflows/flux2_uncensored_api.json",
"LocalFlux2TextEncoderName": "qwen3-4b-abl-q4_0.gguf",
"LocalFlux2Width": 1024,
"LocalFlux2Height": 1024,
"LocalFlux2Steps": 28,
"LocalFlux2Guidance": 1.0
```

If auto-detection cannot find the right prompt node, set
`LocalFlux2PositivePromptNodeId`. If a custom loader uses nonstandard input
names, use `LocalFlux2WorkflowInputOverrides` with exact ComfyUI node ids:

```json
"LocalFlux2WorkflowInputOverrides": {
  "12": {
    "unet_name": "flux2-klein-4b.gguf"
  },
  "13": {
    "clip_name": "qwen3-4b-abl-q4_0.gguf"
  }
}
```

## Use from MultiImageClient

In REPL mode:

```text
:gens add flux2local
```

For batch mode, uncomment `LocalFlux2Uncensored_Square()` in
`GeneratorGroups.GetAll()`.

The generator submits prompts to ComfyUI `/prompt`, polls `/history/{prompt_id}`,
downloads completed `/view` images, and sends the bytes through the existing
MultiImageClient save/annotation pipeline. By default it refuses non-local
ComfyUI endpoints; set `LocalFlux2AllowRemoteEndpoint` only for a trusted LAN
host.
