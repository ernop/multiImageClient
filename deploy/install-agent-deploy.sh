#!/usr/bin/env bash
set -Eeuo pipefail

# Root installer: copies the update helper to a fixed path and grants the
# deploy user passwordless sudo for THAT PATH ONLY. After this one interactive
# sudo, agents can finish deploys with `sudo -n /usr/local/sbin/multiimageclient-update`.
# Rerun it whenever deploy/update-shared-host.sh or deploy/check-unfinished-work.py
# changes; agent-redeploy.sh refuses to release while an installed copy differs
# from the repository.
#
# From the current development workstation:
#   ssh tpbeta-root \
#     'bash /home/tparkour/multiImageClient/deploy/install-agent-deploy.sh'

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || die "run with sudo"

owner=${SUDO_USER:-tparkour}
owner_home=$(getent passwd "$owner" | cut -d: -f6)
[[ -n $owner_home && -d $owner_home ]] || die "cannot resolve deploy user home"

src_update="$owner_home/multiImageClient/deploy/update-shared-host.sh"
src_checker="$owner_home/multiImageClient/deploy/check-unfinished-work.py"
[[ -f $src_update ]] || die "missing $src_update — git pull the repo first"
[[ -f $src_checker ]] || die "missing $src_checker — git pull the repo first"

# sudoers lets the deploy user run this file as root, so it must be a
# root-owned copy. Running a file that the deploy user can edit would give
# that user unrestricted root. The helper runs the idle checker as root, so
# the checker is a root-owned copy too. It goes in first, so an interrupted
# install never leaves a new helper without its checker.
helper=/usr/local/sbin/multiimageclient-update
checker=/usr/local/lib/multiimageclient-update/check-unfinished-work.py
install -d -o root -g root -m 0755 "$(dirname "$checker")"
install -o root -g root -m 0644 "$src_checker" "$checker"
install -o root -g root -m 0755 "$src_update" "$helper"

sudoers_file=/etc/sudoers.d/multiimageclient-agent-deploy
cat >"$sudoers_file" <<EOF
# Managed by deploy/install-agent-deploy.sh — passwordless deploy for agents.
# Only the fixed sbin update helper; nothing else.
$owner ALL=(root) NOPASSWD: $helper
EOF
chmod 0440 "$sudoers_file"
visudo -cf "$sudoers_file" >/dev/null \
    || { rm -f "$sudoers_file"; die "sudoers validation failed; removed $sudoers_file"; }

# Check the grant without running the helper, which would deploy any staged
# build. `sudo -l <command>` also succeeds for password-only grants, so match
# the exact NOPASSWD entry in the listing.
sudo -u "$owner" -H sudo -n -l | sed 's/^[[:space:]]*//' \
    | grep -xF "(root) NOPASSWD: $helper" >/dev/null \
    || die "NOPASSWD for $helper did not take effect for $owner"

printf '\nInstalled.\n'
printf '  helper:  %s\n' "$helper"
printf '  checker: %s\n' "$checker"
printf '  sudoers: %s\n' "$sudoers_file"
printf '  agent:   bash ~/multiImageClient/deploy/agent-redeploy.sh\n'
