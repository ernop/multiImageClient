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

# Ensure Playwright driver bits stay executable for the non-root service user
# after rsync + chown root:root (and force a content touch so rsync won't skip
# a same-mtime stale 764 mode left from an older deploy).
if [[ -d $staging/.playwright ]]; then
    chmod -R a+rX "$staging/.playwright"
    find "$staging/.playwright" -type f \( -name node -o -name chrome -o -name chromium -o -name 'chrome-headless-shell' -o -name ffmpeg \) \
        -exec chmod a+x {} + -exec touch {} +
fi

# Only the pinned helper is NOPASSWD (not sudo in general). -n avoids hanging
# BatchMode SSH on a password prompt when the install step was never run.
if ! sudo -n "$helper"; then
    die "passwordless update failed — run once: ssh -t host 'sudo bash $repo/deploy/install-agent-deploy.sh'"
fi
