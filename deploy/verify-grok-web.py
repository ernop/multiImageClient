#!/usr/bin/env python3
"""Verify shared-host grok-web after apply-grok-cookies-and-update.sh.
Does not print passwords or cookie contents.
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import tempfile
import urllib.parse
import urllib.request

home = pathlib.Path.home()
creds_path = home / "multiimageclient-credentials.txt"
creds: dict[str, str] = {}
for line in creds_path.read_text(encoding="utf-8").splitlines():
    if "=" not in line:
        continue
    k, v = line.split("=", 1)
    creds[k.strip()] = v.strip()

url = creds.get("URL", "").rstrip("/")
# Prefer an ernie* account if present; otherwise first non-URL key.
password = ""
username = ""
for key, value in creds.items():
    if key == "URL" or not value:
        continue
    if key.lower().startswith("ernie"):
        username, password = key, value
        break
if not password:
    for key, value in creds.items():
        if key != "URL" and value:
            username, password = key, value
            break
if not url or not password:
    raise SystemExit(f"credentials incomplete; keys={sorted(creds)}")
print("login_as=", username)

print("service=", subprocess.check_output(["systemctl", "is-active", "multiimageclient-ui"], text=True).strip())
print("staging=", "PRESENT" if (home / "multiimageclient-publish-staging").is_dir() else "GONE")
print("home_cookie=", "PRESENT" if (home / "grok-web-cookies.txt").is_file() else "GONE")
index = pathlib.Path("/opt/multiimageclient/Ui/wwwroot/index.html")
print("show_costs=", index.read_text(encoding="utf-8").count("show-costs") if index.is_file() else "MISSING")

cookie_jar = tempfile.NamedTemporaryFile(delete=False)
cookie_jar.close()

login_body = urllib.parse.urlencode({"username": "ernie", "password": password}).encode()
req = urllib.request.Request(
    f"{url}/api/auth/login",
    data=login_body,
    method="POST",
    headers={"Content-Type": "application/x-www-form-urlencoded"},
)
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor())
# Use curl for cookie jar reliability across redirects.
import os

login = subprocess.run(
    [
        "curl", "-sS", "-c", cookie_jar.name, "-b", cookie_jar.name,
        "-o", "/tmp/mic-login.json", "-w", "%{http_code}",
        "-X", "POST", f"{url}/api/auth/login",
        "-d", f"username={username}&password={password}",
    ],
    check=True,
    capture_output=True,
    text=True,
)
print("login_http=", login.stdout.strip())

cfg = subprocess.run(
    [
        "curl", "-sS", "-c", cookie_jar.name, "-b", cookie_jar.name,
        "-o", "/tmp/mic-cfg.json", "-w", "%{http_code}",
        f"{url}/api/config",
    ],
    check=True,
    capture_output=True,
    text=True,
)
print("config_http=", cfg.stdout.strip())
os.unlink(cookie_jar.name)

data = json.loads(pathlib.Path("/tmp/mic-cfg.json").read_text(encoding="utf-8"))
for g in data.get("generators") or []:
    key = str(g.get("key", ""))
    if "grok" in key.lower():
        problem = g.get("availabilityProblem") or g.get("problem") or ""
        print(f"{key} | {g.get('label')} | available={g.get('available')} | {problem}")
