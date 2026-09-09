#!/usr/bin/env bash
set -Eeuo pipefail
[[ $(id -u) == 0 ]] || { echo 'Run as root.' >&2; exit 1; }
repo=/home/tparkour/multiImageClient
target=/usr/local/lib/multiimageclient-control
install -d -m 0755 "$target/deploy" "$target/MultiImageClient"
install -m 0644 "$repo/deploy/environment-controller.py" "$target/deploy/environment-controller.py"
install -m 0644 "$repo/deploy/create-environment.py" "$target/deploy/create-environment.py"
install -m 0644 "$repo/MultiImageClient/settings - Fill this in and rename it.json" "$target/MultiImageClient/settings - Fill this in and rename it.json"
if [[ ! -d /var/lib/multiimageclient-control ]]; then
    python3 "$target/deploy/environment-controller.py" --initialize
fi
cat > /etc/systemd/system/multiimageclient-environment-controller.service <<'UNIT'
[Unit]
Description=Provision explicitly requested MultiImageClient environments
ConditionPathExists=/var/lib/multiimageclient-control/state/registry.json
[Service]
Type=oneshot
User=root
UMask=0077
ExecStart=/usr/bin/python3 /usr/local/lib/multiimageclient-control/deploy/environment-controller.py
UNIT
cat > /etc/systemd/system/multiimageclient-environment-controller.timer <<'UNIT'
[Unit]
Description=Check requested MultiImageClient environment changes
[Timer]
OnBootSec=30s
OnUnitActiveSec=30s
Unit=multiimageclient-environment-controller.service
[Install]
WantedBy=timers.target
UNIT
systemctl daemon-reload
echo 'Controller prepared. Release the original application, then enable its environment-controller timer.'
