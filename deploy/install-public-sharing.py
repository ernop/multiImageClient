"""Install public sharing for an explicitly selected MIC instance without changing its existing route."""
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
THREAD_STORE = Path("/var/lib/multiimageclient-discord-threads")


def route_text(environment="original", port=5960):
    return """# Public prompt capabilities for the selected MultiImageClient environment.
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
""".replace("/shared/original/", f"/shared/{environment}/").replace("127.0.0.1:5960", f"127.0.0.1:{port}")


def checked(*args):
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(f"{args[0]} failed; inspect diagnostics privately on the server.")


def provision_thread_store(service):
    import grp
    group_name = "mic-discord-threads"
    try:
        group = grp.getgrnam(group_name)
    except KeyError:
        checked("groupadd", "--system", group_name)
        group = grp.getgrnam(group_name)
    if THREAD_STORE.is_symlink():
        raise ValueError("The shared thread directory must not be a symlink.")
    THREAD_STORE.mkdir(mode=0o2770, exist_ok=True)
    os.chown(THREAD_STORE, 0, group.gr_gid)
    os.chmod(THREAD_STORE, 0o2770)
    dropin = Path("/etc/systemd/system") / (service + ".service.d")
    dropin.mkdir(exist_ok=True)
    config = dropin / "public-sharing.conf"
    expected = f"[Service]\nSupplementaryGroups={group_name}\nReadWritePaths={THREAD_STORE}\n"
    if config.exists() and config.read_text() != expected:
        raise ValueError("A different public-sharing service override already exists.")
    config.write_text(expected)
    os.chmod(config, 0o644)
    checked("systemctl", "daemon-reload")


def install(environment="original"):
    global SETTINGS, INCLUDE
    service = "multiimageclient-ui"
    port = 5960
    if environment == "vibecoders-ai-generation":
        service = "multiimageclient-env-vibecoders-ai-generation"
        SETTINGS = Path("/etc") / service / "settings.json"
        INCLUDE = Path("/etc/nginx/multiimageclient-public-sharing-vibecoders.locations")
        manifest = json.loads((SETTINGS.parent / "manifest.json").read_text())
        if manifest["id"] != environment or manifest["service"] != service or manifest["port"] != 5961:
            raise ValueError("The selected environment identity does not match.")
        port = 5961
    elif environment != "original":
        raise ValueError("Select an explicitly approved production instance.")
    if os.name != "posix" or os.geteuid() != 0:
        raise ValueError("Run as Linux root.")
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
    expected = route_text(environment, port)
    if INCLUDE.exists() and INCLUDE.read_text() != expected:
        raise ValueError("A different public-sharing route already exists; inspect it before replacing.")
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d-%H%M%S")
    backup = Path("/etc/nginx/multiimageclient-backups") / ("public-sharing-" + environment + "-" + stamp)
    backup.mkdir(parents=True, mode=0o700)
    os.chmod(backup, 0o700)
    shutil.copy2(site, backup / "vhost.conf")
    shutil.copy2(SETTINGS, backup / "settings.json")
    had_include = INCLUDE.exists()
    settings["UiPublicShareBaseUrl"] = f"https://{HOST}/shared/{environment}"
    settings["DiscordVibecodersThreadStorePath"] = str(THREAD_STORE)
    provision_thread_store(service)
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
    print("Installed public route and shared thread storage for " + environment + ". Restart this selected app to load its settings. No content was published.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--environment", choices=["original", "vibecoders-ai-generation"], required=True)
    args = parser.parse_args()
    install(args.environment)
