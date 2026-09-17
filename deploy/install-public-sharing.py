"""Install the original MIC public-sharing route without changing its private route."""
import argparse
import datetime
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
from urllib.parse import urlparse

HOST = "multiimageclient.alpha.fuseki.net"
INCLUDE = Path("/etc/nginx/multiimageclient-public-sharing.locations")
SETTINGS = Path("/etc/multiimageclient/settings.json")


def route_text():
    return """# Public prompt capabilities for the original MultiImageClient environment.
location /shared/original/ {
    limit_req zone=multiimage_general burst=1500 nodelay;
    proxy_pass http://127.0.0.1:5960/public/;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto https;
    proxy_request_buffering off;
    proxy_read_timeout 300s;
    client_max_body_size 16k;
    access_log off;
}
"""


def checked(*args):
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(f"{args[0]} failed; inspect diagnostics privately on the server.")


def install(server_name, channel_name):
    if os.name != "posix" or os.geteuid() != 0:
        raise ValueError("Run as Linux root.")
    if not server_name.strip() or not channel_name.strip():
        raise ValueError("Specify the verified Discord server and channel names.")
    settings = json.loads(SETTINGS.read_text())
    if urlparse(settings.get("UiPublicBaseUrl", "")).hostname != HOST:
        raise ValueError("The settings do not identify the original MultiImageClient host.")
    spec = importlib.util.spec_from_file_location("environment_deploy", Path(__file__).with_name("create-environment.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    candidates = {p.resolve() for p in Path("/etc/nginx/sites-enabled").iterdir()
                  if p.is_file() and f"server_name {HOST}" in p.read_text()}
    if len(candidates) != 1:
        raise ValueError("Expected one existing named MultiImageClient vhost.")
    site = candidates.pop()
    original = site.read_text()
    include_line = f"include {INCLUDE};"
    replacement = original if include_line in original else module.tls_include(original, include_line)
    expected = route_text()
    if INCLUDE.exists() and INCLUDE.read_text() != expected:
        raise ValueError("A different public-sharing route already exists; inspect it before replacing.")
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d-%H%M%S")
    backup = Path("/etc/nginx/multiimageclient-backups") / ("public-sharing-" + stamp)
    backup.mkdir(parents=True, mode=0o700)
    os.chmod(backup, 0o700)
    shutil.copy2(site, backup / "vhost.conf")
    shutil.copy2(SETTINGS, backup / "settings.json")
    had_include = INCLUDE.exists()
    settings["UiPublicShareBaseUrl"] = f"https://{HOST}/shared/original"
    settings["DiscordVibecodersServerName"] = server_name.strip()
    settings["DiscordVibecodersChannelName"] = channel_name.strip().lstrip("#")
    try:
        INCLUDE.write_text(expected)
        os.chmod(INCLUDE, 0o644)
        site.write_text(replacement)
        checked("nginx", "-t")
        # Writing in place preserves the settings file's owner, group, and access mode.
        SETTINGS.write_text(json.dumps(settings, indent=2) + "\n")
        checked("systemctl", "reload", "nginx")
    except Exception:
        shutil.copy2(backup / "vhost.conf", site)
        shutil.copy2(backup / "settings.json", SETTINGS)
        if not had_include and INCLUDE.exists():
            INCLUDE.unlink()
        checked("nginx", "-t")
        checked("systemctl", "reload", "nginx")
        raise
    print("Installed the original MIC public-sharing route. Run the normal original-service redeploy to load its settings.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-name", required=True)
    parser.add_argument("--channel-name", required=True)
    args = parser.parse_args()
    install(args.server_name, args.channel_name)
