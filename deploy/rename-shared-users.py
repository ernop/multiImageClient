#!/usr/bin/env python3
import grp
import json
import os
import pathlib
import pwd
import re
import sys
import tempfile


def fail(message: str) -> None:
    raise SystemExit(f"ERROR: {message}")


if os.geteuid() != 0:
    fail("run with sudo")
if len(sys.argv) < 2:
    fail("usage: sudo python3 deploy/rename-shared-users.py old=new [old=new ...]")

mapping: dict[str, str] = {}
valid_name = re.compile(r"^[A-Za-z0-9._ -]{1,32}$")
for argument in sys.argv[1:]:
    if "=" not in argument:
        fail(f"invalid rename '{argument}'; expected old=new")
    old, new = argument.split("=", 1)
    if not old or not new or old == new:
        fail(f"invalid rename '{argument}'")
    if not valid_name.fullmatch(new) or new != new.strip() or "  " in new:
        fail(f"new username '{new}' does not meet the UI username contract")
    if old in mapping:
        fail(f"username '{old}' is listed more than once")
    mapping[old] = new
if len(set(mapping.values())) != len(mapping):
    fail("new usernames must be unique")

owner = os.environ.get("SUDO_USER", "tparkour")
owner_entry = pwd.getpwnam(owner)
auth_path = pathlib.Path("/etc/multiimageclient/ui-auth.json")
credentials_path = pathlib.Path(owner_entry.pw_dir) / "multiimageclient-credentials.txt"

auth = json.loads(auth_path.read_text())
accounts = auth.get("accounts")
if not isinstance(accounts, list):
    fail("auth file has no accounts array")
existing = [account.get("username") for account in accounts]
for old in mapping:
    if existing.count(old) != 1:
        fail(f"expected exactly one auth account named '{old}'")
for new in mapping.values():
    if new in existing and new not in mapping:
        fail(f"new username '{new}' already exists")

credential_lines = credentials_path.read_text().splitlines()
if not credential_lines or not credential_lines[0].startswith("URL="):
    fail("credentials file has no URL line")
credential_keys = [line.split("=", 1)[0] for line in credential_lines[1:] if "=" in line]
for old in mapping:
    if credential_keys.count(old) != 1:
        fail(f"expected exactly one credentials entry named '{old}'")
for new in mapping.values():
    if new in credential_keys and new not in mapping:
        fail(f"credentials entry '{new}' already exists")

for account in accounts:
    username = account.get("username")
    if username in mapping:
        account["username"] = mapping[username]

updated_credentials = [credential_lines[0]]
for line in credential_lines[1:]:
    key, separator, value = line.partition("=")
    if not separator:
        fail("credentials file contains a malformed line")
    updated_credentials.append(f"{mapping.get(key, key)}={value}")


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
    json.dumps(auth, indent=2) + "\n",
    0o640,
    0,
    auth_group,
)
credentials_temp = stage(
    credentials_path,
    "\n".join(updated_credentials) + "\n",
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

print("Renamed accounts: " + ", ".join(f"{old} -> {new}" for old, new in mapping.items()))
print("Existing passwords were preserved; old login cookies are now invalid.")
