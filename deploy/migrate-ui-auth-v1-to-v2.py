#!/usr/bin/env python3
"""One-time, fail-closed migration from plaintext UI auth to PBKDF2 hashes.

Also emits a single password hash for greenfield installs:

  python3 deploy/migrate-ui-auth-v1-to-v2.py --emit-hash 'passphrase'
"""

import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import sys
import tempfile


ITERATIONS = 600_000
SALT_BYTES = 16
HASH_BYTES = 32
MIN_SECRET_CHARS = 32


def fail(message: str) -> None:
    raise SystemExit(f"ERROR: {message}")


def exact_keys(value: dict, expected: set[str], context: str) -> None:
    actual = set(value)
    if actual != expected:
        fail(
            f"{context} fields must be exactly {sorted(expected)}; "
            f"found {sorted(actual)}"
        )


def hash_password(password: str) -> str:
    if not isinstance(password, str) or not password or len(password) > 1024:
        fail("password must be a non-empty string of at most 1024 characters")
    salt = secrets.token_bytes(SALT_BYTES)
    digest = hashlib.pbkdf2_hmac(
        "sha256",
        password.encode("utf-8"),
        salt,
        ITERATIONS,
        dklen=HASH_BYTES,
    )
    return (
        f"pbkdf2-sha256${ITERATIONS}$"
        f"{base64.b64encode(salt).decode('ascii')}$"
        f"{base64.b64encode(digest).decode('ascii')}"
    )


def emit_hash(password: str) -> None:
    print(hash_password(password), end="")


def migrate(path_text: str) -> None:
    path = Path(path_text).resolve()
    if not path.is_file():
        fail(f"auth file does not exist: {path}")
    if path.is_symlink():
        fail("auth file must not be a symlink")
    stat = path.stat()
    if stat.st_mode & 0o037:
        fail("auth file permissions must be 0600 or 0640 before migration")

    try:
        source = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        fail(f"cannot read valid JSON: {exc}")
    if not isinstance(source, dict):
        fail("auth file root must be an object")
    if source.get("version") == 2:
        fail("auth file is already version 2")
    exact_keys(source, {"enabled", "secret", "accounts"}, "version-1 root")
    if source["enabled"] is not True:
        fail("version-1 auth must have enabled=true")
    secret = source["secret"]
    if not isinstance(secret, str) or len(secret.strip()) < MIN_SECRET_CHARS:
        fail(f"secret must contain at least {MIN_SECRET_CHARS} characters before migration")
    accounts = source["accounts"]
    if not isinstance(accounts, list) or not accounts:
        fail("accounts must be a non-empty array")

    migrated_accounts = []
    seen = set()
    for index, account in enumerate(accounts):
        if not isinstance(account, dict):
            fail(f"account {index} must be an object")
        exact_keys(account, {"username", "password"}, f"account {index}")
        username = account["username"]
        password = account["password"]
        if not isinstance(username, str) or not username.strip() or len(username.strip()) > 128:
            fail(f"account {index} has an invalid username")
        if any(ord(character) < 32 or ord(character) == 127 for character in username):
            fail(f"account {index} username contains control characters")
        if not isinstance(password, str) or not password or len(password) > 1024:
            fail(f"account {index} has an invalid password")
        canonical = username.strip().casefold()
        if canonical in seen:
            fail(f"duplicate username: {username.strip()}")
        seen.add(canonical)
        migrated_accounts.append(
            {"username": username.strip(), "passwordHash": hash_password(password)}
        )

    target = {
        "version": 2,
        "enabled": True,
        "secret": secret.strip(),
        "accounts": migrated_accounts,
    }
    backup = path.with_name(path.name + ".pre-hash-v1")
    if backup.exists():
        fail(f"backup already exists; inspect it before retrying: {backup}")
    shutil.copy2(path, backup, follow_symlinks=False)
    os.chmod(backup, stat.st_mode & 0o777)

    descriptor, temporary_name = tempfile.mkstemp(
        prefix=path.name + ".",
        suffix=".tmp",
        dir=path.parent,
    )
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            json.dump(target, output, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary_name, stat.st_mode & 0o777)
        if hasattr(os, "chown"):
            os.chown(temporary_name, stat.st_uid, stat.st_gid)
        os.replace(temporary_name, path)
        directory_fd = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    finally:
        if os.path.exists(temporary_name):
            os.unlink(temporary_name)

    print(
        f"Migrated {len(migrated_accounts)} account(s) to version 2 at {path}. "
        f"Plaintext backup retained at {backup}; delete it after login verification. "
        "Existing browser cookies are invalid; each person must log in again."
    )


def main() -> None:
    if len(sys.argv) == 3 and sys.argv[1] == "--emit-hash":
        emit_hash(sys.argv[2])
        return
    if len(sys.argv) == 2 and sys.argv[1] != "--emit-hash":
        migrate(sys.argv[1])
        return
    fail(
        "usage: migrate-ui-auth-v1-to-v2.py /etc/multiimageclient/ui-auth.json\n"
        "       migrate-ui-auth-v1-to-v2.py --emit-hash 'passphrase'"
    )


if __name__ == "__main__":
    main()
