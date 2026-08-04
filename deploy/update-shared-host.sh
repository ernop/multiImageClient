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

# Optional: disk usage for deploy + generated images (passwordless via the
# agent helper). Does not install or restart anything.
if [[ "${1:-}" == "disk-report" ]]; then
    echo "=== filesystem ==="
    df -h /
    echo
    echo "=== deploy (/opt/multiimageclient) ==="
    du -sh /opt/multiimageclient
    du -sh /opt/multiimageclient/.playwright 2>/dev/null || true
    echo
    echo "=== data (/var/lib/multiimageclient) ==="
    du -sh /var/lib/multiimageclient
    du -sh /var/lib/multiimageclient/* 2>/dev/null | sort -h
    echo
    if [[ -d /var/lib/multiimageclient/saves ]]; then
        echo "=== saves top-level ==="
        du -sh /var/lib/multiimageclient/saves/* 2>/dev/null | sort -h | tail -40
        echo
        if [[ -d /var/lib/multiimageclient/saves/UiHistory ]]; then
            echo "=== UiHistory ==="
            du -sh /var/lib/multiimageclient/saves/UiHistory
            find /var/lib/multiimageclient/saves/UiHistory -mindepth 1 -maxdepth 1 -type d 2>/dev/null \
                | wc -l | awk '{print "job folders: "$1}'
        fi
    fi
    exit 0
fi

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

# Playwright's bundled node/chromium must be world-executable: the service
# runs as multiimageclient while /opt is root:root. rsync can skip unchanged
# files and leave a stale mode (observed: 764 → EACCES "Permission denied"
# starting …/.playwright/node/linux-x64/node). Always re-apply after install.
if [[ -d /opt/multiimageclient/.playwright ]]; then
    chmod -R a+rX /opt/multiimageclient/.playwright
    find /opt/multiimageclient/.playwright -type f \( -name node -o -name chrome -o -name chromium -o -name 'chrome-headless-shell' -o -name ffmpeg \) \
        -exec chmod a+x {} +
fi

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
