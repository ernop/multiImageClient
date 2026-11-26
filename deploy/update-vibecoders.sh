#!/usr/bin/env bash
set -Eeuo pipefail

# Second half of every production release. Run the root-owned installed copy
# after agent-redeploy.sh has installed and verified the original (AVE) service.
# It installs that same build from /opt/multiimageclient into the Vibecoders
# instance and verifies it. Output never includes private paths, credentials,
# or cookies.
#
# From the current development workstation:
#   ssh tpbeta-root \
#     '/usr/local/lib/multiimageclient-update/update-vibecoders.sh [--wait-minutes N]'

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

wait_minutes=0
if [[ "${1:-}" == "--wait-minutes" && "${2:-}" =~ ^[0-9]+$ ]]; then
    wait_minutes=$2
    shift 2
fi
(( $# == 0 )) || die "usage: update-vibecoders.sh [--wait-minutes N]"

id=vibecoders-ai-generation
unit=multiimageclient-env-$id
install=/opt/$unit
manifest=/etc/$unit/manifest.json
publish=/opt/multiimageclient
repo=/home/tparkour/multiImageClient
checker=/usr/local/lib/multiimageclient-update/check-unfinished-work.py
env_tool=/usr/local/lib/multiimageclient-update/create-environment.py
vibecoders=/usr/local/lib/multiimageclient-update/update-vibecoders.sh

[[ $EUID -eq 0 ]] || die "run as root"
[[ -f $manifest ]] || die "missing $manifest"
[[ -f $publish/MultiImageClient.dll ]] || die "missing the original's installed build"
[[ -f $checker && ! -L $checker ]] || die "missing $checker — rerun install-agent-deploy.sh as root"
[[ -f $env_tool && ! -L $env_tool ]] || die "missing $env_tool — rerun install-agent-deploy.sh as root"
[[ -x $vibecoders && ! -L $vibecoders ]] || die "missing $vibecoders — rerun install-agent-deploy.sh as root"

cmp -s "$vibecoders" "$repo/deploy/update-vibecoders.sh" \
    || die "$vibecoders differs from deploy/update-vibecoders.sh — as root run: bash $repo/deploy/install-agent-deploy.sh, then rerun"
cmp -s "$checker" "$repo/deploy/check-unfinished-work.py" \
    || die "$checker differs from deploy/check-unfinished-work.py — as root run: bash $repo/deploy/install-agent-deploy.sh, then rerun"
cmp -s "$env_tool" "$repo/deploy/create-environment.py" \
    || die "$env_tool differs from deploy/create-environment.py — as root run: bash $repo/deploy/install-agent-deploy.sh, then rerun"

# A restart kills in-flight work, so this exits 75 without changes while
# Vibecoders has unfinished jobs or goal loops.
/usr/bin/python3 -I "$checker" --wait-minutes "$wait_minutes" "$unit"

/usr/bin/python3 -I "$env_tool" update --id "$id" --publish "$publish"

if ! differences=$(diff -rq --exclude=settings.json "$publish" "$install" 2>&1); then
    printf '%s\n' "${differences:0:2000}" >&2
    die "the Vibecoders install differs from the original's"
fi
printf 'checkout=%s\n' "$(sudo -u tparkour -H git -C "$repo" rev-parse --short HEAD)"
printf 'build=%s, identical in both instances\n' "$(sha256sum "$install/MultiImageClient.dll" | cut -c1-12)"
printf 'service=%s since %s\n' "$(systemctl is-active "$unit")" \
    "$(systemctl show "$unit" -p ActiveEnterTimestamp --value)"
pid=$(systemctl show "$unit" -p MainPID --value)
[[ $pid =~ ^[1-9][0-9]*$ ]] || die "$unit has no running process"
gc_limit=$(tr '\0' '\n' < "/proc/$pid/environ" | sed -n 's/^DOTNET_GCHeapHardLimit=//p')
[[ $gc_limit =~ ^0x[0-9a-f]+$ ]] || die "the running $unit has no GC heap limit"
printf 'gc_heap_limit=%d MiB\n' "$(( gc_limit / 1048576 ))"

/usr/bin/python3 -I - "$manifest" <<'PY'
import json, sys, urllib.error, urllib.request
with open(sys.argv[1], encoding="utf-8") as f:
    manifest = json.load(f)
def status(url):
    try:
        with urllib.request.urlopen(url, timeout=15) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code
    except OSError as error:
        return type(error).__name__
loopback = status(f"http://127.0.0.1:{manifest['port']}/healthz")
route = status("https://multiimageclient.alpha.fuseki.net/" + manifest["privatePath"].strip("/") + "/")
print(f"loopback_health={loopback} private_route={route}")
if (loopback, route) != (200, 401):
    sys.exit("ERROR: expected loopback health 200 and the login gate's 401")
PY
