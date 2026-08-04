#!/usr/bin/env bash
set -Eeuo pipefail

# Root installer: copies the update helper to a fixed path and grants the
# deploy user passwordless sudo for THAT PATH ONLY. After this one interactive
# sudo, agents can finish deploys with `sudo -n /usr/local/sbin/multiimageclient-update`.
#
# Usage (once):
#   ssh -t tpbeta 'sudo bash ~/multiImageClient/deploy/install-agent-deploy.sh'

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || die "run with sudo"

owner=${SUDO_USER:-tparkour}
owner_home=$(getent passwd "$owner" | cut -d: -f6)
[[ -n $owner_home && -d $owner_home ]] || die "cannot resolve deploy user home"

src_update="$owner_home/multiImageClient/deploy/update-shared-host.sh"
[[ -f $src_update ]] || die "missing $src_update — git pull the repo first"

# Thin wrapper: sudoers pins this path; body always execs the repo script so
# agent git pulls pick up deploy fixes without re-running this installer.
helper=/usr/local/sbin/multiimageclient-update
cat >"$helper" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
exec bash "$src_update" "\$@"
EOF
chmod 0755 "$helper"
chown root:root "$helper"

sudoers_file=/etc/sudoers.d/multiimageclient-agent-deploy
cat >"$sudoers_file" <<EOF
# Managed by deploy/install-agent-deploy.sh — passwordless deploy for agents.
# Only the fixed sbin update helper; nothing else.
$owner ALL=(root) NOPASSWD: $helper
EOF
chmod 0440 "$sudoers_file"
visudo -cf "$sudoers_file" >/dev/null \
    || { rm -f "$sudoers_file"; die "sudoers validation failed; removed $sudoers_file"; }

# Smoke as the deploy user (staging may be present — then this finishes tonight's deploy).
if sudo -u "$owner" -H sudo -n "$helper" >/tmp/mic-agent-deploy-smoke.out 2>/tmp/mic-agent-deploy-smoke.err; then
    printf 'Smoke deploy succeeded.\n'
    cat /tmp/mic-agent-deploy-smoke.out
else
    if grep -qi 'password is required' /tmp/mic-agent-deploy-smoke.err; then
        die "NOPASSWD did not take effect"
    fi
    printf 'NOPASSWD is active (update exited non-zero — often missing staging; OK for install).\n'
    printf 'stderr: %s\n' "$(head -5 /tmp/mic-agent-deploy-smoke.err)"
fi

printf '\nInstalled.\n'
printf '  helper:  %s\n' "$helper"
printf '  sudoers: %s\n' "$sudoers_file"
printf '  agent:   bash ~/multiImageClient/deploy/agent-redeploy.sh\n'
