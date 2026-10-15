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

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

repo="${MULTIIMAGECLIENT_REPO:-$HOME/multiImageClient}"
dotnet="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
staging="$HOME/multiimageclient-publish-staging"
helper=/usr/local/sbin/multiimageclient-update

[[ -d $repo/.git ]] || die "repo not found at $repo"
[[ -x $dotnet ]] || die "dotnet not found at $dotnet — install .NET 10 SDK"
[[ -x $helper ]] || die "missing $helper — run once: sudo bash $repo/deploy/install-agent-deploy.sh"

cd "$repo"
git pull --ff-only
git log -1 --oneline

# The helper is a root-owned copy, so repository changes to the update script
# take effect only after root reinstalls it. Never release through a stale copy.
cmp -s "$helper" "$repo/deploy/update-shared-host.sh" \
    || die "$helper differs from deploy/update-shared-host.sh — as root run: bash $repo/deploy/install-agent-deploy.sh, then rerun this script"

rm -rf "$staging"
"$dotnet" publish "$repo/MultiImageClient/MultiImageClient.csproj" \
    -c Release -r linux-x64 --self-contained false \
    -o "$staging" --nologo

[[ -f $staging/MultiImageClient.dll ]] || die "publish produced no dll"
[[ -f $staging/Ui/wwwroot/index.html ]] || die "publish missing wwwroot"

# Only the pinned helper is NOPASSWD (not sudo in general). -n avoids hanging
# BatchMode SSH on a password prompt when the install step was never run.
if ! sudo -n "$helper"; then
    die "passwordless update failed — run once: ssh -t host 'sudo bash $repo/deploy/install-agent-deploy.sh'"
fi

printf 'Original updated. Next release step, as root: bash %s/deploy/update-vibecoders.sh\n' "$repo"
