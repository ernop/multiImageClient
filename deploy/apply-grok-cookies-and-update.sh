#!/usr/bin/env bash
set -Eeuo pipefail

# Install grok-web cookies into /etc, point production settings at them,
# optionally install a staged publish into /opt, and restart the UI.
#
# Prerequisites (as the deploying user, before sudo):
#   - ~/grok-web-cookies.txt          (Netscape export; scp'd from your machine)
#   - ~/multiimageclient-publish-staging/  (optional; from dotnet publish)
#
# Usage:
#   sudo bash deploy/apply-grok-cookies-and-update.sh
#   # or, once copied to the host:
#   sudo bash /home/tparkour/apply-grok-cookies-and-update.sh

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

[[ $EUID -eq 0 ]] || die "run with sudo"

owner=${SUDO_USER:-tparkour}
owner_home=$(getent passwd "$owner" | cut -d: -f6)
[[ -n $owner_home && -d $owner_home ]] || die "cannot resolve deploying user's home"

cookie_src="$owner_home/grok-web-cookies.txt"
cookie_dst=/etc/multiimageclient/grok-web-cookies.txt
settings=/etc/multiimageclient/settings.json
publish_staging="$owner_home/multiimageclient-publish-staging"

[[ -f $cookie_src ]] || die "missing cookie file: $cookie_src"
[[ -f $settings ]] || die "missing settings: $settings"
command -v jq >/dev/null || die "jq is required"
getent group multiimageclient >/dev/null || die "group multiimageclient is missing"

install -o root -g multiimageclient -m 0640 "$cookie_src" "$cookie_dst"

tmp=$(mktemp)
jq --arg path "$cookie_dst" '.GrokWebCookiePath = $path' "$settings" >"$tmp" \
    || die "jq failed updating GrokWebCookiePath"
install -o root -g multiimageclient -m 0640 "$tmp" "$settings"
rm -f "$tmp"

if [[ -f $publish_staging/MultiImageClient.dll ]]; then
    [[ -f $publish_staging/Ui/wwwroot/index.html ]] \
        || die "staged publish is missing Ui/wwwroot"
    rsync -a --delete "$publish_staging/" /opt/multiimageclient/
    chown -R root:root /opt/multiimageclient
    rm -rf "$publish_staging"
    printf 'Installed staged publish into /opt/multiimageclient.\n'
else
    printf 'No publish staging at %s — left /opt unchanged.\n' "$publish_staging"
fi

systemctl restart multiimageclient-ui
sleep 1
systemctl is-active multiimageclient-ui >/dev/null \
    || die "multiimageclient-ui failed to become active"

# Home copy is no longer needed once /etc has the secret.
rm -f "$cookie_src"

printf '\nOK.\n'
printf '  service:           %s\n' "$(systemctl is-active multiimageclient-ui)"
printf '  GrokWebCookiePath: %s\n' "$(jq -r .GrokWebCookiePath "$settings")"
printf '  cookie file:       %s\n' "$(ls -l "$cookie_dst")"
if grep -q show-costs /opt/multiimageclient/Ui/wwwroot/index.html 2>/dev/null; then
    printf '  wwwroot:           show-costs present (current UI)\n'
else
    printf '  wwwroot:           show-costs absent (publish staging was not installed)\n'
fi
