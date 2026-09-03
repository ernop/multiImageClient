#!/usr/bin/env bash
set -Eeuo pipefail

# Restart the workstation local `--ui` on 127.0.0.1:5960 from this checkout.
# Production uses deploy/agent-redeploy.sh. tools/start-ui.sh is the Surface
# WSL launcher and must not be used here.

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
log="${MIC_UI_LOCAL_LOG:-/tmp/mic-ui-local.log}"
port="${MIC_UI_PORT:-5960}"
export DOTNET_EnableWriteXorExecute="${DOTNET_EnableWriteXorExecute:-0}"

dotnet_bin="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
if [[ ! -x $dotnet_bin ]]; then
    dotnet_bin="$(command -v dotnet || true)"
fi
[[ -n ${dotnet_bin} && -x $dotnet_bin ]] || {
    printf 'ERROR: dotnet not found\n' >&2
    exit 1
}

stop_ui() {
    if command -v fuser >/dev/null 2>&1; then
        fuser -k -TERM "${port}/tcp" 2>/dev/null || true
    fi
    pkill -TERM -f 'MultiImageClient.*--ui' 2>/dev/null || true
    local i
    for i in $(seq 1 20); do
        if ! ss -ltn 2>/dev/null | grep -q ":${port} "; then
            return 0
        fi
        sleep 0.25
    done
    if command -v fuser >/dev/null 2>&1; then
        fuser -k -KILL "${port}/tcp" 2>/dev/null || true
    fi
    pkill -KILL -f 'MultiImageClient.*--ui' 2>/dev/null || true
    sleep 0.5
}

stop_ui
"$dotnet_bin" build MultiImageClient/MultiImageClient.csproj -c Release --nologo

nohup "$dotnet_bin" run \
    --project MultiImageClient/MultiImageClient.csproj \
    -c Release --no-build -- \
    --ui --ui-port "$port" --ui-no-open \
    >>"$log" 2>&1 &
printf '%s\n' "$!" > /tmp/mic-ui-local.pid

i=0
while [[ $i -lt 60 ]]; do
    if curl -sf -o /dev/null "http://127.0.0.1:${port}/healthz"; then
        printf 'local UI listening on 127.0.0.1:%s pid=%s\n' \
            "$port" "$(cat /tmp/mic-ui-local.pid)"
        exit 0
    fi
    i=$((i + 1))
    sleep 1
done

printf 'ERROR: local UI did not become healthy; see %s\n' "$log" >&2
exit 1
