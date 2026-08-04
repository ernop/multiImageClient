#!/usr/bin/env bash
set -Eeuo pipefail

# Adjust shared-host finalization and aggregate request concurrency, then restart.
# Usage: sudo bash ~/set-ui-concurrency.sh [finalizers] [requests]
# Defaults: 1 finalizer, leave aggregate requests unchanged (or pass both).

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || die "run with sudo"

settings=/etc/multiimageclient/settings.json
[[ -f $settings ]] || die "missing $settings"
command -v jq >/dev/null || die "jq is required"

jobs=${1:-1}
gens=${2:-}

[[ $jobs =~ ^[0-9]+$ ]] && (( jobs >= 1 && jobs <= 32 )) \
    || die "finalizers must be an integer 1..32 (got: $jobs)"

tmp=$(mktemp)
if [[ -n $gens ]]; then
    [[ $gens =~ ^[0-9]+$ ]] && (( gens >= 1 && gens <= 32 )) \
        || die "requests must be an integer 1..32 (got: $gens)"
    jq --argjson j "$jobs" --argjson g "$gens" \
        '.UiMaxConcurrentJobs = $j | .UiMaxConcurrentGenerators = $g' \
        "$settings" >"$tmp"
else
    jq --argjson j "$jobs" '.UiMaxConcurrentJobs = $j' "$settings" >"$tmp"
fi
install -o root -g multiimageclient -m 0640 "$tmp" "$settings"
rm -f "$tmp"

systemctl restart multiimageclient-ui
sleep 1
systemctl is-active multiimageclient-ui >/dev/null \
    || die "multiimageclient-ui failed to become active"

printf 'OK service=%s\n' "$(systemctl is-active multiimageclient-ui)"
jq '{UiMaxConcurrentJobs, UiMaxConcurrentGenerators}' "$settings"
