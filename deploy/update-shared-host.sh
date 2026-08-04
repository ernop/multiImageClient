#!/usr/bin/env bash
set -Eeuo pipefail

# Redeploy a pre-built publish staging tree into /opt and restart the UI.
# Run after: git pull && dotnet publish … -o ~/multiimageclient-publish-staging
# Usage (as root, or via passwordless sudo after install-agent-deploy.sh):
#   sudo /usr/local/sbin/multiimageclient-update
#   sudo bash deploy/update-shared-host.sh
#
# Agents: publish staging first (or run deploy/agent-redeploy.sh), then this.

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
grep -q ram-status "$publish_staging/Ui/wwwroot/index.html" \
    || die "staged publish is missing ram-status marker — wrong build?"

printf 'Installing staged publish into /opt/multiimageclient …\n'
rsync -a --delete "$publish_staging/" /opt/multiimageclient/
chown -R root:root /opt/multiimageclient

# Wedged-under-memory processes often ignore SIGTERM. systemctl kill -s SIGKILL
# also sometimes prints "failed to send signal SIGKILL to auxiliary processes:
# Invalid argument" even when the main PID dies — ignore that and kill by PID.
old_pid=$(systemctl show -p MainPID --value multiimageclient-ui 2>/dev/null || true)
systemctl stop multiimageclient-ui 2>/dev/null || true
if [[ -n ${old_pid:-} && $old_pid != 0 ]] && kill -0 "$old_pid" 2>/dev/null; then
    kill -9 "$old_pid" 2>/dev/null || true
    sleep 1
fi
# Catch anything still bound to the port.
if ss -lntp 2>/dev/null | grep -q ':5960'; then
    fuser -k 5960/tcp 2>/dev/null || true
    sleep 1
fi
systemctl reset-failed multiimageclient-ui 2>/dev/null || true
systemctl start multiimageclient-ui
sleep 2
systemctl is-active multiimageclient-ui >/dev/null \
    || die "multiimageclient-ui failed to become active"

rm -rf "$publish_staging"

printf 'OK.\n'
printf '  service=%s pid=%s\n' \
    "$(systemctl is-active multiimageclient-ui)" \
    "$(systemctl show -p MainPID --value multiimageclient-ui)"
grep -c ram-status /opt/multiimageclient/Ui/wwwroot/index.html \
    | awk '{print "  ram-status markers in wwwroot: "$1}'
timeout 5 curl -sS -o /dev/null -w '  http=%{http_code} t=%{time_total}\n' http://127.0.0.1:5960/ \
    || printf '  http probe failed (may still be warming up)\n'
