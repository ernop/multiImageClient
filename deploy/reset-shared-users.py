#!/usr/bin/env python3
import grp
import json
import os
import pathlib
import pwd
import re
import secrets
import sys
import tempfile


def fail(message: str) -> None:
    raise SystemExit(f"ERROR: {message}")


if os.geteuid() != 0:
    fail("run with sudo")
if len(sys.argv) < 2:
    fail("usage: sudo python3 deploy/reset-shared-users.py username [username ...]")

usernames = sys.argv[1:]
valid_name = re.compile(r"^[A-Za-z0-9._ -]{1,32}$")
for username in usernames:
    if (
        not valid_name.fullmatch(username)
        or username != username.strip()
        or "  " in username
    ):
        fail(f"username '{username}' does not meet the UI username contract")
if len(set(usernames)) != len(usernames):
    fail("usernames must be unique")

owner = os.environ.get("SUDO_USER", "tparkour")
owner_entry = pwd.getpwnam(owner)
auth_path = pathlib.Path("/etc/multiimageclient/ui-auth.json")
credentials_path = pathlib.Path(owner_entry.pw_dir) / "multiimageclient-credentials.txt"

current_auth = json.loads(auth_path.read_text())
if not current_auth.get("enabled"):
    fail("existing auth file is not enabled")

credential_lines = credentials_path.read_text().splitlines()
if not credential_lines or not credential_lines[0].startswith("URL="):
    fail("credentials file has no URL line")
url_line = credential_lines[0]

passwords = {username: secrets.token_hex(16) for username in usernames}
replacement_auth = {
    "enabled": True,
    "secret": secrets.token_hex(32),
    "accounts": [
        {"username": username, "password": passwords[username]}
        for username in usernames
    ],
}
replacement_credentials = [url_line]
replacement_credentials.extend(
    f"{username}={passwords[username]}" for username in usernames
)


def stage(path: pathlib.Path, content: str, mode: int, uid: int, gid: int) -> pathlib.Path:
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = pathlib.Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        os.chown(temporary, uid, gid)
        return temporary
    except Exception:
        temporary.unlink(missing_ok=True)
        raise


auth_group = grp.getgrnam("multiimageclient").gr_gid
auth_temp = stage(
    auth_path,
    json.dumps(replacement_auth, indent=2) + "\n",
    0o640,
    0,
    auth_group,
)
credentials_temp = stage(
    credentials_path,
    "\n".join(replacement_credentials) + "\n",
    0o600,
    owner_entry.pw_uid,
    owner_entry.pw_gid,
)

try:
    os.replace(auth_temp, auth_path)
    os.replace(credentials_temp, credentials_path)
except Exception:
    auth_temp.unlink(missing_ok=True)
    credentials_temp.unlink(missing_ok=True)
    raise

print("Replaced the complete account set and rotated the signing secret.")
print("Fresh credentials are owner-only at " + str(credentials_path))
