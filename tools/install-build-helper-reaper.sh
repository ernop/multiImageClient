#!/usr/bin/env bash
set -Eeuo pipefail

# Installs and starts the systemd user timer that runs
# tools/reap-build-helpers.sh every 10 minutes on this workstation.
# Rerun after moving the repository. See docs/build-helper-processes.md.

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
units="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
mkdir -p "$units"

cat > "$units/dotnet-build-helper-reaper.service" <<EOF
[Unit]
Description=Stop orphaned .NET build helper processes
Documentation=file://$repo/docs/build-helper-processes.md

[Service]
Type=oneshot
ExecStart=$repo/tools/reap-build-helpers.sh
EOF

cat > "$units/dotnet-build-helper-reaper.timer" <<EOF
[Unit]
Description=Stop orphaned .NET build helpers every 10 minutes
Documentation=file://$repo/docs/build-helper-processes.md

[Timer]
OnBootSec=10min
OnUnitActiveSec=10min

[Install]
WantedBy=timers.target
EOF

systemctl --user daemon-reload
systemctl --user enable --now dotnet-build-helper-reaper.timer
systemctl --user list-timers dotnet-build-helper-reaper.timer --no-pager
