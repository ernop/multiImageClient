#!/usr/bin/env bash
set -euo pipefail

COMFYUI_DIR="${COMFYUI_DIR:-$HOME/ComfyUI}"
COMFYUI_HOST="${COMFYUI_HOST:-127.0.0.1}"
COMFYUI_PORT="${COMFYUI_PORT:-8188}"
PYTHON_BIN="${PYTHON_BIN:-python3}"
INSTALL_CUSTOM_NODES="${INSTALL_CUSTOM_NODES:-1}"
DOWNLOAD_UNCENSORED_ENCODER="${DOWNLOAD_UNCENSORED_ENCODER:-0}"
START_SERVER="${START_SERVER:-1}"

UNCENSORED_ENCODER_REPO="${UNCENSORED_ENCODER_REPO:-Cordux/flux2-klein-4B-uncensored-text-encoder}"
UNCENSORED_ENCODER_FILE="${UNCENSORED_ENCODER_FILE:-qwen3-4b-abl-q4_0.gguf}"

FLUX2_MODEL_REPO="${FLUX2_MODEL_REPO:-}"
FLUX2_MODEL_FILE="${FLUX2_MODEL_FILE:-}"
FLUX2_VAE_REPO="${FLUX2_VAE_REPO:-}"
FLUX2_VAE_FILE="${FLUX2_VAE_FILE:-}"
HF_TOKEN="${HF_TOKEN:-}"

need_cmd() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "Missing required command: $1" >&2
        exit 1
    fi
}

clone_or_update() {
    local repo="$1"
    local dest="$2"
    if [ -d "$dest/.git" ]; then
        echo "Updating $dest"
        git -C "$dest" pull --ff-only
    else
        echo "Cloning $repo -> $dest"
        git clone "$repo" "$dest"
    fi
}

install_hf_cli() {
    if ! command -v huggingface-cli >/dev/null 2>&1; then
        python -m pip install -U huggingface_hub
    fi
}

download_hf_file() {
    local repo="$1"
    local file="$2"
    local dest="$3"
    if [ -z "$repo" ] || [ -z "$file" ]; then
        return
    fi

    mkdir -p "$dest"
    if [ -f "$dest/$file" ]; then
        echo "Already present: $dest/$file"
        return
    fi

    install_hf_cli
    echo "Downloading $repo/$file -> $dest"
    if [ -n "$HF_TOKEN" ]; then
        huggingface-cli download "$repo" "$file" --local-dir "$dest" --token "$HF_TOKEN"
    else
        huggingface-cli download "$repo" "$file" --local-dir "$dest"
    fi
}

need_cmd git
need_cmd "$PYTHON_BIN"

if [ "$COMFYUI_HOST" != "127.0.0.1" ] && [ "$COMFYUI_HOST" != "localhost" ]; then
    echo "Warning: COMFYUI_HOST=$COMFYUI_HOST exposes ComfyUI beyond localhost. Only do this on a trusted network." >&2
fi

clone_or_update "https://github.com/comfyanonymous/ComfyUI.git" "$COMFYUI_DIR"

cd "$COMFYUI_DIR"
"$PYTHON_BIN" -m venv .venv
# shellcheck source=/dev/null
source .venv/bin/activate
python -m pip install -U pip wheel
python -m pip install -r requirements.txt

if [ "$INSTALL_CUSTOM_NODES" = "1" ]; then
    mkdir -p custom_nodes
    clone_or_update "https://github.com/ltdrdata/ComfyUI-Manager.git" "custom_nodes/ComfyUI-Manager"
    clone_or_update "https://github.com/city96/ComfyUI-GGUF.git" "custom_nodes/ComfyUI-GGUF"
    if [ -f "custom_nodes/ComfyUI-GGUF/requirements.txt" ]; then
        python -m pip install -r "custom_nodes/ComfyUI-GGUF/requirements.txt"
    fi
fi

mkdir -p models/diffusion_models models/text_encoders models/vae user/default/workflows

if [ "$DOWNLOAD_UNCENSORED_ENCODER" = "1" ]; then
    echo "Downloading the uncensored text encoder requires that you have accepted the model terms on Hugging Face."
    download_hf_file "$UNCENSORED_ENCODER_REPO" "$UNCENSORED_ENCODER_FILE" "$COMFYUI_DIR/models/text_encoders"
else
    echo "Skipping uncensored text encoder download."
    echo "Set DOWNLOAD_UNCENSORED_ENCODER=1 after accepting the Hugging Face model terms to download $UNCENSORED_ENCODER_FILE."
fi

download_hf_file "$FLUX2_MODEL_REPO" "$FLUX2_MODEL_FILE" "$COMFYUI_DIR/models/diffusion_models"
download_hf_file "$FLUX2_VAE_REPO" "$FLUX2_VAE_FILE" "$COMFYUI_DIR/models/vae"

cat <<EOF

ComfyUI setup is ready.

Endpoint:
  http://$COMFYUI_HOST:$COMFYUI_PORT

MultiImageClient settings:
  "LocalFlux2ComfyEndpoint": "http://$COMFYUI_HOST:$COMFYUI_PORT",
  "LocalFlux2WorkflowPath": "$COMFYUI_DIR/user/default/workflows/flux2_uncensored_api.json",
  "LocalFlux2TextEncoderName": "$UNCENSORED_ENCODER_FILE"

Next:
  1. Build/load a Flux2 Klein workflow in ComfyUI.
  2. Select the ablated text encoder in the workflow.
  3. Export with "Save (API Format)" to:
     $COMFYUI_DIR/user/default/workflows/flux2_uncensored_api.json
  4. In MultiImageClient REPL, run:
     :gens add flux2local

EOF

if [ "$START_SERVER" = "1" ]; then
    exec python main.py --listen "$COMFYUI_HOST" --port "$COMFYUI_PORT" --disable-auto-launch
fi
