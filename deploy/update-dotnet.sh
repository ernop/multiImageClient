#!/usr/bin/env bash
set -Eeuo pipefail

# Installs the newest .NET 10 SDK and runtime into ~/.dotnet beside the
# installed versions. Run it as the account that owns ~/.dotnet: tparkour on
# the production host, the owner on the workstation. Running services and
# build servers keep the old files until they restart.
#
# Production, then release both instances so they load the new runtime:
#   ssh tpbeta-root \
#     'sudo -u tparkour -H bash /home/tparkour/multiImageClient/deploy/update-dotnet.sh'
# Workstation, then restart the local service:
#   bash deploy/update-dotnet.sh

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
(( $# == 0 )) || die "usage: update-dotnet.sh"

# The .NET SDK sends usage data to Microsoft unless this is set.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
root="$HOME/.dotnet"
work=$(mktemp -d "$HOME/dotnet-update.XXXXXX")
trap 'rm -rf "$work"' EXIT

curl -sSfL -m 120 https://dot.net/v1/dotnet-install.sh -o "$work/dotnet-install.sh"
# Linux refuses to overwrite a program while it runs, and the services run
# the dotnet launcher, so the install skips it and a rename replaces it.
bash "$work/dotnet-install.sh" --channel 10.0 --install-dir "$root" \
    --skip-non-versioned-files --no-path --keep-zip --zip-path "$work/dotnet.tar.gz"

# Microsoft's script exits without downloading when the newest version is
# already installed.
if [[ -s $work/dotnet.tar.gz ]]; then
    member=$(tar -tzf "$work/dotnet.tar.gz" | grep -x -E '(\./)?dotnet')
    tar -xzf "$work/dotnet.tar.gz" -C "$work" "$member"
    install -m 755 "$work/dotnet" "$root/dotnet.new"
    mv -f "$root/dotnet.new" "$root/dotnet"
fi

# Reports queued before the opt-out would otherwise wait to be sent.
rm -f "$root"/TelemetryStorageService/*.trn

"$root/dotnet" --list-sdks
"$root/dotnet" --list-runtimes
