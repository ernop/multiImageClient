#!/usr/bin/env bash
set -Eeuo pipefail
# Verify shared-host grok-web after apply-grok-cookies-and-update.sh
# Does not print passwords or cookie contents.

creds=$HOME/multiimageclient-credentials.txt
[[ -f $creds ]] || { echo "missing $creds"; exit 1; }

url=$(awk -F= '/^URL=/{print $2; exit}' "$creds")
pass=$(awk -F= '/^ernie=/{print $2; exit}' "$creds")
[[ -n $url && -n $pass ]] || { echo "credentials file incomplete"; exit 1; }
base=${url%/}

echo "service=$(systemctl is-active multiimageclient-ui)"
echo "staging=$([[ -d $HOME/multiimageclient-publish-staging ]] && echo PRESENT || echo GONE)"
echo "home_cookie=$([[ -f $HOME/grok-web-cookies.txt ]] && echo PRESENT || echo GONE)"
echo "show_costs=$(grep -c show-costs /opt/multiimageclient/Ui/wwwroot/index.html || true)"

jar=$(mktemp)
trap 'rm -f "$jar"' EXIT

login_code=$(curl -sS -c "$jar" -b "$jar" -o /tmp/mic-login.json -w '%{http_code}' \
  -X POST "$base/api/auth/login" \
  -F "username=ernie" -F "password=$pass")
echo "login_http=$login_code"

config_code=$(curl -sS -c "$jar" -b "$jar" -o /tmp/mic-cfg.json -w '%{http_code}' \
  "$base/api/config")
echo "config_http=$config_code"

python3 - <<'PY'
import json
d = json.load(open("/tmp/mic-cfg.json"))
gens = d.get("generators") or []
for g in gens:
    key = str(g.get("key", ""))
    if "grok" in key.lower():
        problem = g.get("availabilityProblem") or g.get("problem") or ""
        print(f"{key} | {g.get('label')} | available={g.get('available')} | {problem}")
PY
