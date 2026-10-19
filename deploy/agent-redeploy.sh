#!/usr/bin/env bash
set -Eeuo pipefail

# Non-interactive release of the original (AVE) MultiImageClient instance
# (currently multiimageclient.alpha.fuseki.net on host tpbeta). Pulls,
# publishes to ~/multiimageclient-publish-staging, then installs via the
# passwordless helper. It updates only multiimageclient-ui.service. Every
# release then runs deploy/update-vibecoders.sh as root, which installs the
# same build into Vibecoders. The host's other sites and services are outside
# this script's scope.
# Requires one-time: sudo bash deploy/install-agent-deploy.sh
#
# From the current development workstation:
#   ssh tpbeta-root \
#     'sudo -u tparkour -H bash /home/tparkour/multiImageClient/deploy/agent-redeploy.sh'
#
# A restart kills in-flight work, so the release exits 75 without changes
# while either instance has unfinished jobs or goal loops. --wait-minutes N
# polls until both are idle instead.

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

wait_minutes=0
if [[ "${1:-}" == "--wait-minutes" && "${2:-}" =~ ^[0-9]+$ ]]; then
    wait_minutes=$2
    shift 2
fi
(( $# == 0 )) || die "usage: agent-redeploy.sh [--wait-minutes N]"

repo="${MULTIIMAGECLIENT_REPO:-$HOME/multiImageClient}"
dotnet="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
staging="$HOME/multiimageclient-publish-staging"
helper=/usr/local/sbin/multiimageclient-update
checker=/usr/local/lib/multiimageclient-update/check-unfinished-work.py

[[ -d $repo/.git ]] || die "repo not found at $repo"
[[ -x $dotnet ]] || die "dotnet not found at $dotnet — install .NET 10 SDK"
[[ -x $helper ]] || die "missing $helper — run once: sudo bash $repo/deploy/install-agent-deploy.sh"

# Only the pinned helper is NOPASSWD (not sudo in general). -n avoids hanging
# BatchMode SSH on a password prompt when the install step was never run.
run_helper() {
    local status=0
    sudo -n "$helper" "$@" || status=$?
    case $status in
        0) ;;
        75) exit 75 ;;
        *) die "update helper failed (see above). If sudo asked for a password, run once: ssh -t host 'sudo bash $repo/deploy/install-agent-deploy.sh'" ;;
    esac
}

cd "$repo"
git pull --ff-only
git log -1 --oneline

# The helper and its idle checker are root-owned copies, so repository changes
# to either take effect only after root reinstalls them. Never release through
# a stale copy.
cmp -s "$helper" "$repo/deploy/update-shared-host.sh" \
    || die "$helper differs from deploy/update-shared-host.sh — as root run: bash $repo/deploy/install-agent-deploy.sh, then rerun this script"
cmp -s "$checker" "$repo/deploy/check-unfinished-work.py" \
    || die "$checker differs from deploy/check-unfinished-work.py — as root run: bash $repo/deploy/install-agent-deploy.sh, then rerun this script"

rm -rf "$staging"
# Publishing competes with running jobs on the shared host, and every release
# restarts both instances, so build only once both are idle.
run_helper busy-check --wait-minutes "$wait_minutes"

"$dotnet" publish "$repo/MultiImageClient/MultiImageClient.csproj" \
    -c Release -r linux-x64 --self-contained false \
    -o "$staging" --nologo

[[ -f $staging/MultiImageClient.dll ]] || die "publish produced no dll"
[[ -f $staging/Ui/wwwroot/index.html ]] || die "publish missing wwwroot"

run_helper --wait-minutes "$wait_minutes"

printf 'Original updated. Next release step, as root: bash %s/deploy/update-vibecoders.sh [--wait-minutes N]\n' "$repo"
