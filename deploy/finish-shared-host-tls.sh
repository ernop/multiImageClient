#!/usr/bin/env bash
set -Eeuo pipefail

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

[[ $EUID -eq 0 ]] || die "run with sudo"
[[ $# -eq 2 ]] \
    || die "usage: sudo bash deploy/finish-shared-host-tls.sh HOSTNAME PUBLIC_IP"

host=${1,,}
public_ip=$2
[[ $host =~ ^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$ ]] || die "invalid hostname"
[[ $public_ip =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "invalid IPv4 address"

owner=${SUDO_USER:-tparkour}
owner_home=$(getent passwd "$owner" | cut -d: -f6)
repo="$owner_home/multiImageClient"
credentials="$owner_home/multiimageclient-credentials.txt"
template_nginx="$repo/deploy/nginx-multiimageclient.conf"
site=/etc/nginx/sites-available/multiimageclient.conf

for required in "$credentials" "$template_nginx" "$site"; do
    [[ -f $required ]] || die "required file is missing: $required"
done
systemctl is-active --quiet multiimageclient-ui.service \
    || die "multiimageclient-ui.service is not active"

url=$(awk -F= '$1 == "URL" { print substr($0, 5); exit }' "$credentials")
[[ $url == https://*/*/ ]] || die "credentials file has an invalid URL"
without_scheme=${url#https://}
old_host=${without_scheme%%/*}
path_and_rest=${without_scheme#*/}
secret_path=${path_and_rest%%/*}
[[ $secret_path =~ ^[a-f0-9]{32}$ ]] || die "credentials URL has an invalid secret path"

resolved_ip=$(getent ahostsv4 "$host" | awk 'NR == 1 { print $1 }')
[[ $resolved_ip == "$public_ip" ]] \
    || die "$host resolves to '$resolved_ip', expected '$public_ip'; wait for DNS"

for command in nginx certbot curl python3 systemctl; do
    command -v "$command" >/dev/null || die "required command is missing: $command"
done

stamp=$(date -u +%Y%m%dT%H%M%SZ)
backup="/root/multiimageclient-tls-resume-$stamp"
install -d -m 0700 "$backup"
nginx -T >"$backup/nginx-effective.txt" 2>&1 \
    || die "existing nginx configuration is invalid; refusing changes"
cp -a "$site" "$backup/multiimageclient.conf"

# Phase 1: replace only MultiImageClient's failed temporary vhost with the
# owned exact hostname and an ACME webroot. Every neighboring vhost is outside
# this file and remains byte-for-byte unchanged.
cat >"$site" <<EOF
server {
    listen 80;
    listen [::]:80;
    server_name $host;

    location ^~ /.well-known/acme-challenge/ {
        root /var/www/letsencrypt;
        default_type text/plain;
    }

    location / {
        return 404;
    }
}
EOF
chown root:root "$site"
chmod 0644 "$site"
nginx -t
systemctl reload nginx

probe="multiimageclient-acme-$stamp"
install -d -o root -g root -m 0755 \
    /var/www/letsencrypt/.well-known/acme-challenge
printf '%s' "$probe" \
    >"/var/www/letsencrypt/.well-known/acme-challenge/$probe"
served_probe=
for _ in $(seq 1 10); do
    served_probe=$(curl -sS -H "Host: $host" \
        "http://127.0.0.1/.well-known/acme-challenge/$probe" || true)
    [[ $served_probe == "$probe" ]] && break
    # nginx reload is graceful and briefly leaves retiring workers able to
    # receive a request with the previous routing table.
    sleep 1
done
rm -f "/var/www/letsencrypt/.well-known/acme-challenge/$probe"
[[ $served_probe == "$probe" ]] || die "isolated ACME vhost probe failed"

certbot certonly --webroot -w /var/www/letsencrypt \
    -d "$host" --non-interactive --agree-tos \
    --register-unsafely-without-email

install -d -o root -g root -m 0755 /etc/letsencrypt/renewal-hooks/deploy
cat >/etc/letsencrypt/renewal-hooks/deploy/multiimageclient-reload-nginx <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
nginx -t
systemctl reload nginx
EOF
chown root:root \
    /etc/letsencrypt/renewal-hooks/deploy/multiimageclient-reload-nginx
chmod 0755 \
    /etc/letsencrypt/renewal-hooks/deploy/multiimageclient-reload-nginx

# Phase 2: install the reviewed final vhost under the same dedicated site
# file, preserving the existing unlisted path.
python3 - "$template_nginx" "$site" "$host" "$secret_path" <<'PY'
import pathlib
import sys

source, destination, hostname, secret_path = sys.argv[1:]
text = pathlib.Path(source).read_text()
if "REPLACE_HOSTNAME" not in text or "REPLACE-WITH-LONG-RANDOM-SEGMENT" not in text:
    raise SystemExit("nginx template placeholders are missing")
text = text.replace("REPLACE_HOSTNAME", hostname)
text = text.replace("REPLACE-WITH-LONG-RANDOM-SEGMENT", secret_path)
pathlib.Path(destination).write_text(text)
PY
chown root:root "$site"
chmod 0644 "$site"
nginx -t
systemctl reload nginx

root_code=
secret_code=
for _ in $(seq 1 10); do
    root_code=$(curl -sS -o /dev/null -w '%{http_code}' \
        "https://$host/" || true)
    secret_code=$(curl -sS -o /dev/null -w '%{http_code}' \
        "https://$host/$secret_path/" || true)
    [[ $root_code == 404 && $secret_code == 401 ]] && break
    sleep 1
done
[[ $root_code == 404 ]] || die "hostname root returned $root_code, expected 404"
[[ $secret_code == 401 ]] \
    || die "secret-path login returned $secret_code, expected 401"

python3 - "$credentials" "https://$host/$secret_path/" <<'PY'
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
url = sys.argv[2]
lines = path.read_text().splitlines()
if not lines or not lines[0].startswith("URL="):
    raise SystemExit("credentials file URL line is missing")
lines[0] = f"URL={url}"
path.write_text("\n".join(lines) + "\n")
PY
chown "$owner:$owner" "$credentials"
chmod 0600 "$credentials"

# The old sslip.io hostname no longer appears in nginx. Reclaim the duplicated
# publish staging directory left by the interrupted first pass.
rm -rf "$owner_home/multiimageclient-publish-staging"

printf 'TLS deployment completed for %s (replaced %s).\n' "$host" "$old_host"
printf 'Credentials remain at %s\n' "$credentials"
printf 'Pre-change backup: %s\n' "$backup"
