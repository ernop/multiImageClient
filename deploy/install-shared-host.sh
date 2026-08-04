#!/usr/bin/env bash
set -Eeuo pipefail

# One-shot root installer for a colocated nginx/systemd host. It never edits
# an existing vhost: MultiImageClient gets a new exact server_name, dedicated
# service user, directories, certificate, unit, and rate-limit zones.

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

[[ $EUID -eq 0 ]] || die "run with sudo"
[[ $# -eq 1 ]] || die "usage: sudo bash deploy/install-shared-host.sh PUBLIC_IP"

public_ip=$1
[[ $public_ip =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "PUBLIC_IP must be IPv4"

owner=${SUDO_USER:-tparkour}
owner_home=$(getent passwd "$owner" | cut -d: -f6)
[[ -n $owner_home && -d $owner_home ]] || die "cannot resolve deploying user's home"

repo="$owner_home/multiImageClient"
publish_staging="$owner_home/multiimageclient-publish-staging"
source_settings="$repo/MultiImageClient/settings.json"
template_nginx="$repo/deploy/nginx-multiimageclient.conf"
template_service="$repo/deploy/multiimageclient-ui.service"

for required in "$publish_staging/MultiImageClient.dll" "$source_settings" \
    "$template_nginx" "$template_service"; do
    [[ -e $required ]] || die "required staged file is missing: $required"
done

for command in nginx certbot jq openssl rsync python3 curl systemctl ss; do
    command -v "$command" >/dev/null || die "required command is missing: $command"
done

# Port 5960 belongs exclusively to this service.
if ss -lnt | awk '$4 ~ /:5960$/ { found=1 } END { exit found ? 0 : 1 }'; then
    die "TCP port 5960 is already in use"
fi

stamp=$(date -u +%Y%m%dT%H%M%SZ)
backup="/root/multiimageclient-preinstall-$stamp"
install -d -m 0700 "$backup"

# Capture the complete effective nginx topology before touching anything,
# including root-only sites. Also retain byte-for-byte config/unit backups.
nginx -T >"$backup/nginx-effective.txt" 2>&1 \
    || die "existing nginx configuration is invalid; refusing deployment"
cp -a /etc/nginx "$backup/nginx"
if [[ -e /etc/systemd/system/multiimageclient-ui.service ]]; then
    cp -a /etc/systemd/system/multiimageclient-ui.service "$backup/"
fi

host="mic-$(openssl rand -hex 6).${public_ip//./-}.sslip.io"
secret_path=$(openssl rand -hex 16)
auth_secret=$(openssl rand -hex 32)
admin_password=$(openssl rand -hex 16)
a_password=$(openssl rand -hex 16)
b_password=$(openssl rand -hex 16)

resolved_ip=$(getent ahostsv4 "$host" | awk 'NR == 1 { print $1 }')
[[ $resolved_ip == "$public_ip" ]] \
    || die "$host resolved to '$resolved_ip', expected '$public_ip'"

credentials="$owner_home/multiimageclient-credentials.txt"
cat >"$credentials" <<EOF
URL=https://$host/$secret_path/
ernie=$admin_password
a=$a_password
b=$b_password
EOF
chown "$owner:$owner" "$credentials"
chmod 0600 "$credentials"

if ! getent group multiimageclient >/dev/null; then
    groupadd --system multiimageclient
fi
if ! getent passwd multiimageclient >/dev/null; then
    useradd --system --gid multiimageclient --home-dir /var/lib/multiimageclient \
        --shell /usr/sbin/nologin multiimageclient
fi

install -d -o root -g root -m 0755 /opt/multiimageclient
install -d -o root -g multiimageclient -m 0750 /etc/multiimageclient
install -d -o multiimageclient -g multiimageclient -m 0750 \
    /var/lib/multiimageclient \
    /var/lib/multiimageclient/logs \
    /var/lib/multiimageclient/saves
install -d -o root -g root -m 0755 /var/www/letsencrypt

# --delete is confined to this dedicated /opt directory. No other project or
# host path is a destination.
rsync -a --delete "$publish_staging/" /opt/multiimageclient/
chown -R root:root /opt/multiimageclient

settings_tmp=$(mktemp)
jq \
    --arg log "/var/lib/multiimageclient/logs/multiimageclient.log" \
    --arg saves "/var/lib/multiimageclient/saves" \
    --arg archive "/var/lib/multiimageclient/saves/generation-history.sqlite3" \
    --arg auth "/etc/multiimageclient/ui-auth.json" \
    '.LogFilePath = $log
     | .ImageDownloadBaseFolder = $saves
     | .GenerationArchiveDbPath = $archive
     | .UiAuthFilePath = $auth
     | .UiMaxConcurrentJobs = 1
     | .UiMaxConcurrentGenerators = 2
     | .UiMinimumFreeDiskBytes = 3221225472
     | .FlatImageMirrorPath = ""
     | .TypedPromptsAppendFile = ""' \
    "$source_settings" >"$settings_tmp"
install -o root -g multiimageclient -m 0640 \
    "$settings_tmp" /etc/multiimageclient/settings.json
rm -f "$settings_tmp"

auth_tmp=$(mktemp)
jq -n \
    --arg secret "$auth_secret" \
    --arg admin "$admin_password" \
    --arg a "$a_password" \
    --arg b "$b_password" \
    '{
       enabled: true,
       secret: $secret,
       accounts: [
         { username: "ernie", password: $admin },
         { username: "a", password: $a },
         { username: "b", password: $b }
       ]
     }' >"$auth_tmp"
install -o root -g multiimageclient -m 0640 \
    "$auth_tmp" /etc/multiimageclient/ui-auth.json
rm -f "$auth_tmp"

# Substitute only the deploying user's home; the tracked unit contains no
# host routing or credentials.
sed "s|/home/tparkour|$owner_home|g" "$template_service" \
    >/etc/systemd/system/multiimageclient-ui.service
chown root:root /etc/systemd/system/multiimageclient-ui.service
chmod 0644 /etc/systemd/system/multiimageclient-ui.service

systemctl daemon-reload
systemctl enable --now multiimageclient-ui.service

app_ready=false
for _ in $(seq 1 30); do
    code=$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5960/ || true)
    if [[ $code == 401 ]]; then
        app_ready=true
        break
    fi
    sleep 1
done
if [[ $app_ready != true ]]; then
    systemctl status multiimageclient-ui.service --no-pager >&2 || true
    journalctl -u multiimageclient-ui.service -n 80 --no-pager >&2 || true
    die "application did not reach authenticated-ready state on loopback"
fi

# Phase 1: a new HTTP-only exact-name vhost for ACME. Existing vhosts and
# defaults remain byte-for-byte untouched.
site=/etc/nginx/sites-available/multiimageclient.conf
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
ln -sfn "$site" /etc/nginx/sites-enabled/multiimageclient.conf

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

# Renewals update certificate symlinks; nginx must reload to consume the new
# files. This dedicated hook validates the whole shared configuration first.
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

# Phase 2: render the reviewed final named-vhost template. Exact placeholder
# replacement fails closed if the tracked template is incomplete.
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

root_code=$(curl -sS -o /dev/null -w '%{http_code}' "https://$host/")
secret_code=$(curl -sS -o /dev/null -w '%{http_code}' \
    "https://$host/$secret_path/")
[[ $root_code == 404 ]] || die "public hostname root returned $root_code, expected 404"
[[ $secret_code == 401 ]] \
    || die "secret-path login returned $secret_code, expected 401"

# The immutable installed copy is now under /opt; reclaim staging disk.
rm -rf "$publish_staging"

printf 'MultiImageClient deployed successfully.\n'
printf 'Existing nginx sites were not edited. Preinstall backup: %s\n' "$backup"
printf 'Credentials (owner-only): %s\n' "$credentials"
