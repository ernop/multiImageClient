#!/usr/bin/env bash
# Cloud Agent environment bootstrap for MultiImageClient.
# Idempotent: safe to run repeatedly. Installs the .NET 10 SDK, the `ss`
# tool the local-UI launcher needs, creates a keyless settings.json when
# absent, then restores and builds the solution.
set -Eeuo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_DIR="$HOME/.dotnet"
DOTNET_CHANNEL="10.0"

echo "==> MultiImageClient environment setup (repo: $REPO_ROOT)"

# 1. System package: iproute2 provides `ss`, required by tools/restart-local-ui.sh.
if ! command -v ss >/dev/null 2>&1; then
    echo "==> Installing iproute2 (provides ss)"
    sudo apt-get update -y
    sudo apt-get install -y --no-install-recommends iproute2
fi

# 2. .NET 10 SDK (framework-dependent apps need the matching runtime too).
if ! "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
    echo "==> Installing .NET $DOTNET_CHANNEL SDK into $DOTNET_DIR"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_DIR"
fi

export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Expose dotnet on PATH for every future shell and terminal.
sudo ln -sf "$DOTNET_DIR/dotnet" /usr/local/bin/dotnet

# 3. Keyless settings.json (gitignored). Only LogFilePath and
#    ImageDownloadBaseFolder are required; provider keys stay blank, so every
#    remote target shows as unconfigured instead of failing at call time.
SETTINGS="$REPO_ROOT/MultiImageClient/settings.json"
if [ ! -f "$SETTINGS" ]; then
    echo "==> Writing keyless $SETTINGS"
    cat > "$SETTINGS" <<JSON
{
  "PromptFiles": [],
  "LoadPromptsFrom": "",
  "IdeogramApiKey": "",
  "OpenAIApiKey": "",
  "BFLApiKey": "",
  "KreaApiKey": "",
  "AnthropicApiKey": "",
  "RecraftApiKey": "",
  "XAIGrokApiKey": "",
  "GrokWebCookiePath": "",
  "GrokWebStatsigVerificationKey": "",
  "GrokWebStatsigAnimationKey": "",
  "GrokWebBrowserExecutablePath": "",
  "GrokWebBrowserHeaded": false,
  "MetaWebCookiePath": "",
  "MetaWebBrowserProfilePath": "",
  "MetaWebBrowserExecutablePath": "",
  "MetaWebHeaded": false,
  "GoogleGeminiApiKey": "",
  "GoogleCloudApiKey": "",
  "GoogleCloudLocation": "",
  "GoogleCloudProjectId": "",
  "GoogleServiceAccountKeyPath": "",
  "ImageDownloadBaseFolder": "$REPO_ROOT/saves",
  "LogFilePath": "$REPO_ROOT/logs/mic.log",
  "SaveJsonLog": true,
  "EnableGenerationArchive": true,
  "GenerationArchiveDbPath": "",
  "EnableLogging": true,
  "AnnotationSide": "right",
  "FlatImageMirrorPath": "",
  "TypedPromptsAppendFile": "",
  "UiMaxConcurrentJobs": 4,
  "UiMaxConcurrentGenerators": 20,
  "UiMaxPendingJobs": 64,
  "EnableB2ImageHosting": false,
  "B2KeepLocalRawImages": true,
  "EnableLocalGenerators": false
}
JSON
fi

# 4. Restore + build the whole solution (Release).
echo "==> Restoring NuGet packages"
dotnet restore "$REPO_ROOT/MultiImageClient.sln"
echo "==> Building solution (Release)"
dotnet build "$REPO_ROOT/MultiImageClient.sln" -c Release --no-restore

echo "==> MultiImageClient environment setup complete"
