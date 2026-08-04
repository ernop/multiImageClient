#!/usr/bin/env bash
set -Eeuo pipefail

# Non-interactive redeploy for agents (and humans). Pulls, publishes to
# ~/multiimageclient-publish-staging, then installs via passwordless sudo.
# Requires one-time: sudo bash deploy/install-agent-deploy.sh
#
# Usage (no TTY / no password after install):
#   bash ~/multiImageClient/deploy/agent-redeploy.sh

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

repo="${MULTIIMAGECLIENT_REPO:-$HOME/multiImageClient}"
dotnet="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
staging="$HOME/multiimageclient-publish-staging"
helper=/usr/local/sbin/multiimageclient-update

[[ -d $repo/.git ]] || die "repo not found at $repo"
[[ -x $dotnet ]] || die "dotnet not found at $dotnet — install .NET 9 SDK"
[[ -x $helper ]] || die "missing $helper — run once: sudo bash $repo/deploy/install-agent-deploy.sh"

cd "$repo"
git pull --ff-only
git log -1 --oneline

rm -rf "$staging"
"$dotnet" publish "$repo/MultiImageClient/MultiImageClient.csproj" \
    -c Release -r linux-x64 --self-contained false \
    -o "$staging" --nologo

[[ -f $staging/MultiImageClient.dll ]] || die "publish produced no dll"
[[ -f $staging/Ui/wwwroot/index.html ]] || die "publish missing wwwroot"

# Prefer sudo -n so BatchMode SSH / agents never hang on a password prompt.
if ! sudo -n true 2>/dev/null; then
    die "passwordless sudo not available — run once: ssh -t host 'sudo bash $repo/deploy/install-agent-deploy.sh'"
fi

sudo -n "$helper"
