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
#     'bash /home/tparkour/multiImageClient/deploy/update-vibecoders.sh'

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

id=vibecoders-ai-generation
unit=multiimageclient-env-$id
install=/opt/$unit
manifest=/etc/$unit/manifest.json
publish=/opt/multiimageclient
repo=/home/tparkour/multiImageClient

[[ $EUID -eq 0 ]] || die "run as root"
[[ -f $manifest ]] || die "missing $manifest"
[[ -f $publish/MultiImageClient.dll ]] || die "missing the original's installed build"

settings=$(systemctl show "$unit" -p Environment | tr ' ' '\n' \
    | sed -n 's/^\(Environment=\)\?MULTIIMAGECLIENT_SETTINGS=//p')
[[ -f $settings ]] || die "cannot locate the $unit settings file"

# A restart kills in-flight work, so abort instead and rerun after it finishes.
python3 - "$settings" <<'PY'
import glob, json, os, sys, time
with open(sys.argv[1], encoding="utf-8") as f:
    base = json.load(f)["ImageDownloadBaseFolder"]
cutoff = time.time() - 15 * 60
busy = 0
for events in glob.glob(os.path.join(base, "UiHistory", "*", "events.jsonl")):
    if os.path.getmtime(events) >= cutoff:
        with open(events, encoding="utf-8") as f:
            if not any(json.loads(line).get("type") == "job-done" for line in f if line.strip()):
                busy += 1
for loop in glob.glob(os.path.join(base, "UiGoalLoops", "*", "loop.json")):
    if os.path.getmtime(loop) >= cutoff:
        with open(loop, encoding="utf-8") as f:
            if str(json.load(f).get("status")).lower() in ("running", "starting"):
                busy += 1
if busy:
    sys.exit(f"BUSY: {busy} unfinished Vibecoders job(s) or goal loop(s); nothing changed. Rerun after they finish.")
print("pre-check: no unfinished jobs or goal loops")
PY

# create-environment.py refuses to run while .previous exists. Keep that
# recovery copy, named by the build time of the release it holds.
if [[ -e $install.previous ]]; then
    kept=$install.previous-$(date -u -r "$install.previous/MultiImageClient.dll" +%Y%m%dT%H%MZ)
    [[ ! -e $kept ]] || die "${kept##*/} already exists"
    mv "$install.previous" "$kept"
    printf 'kept recovery copy %s\n' "${kept##*/}"
fi

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
