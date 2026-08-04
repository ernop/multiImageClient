#!/usr/bin/env bash
set -Eeuo pipefail

# Redeploy a pre-built publish staging tree into /opt and restart the UI.
# Run after: git pull && dotnet publish … -o ~/multiimageclient-publish-staging
# Usage: sudo bash deploy/update-shared-host.sh

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

[[ $EUID -eq 0 ]] || die "run with sudo"

owner=${SUDO_USER:-tparkour}
owner_home=$(getent passwd "$owner" | cut -d: -f6)
[[ -n $owner_home && -d $owner_home ]] || die "cannot resolve deploying user's home"

publish_staging="$owner_home/multiimageclient-publish-staging"
[[ -f $publish_staging/MultiImageClient.dll ]] \
    || die "missing staged publish at $publish_staging (dotnet publish first)"
[[ -f $publish_staging/Ui/wwwroot/index.html ]] \
    || die "staged publish is missing Ui/wwwroot"

rsync -a --delete "$publish_staging/" /opt/multiimageclient/
chown -R root:root /opt/multiimageclient

# Under memory pressure the old process often ignores SIGTERM and leaves
# systemd stuck in deactivating — kill hard, then start clean.
systemctl kill -s SIGKILL multiimageclient-ui 2>/dev/null || true
sleep 1
systemctl reset-failed multiimageclient-ui 2>/dev/null || true
systemctl start multiimageclient-ui
sleep 2
systemctl is-active multiimageclient-ui >/dev/null \
    || die "multiimageclient-ui failed to become active"

rm -rf "$publish_staging"
printf 'MultiImageClient updated in /opt and multiimageclient-ui restarted.\n'
printf 'service=%s pid=%s\n' \
    "$(systemctl is-active multiimageclient-ui)" \
    "$(systemctl show -p MainPID --value multiimageclient-ui)"
timeout 5 curl -sS -o /dev/null -w 'http=%{http_code} t=%{time_total}\n' http://127.0.0.1:5960/ \
    || printf 'http probe failed (may still be warming up)\n'
grep -c ram-status /opt/multiimageclient/Ui/wwwroot/index.html \
    || printf 'WARN: ram-status marker missing from wwwroot\n'
