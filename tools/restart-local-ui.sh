#!/usr/bin/env bash
set -Eeuo pipefail

# Workstation launcher. Production uses deploy/agent-redeploy.sh.
exec python3 "$(dirname "${BASH_SOURCE[0]}")/restart_local_ui.py" "$@"
