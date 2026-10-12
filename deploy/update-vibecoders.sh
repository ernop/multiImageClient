#!/usr/bin/env bash
set -Eeuo pipefail

# Second half of every production release. Run it as root after
# agent-redeploy.sh has installed and verified the original (AVE) service. It
# installs that same build from /opt/multiimageclient into the Vibecoders
# instance and verifies it. Output never includes private paths, credentials,
# or cookies.
#
# From the current development workstation:
#   ssh tpbeta-root \
#     'bash /home/tparkour/multiImageClient/deploy/update-vibecoders.sh [--wait-minutes N]'

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

[[ $EUID -eq 0 ]] || die "run as root"
[[ -f $manifest ]] || die "missing $manifest"
[[ -f $publish/MultiImageClient.dll ]] || die "missing the original's installed build"

# A restart kills in-flight work, so this exits 75 without changes while
# Vibecoders has unfinished jobs or goal loops.
python3 "$repo/deploy/check-unfinished-work.py" --wait-minutes "$wait_minutes" "$unit"

python3 "$repo/deploy/create-environment.py" update --id "$id" --publish "$publish"

if ! differences=$(diff -rq --exclude=settings.json "$publish" "$install" 2>&1); then
    printf '%s\n' "${differences:0:2000}" >&2
    die "the Vibecoders install differs from the original's"
fi
printf 'checkout=%s\n' "$(sudo -u tparkour -H git -C "$repo" rev-parse --short HEAD)"
printf 'build=%s, identical in both instances\n' "$(sha256sum "$install/MultiImageClient.dll" | cut -c1-12)"
printf 'service=%s since %s\n' "$(systemctl is-active "$unit")" \
    "$(systemctl show "$unit" -p ActiveEnterTimestamp --value)"

python3 - "$manifest" <<'PY'
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
