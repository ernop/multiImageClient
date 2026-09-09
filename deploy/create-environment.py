#!/usr/bin/env python3
"""Prepare, install, or update an isolated MultiImageClient environment.

Preparation creates private files only. Installation changes one new service
and adds an include to the existing hostname's TLS server block.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import subprocess
import sys
import time
import urllib.request
import uuid


# Copy provider access only. Never copy histories, profiles, personal preferences,
# output paths, webhook destinations, cookie-file paths, or the old auth secret.
PROVIDER_FIELDS = (
    "OpenAIApiKey", "AnthropicApiKey", "IdeogramApiKey", "BFLApiKey", "RecraftApiKey",
    "GoogleGeminiApiKey", "XAIGrokApiKey", "KreaApiKey", "GoogleCloudApiKey",
    "GoogleCloudLocation", "GoogleCloudProjectId", "XAIBaseUrl", "GrokWebStatsigVerificationKey",
    "GrokWebStatsigAnimationKey", "EnableB2ImageHosting", "B2KeyId", "B2ApplicationKey",
    "B2BucketId", "B2BucketName", "B2DownloadBaseUrl", "B2KeepLocalRawImages",
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def write_private(path, value):
    with path.open("x", encoding="utf-8") as stream:
        os.chmod(path, 0o600)
        stream.write(value if isinstance(value, str) else json.dumps(value, indent=2) + "\n")


def validate_id(value):
    require(re.fullmatch(r"[a-z][a-z0-9-]{0,23}", value), "Use a 1–24 character lowercase environment ID.")
    return value


def prepare(args):
    validate_id(args.id)
    require(1 <= len(args.name.strip()) <= 80 and not any(ord(c) < 32 for c in args.name), "Invalid environment name.")
    require(1024 <= args.port <= 65535 and args.port != 5960, "Choose a separate unprivileged port, not 5960.")
    require(256 <= args.memory_high_mib < args.memory_max_mib <= 2560, "Set explicit memory limits: 256 <= high < max <= 2560 MiB.")
    require(1 <= args.max_requests <= 4, "Use 1–4 aggregate provider requests for the new environment.")
    source = json.loads(Path(args.settings_source).read_text(encoding="utf-8-sig"))
    template_path = Path(__file__).resolve().parents[1] / "MultiImageClient" / "settings - Fill this in and rename it.json"
    settings = json.loads(template_path.read_text(encoding="utf-8-sig"))
    # Remove descriptive placeholders. Keep application defaults such as annotation placement.
    for key in list(settings):
        if isinstance(settings[key], str) and (settings[key].startswith(("Optional:", "REQUIRED"))
                or key.endswith(("Path", "File", "Folder"))): settings[key] = ""
    for key in PROVIDER_FIELDS:
        if key in source: settings[key] = source[key]
    service = "multiimageclient-env-" + args.id
    root = "/var/lib/" + service
    config = "/etc/" + service
    path = getattr(args, "slug", None) or secrets.token_hex(16)
    require(re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", path), "Invalid URL name.")
    base = "https://multiimageclient.alpha.fuseki.net/" + path
    settings.update({
        "UiEnvironmentId": args.id, "UiEnvironmentName": args.name.strip(), "UiPublicBaseUrl": base,
        "UiAuthFilePath": config + "/ui-auth.json", "UiLoginLinksFilePath": root + "/login-links.json",
        "ImageDownloadBaseFolder": root + "/saves", "LogFilePath": root + "/logs/multiimageclient.log",
        "GenerationArchiveDbPath": root + "/saves/generation-history.sqlite3", "UiCommunityDbPath": root + "/saves/ui-community.sqlite3",
        "UiMaxConcurrentJobs": 1, "UiMaxConcurrentGenerators": args.max_requests, "UiMaxPendingJobs": 16,
        "UiTargetConcurrency": {lane: 1 for lane in ["openai", "xai-api", "grok-web-ws", "google", "bfl", "ideogram", "recraft", "krea", "comfyui"]},
        "UiMinimumFreeDiskBytes": 3221225472, "EnableGenerationArchive": True, "EnableLocalGenerators": False,
        "FlatImageMirrorPath": "", "TypedPromptsAppendFile": "", "PromptFiles": [],
        "DiscordVibecodersWebhookUrl": "", "FableBotDiscordBotToken": "", "FableBotDiscordChannelId": "",
    })
    if getattr(args, "managed", False):
        settings.update({"UiAuthFilePath": "/var/lib/multiimageclient-control/auth.json",
            "UiLoginLinksFilePath": "/var/lib/multiimageclient-control/state/links.json",
            "UiEnvironmentRegistryPath": "/var/lib/multiimageclient-control/state/registry.json",
            "UiEnvironmentController": False})
    # Only explicit consumer-session opt-in copies the cookie file into the new account's private configuration.
    cookie_source = None
    if args.copy_grok_session:
        cookie_source = Path(source.get("GrokWebCookiePath", ""))
        require(cookie_source.is_file(), "The selected Grok session cookie file is missing.")
        settings["GrokWebCookiePath"] = config + "/grok-cookies.txt"
    defaults = args.providers.split(',') if args.providers else []
    require(len(defaults) == len(set(defaults)) and all(re.fullmatch(r"[a-z0-9-]+", k) for k in defaults), "Invalid provider default list.")
    account_id = uuid.uuid4().hex
    token_secret = secrets.token_urlsafe(32)
    password = secrets.token_urlsafe(32)
    salt = secrets.token_bytes(16)
    digest = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, 600000)
    password_hash = "pbkdf2-sha256$600000$" + base64.b64encode(salt).decode() + "$" + base64.b64encode(digest).decode()
    auth = {"version": 2, "enabled": True, "secret": secrets.token_hex(32),
            "accounts": [{"username": "ernieMultiZone", "passwordHash": password_hash}]}
    links = {"version": 1, "accounts": [{"id": account_id, "login": "ernieMultiZone", "displayName": "Ernie",
            "tokenHash": hashlib.sha256(token_secret.encode()).hexdigest(), "revoked": False}], "defaultGenerators": defaults}
    out = Path(args.output).resolve()
    require(not out.exists(), "The output directory already exists. Choose a fresh private directory.")
    out.mkdir(mode=0o700, parents=True)
    manifest = {"id": args.id, "name": args.name.strip(), "service": service, "port": args.port,
        "memoryHighMiB": args.memory_high_mib, "memoryMaxMiB": args.memory_max_mib,
        "nginxSite": args.nginx_site, "dotnet": args.dotnet, "privatePath": path}
    manifest["managed"] = bool(getattr(args, "managed", False))
    write_private(out / "manifest.json", manifest)
    write_private(out / "settings.json", settings)
    if not manifest["managed"]: write_private(out / "ui-auth.json", auth)
    if not manifest["managed"]: write_private(out / "login-links.json", links)
    if not manifest["managed"]:
        write_private(out / "owner-access.txt", f"Owner login link: {base}/enter.html#{account_id}.{token_secret}\n"
                      f"Recovery username: ernieMultiZone\nRecovery password: {password}\n")
    if cookie_source:
        write_private(out / "grok-cookies.txt", cookie_source.read_text())
    print("Prepared private environment files. Managed environments use the existing global accounts.")


def tls_include(text, include_line):
    # Parse braces while ignoring comments and quoted nginx strings, retaining original offsets.
    pattern = r'''\#[^\n]*|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[{}]'''
    stack, matches = [], []
    for match in re.finditer(pattern, text):
        token = match.group()
        if token == "{": stack.append(match.start())
        elif token == "}":
            require(bool(stack), "Unbalanced nginx configuration.")
            start = stack.pop()
            before = text[:start].rstrip()
            if not re.search(r"\bserver$", before): continue
            body = text[start + 1:match.start()]
            if (re.search(r"server_name\s+multiimageclient\.alpha\.fuseki\.net\s*;", body)
                    and re.search(r"listen\s+(?:\[::\]:)?443\b", body)):
                matches.append(match.start())
    require(not stack and len(matches) == 1, "Expected exactly one existing MultiImageClient TLS server block.")
    require(include_line not in text, "The environment include already exists.")
    at = matches[0]
    return text[:at] + "    " + include_line + "\n" + text[at:]


def locations(manifest):
    secret, port = manifest["privatePath"], manifest["port"]
    require(re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", secret), "Invalid private route.")
    common = """        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_request_buffering off;
        proxy_read_timeout 300s;
"""
    result = f"    location = /{secret} {{ return 301 /{secret}/; }}\n"
    for endpoint in ("login", "link"):
        result += (f"    location = /{secret}/api/auth/{endpoint} {{\n"
                   "        limit_req zone=multiimage_login burst=3 nodelay;\n"
                   f"        proxy_pass http://127.0.0.1:{port}/api/auth/{endpoint};\n" + common + "    }\n")
    result += (f"    location /{secret}/ {{\n        limit_req zone=multiimage_general burst=1500 nodelay;\n"
               f"        proxy_pass http://127.0.0.1:{port}/;\n" + common + "    }\n")
    return result


def run(*args):
    # nginx diagnostics can contain the private path. Keep command output out of normal logs.
    result = subprocess.run(args, capture_output=True, text=True)
    require(result.returncode == 0, f"Command failed: {args[0]}. Inspect its diagnostics privately on the server.")


def create_configuration_directory(path, group):
    path.mkdir(mode=0o750)
    os.chown(path, 0, group)
    # systemd's UMask=0077 otherwise turns mkdir(0750) into 0700.
    os.chmod(path, 0o750)


def install(args):
    require(sys.platform == "linux" and os.geteuid() == 0, "Installation requires Linux root.")
    import pwd
    bundle = Path(args.bundle).resolve()
    manifest = json.loads((bundle / "manifest.json").read_text())
    ident = validate_id(manifest["id"])
    service = "multiimageclient-env-" + ident
    require(manifest["service"] == service, "The service identity does not match the environment.")
    port = manifest["port"]
    require(isinstance(port, int) and 1024 <= port <= 65535 and port != 5960, "Invalid environment port.")
    publish = Path(args.publish).resolve()
    require((publish / "MultiImageClient.dll").is_file() and (publish / "Ui/wwwroot/people.html").is_file(), "Publish the environment-enabled application first.")
    target, config, data = (Path(prefix) / service for prefix in ("/opt", "/etc", "/var/lib"))
    unit = Path("/etc/systemd/system") / (service + ".service")
    include = Path("/etc/nginx") / (service + ".locations")
    for path in (target, config, data, unit, include): require(not path.exists(), "An environment target already exists; refusing to overwrite it.")
    account = "mic-" + ident
    try: pwd.getpwnam(account)
    except KeyError: pass
    else: raise ValueError("The environment service account already exists.")
    with socket.socket() as listener: listener.bind(("127.0.0.1", port))
    require(shutil.disk_usage("/var/lib").free >= 4 * 1024**3, "Provisioning requires at least 4 GiB free disk, including the existing 3 GiB reserve.")
    site = Path(manifest["nginxSite"]).resolve()
    require(any(site.is_relative_to(Path(root)) for root in ("/etc/nginx/sites-available", "/etc/nginx/sites-enabled")),
            "Select the existing named nginx site under sites-available or sites-enabled.")
    original = site.read_text()
    replacement = tls_include(original, f"include {include};")
    require(str(manifest["dotnet"]).startswith("/") and Path(manifest["dotnet"]).is_file(), "Invalid dotnet executable.")
    require(re.fullmatch(r"/[A-Za-z0-9_./-]+", manifest["dotnet"]), "The dotnet path contains unsupported characters.")
    high, maximum = manifest["memoryHighMiB"], manifest["memoryMaxMiB"]
    require(isinstance(high, int) and isinstance(maximum, int) and 256 <= high < maximum <= 2560, "Invalid memory budget.")
    settings = json.loads((bundle / "settings.json").read_text())
    expected = {"UiEnvironmentId": ident, "UiAuthFilePath": str(config / "ui-auth.json"),
        "UiLoginLinksFilePath": str(data / "login-links.json"), "ImageDownloadBaseFolder": str(data / "saves"),
        "LogFilePath": str(data / "logs/multiimageclient.log"),
        "GenerationArchiveDbPath": str(data / "saves/generation-history.sqlite3"),
        "UiCommunityDbPath": str(data / "saves/ui-community.sqlite3"),
        "UiPublicBaseUrl": "https://multiimageclient.alpha.fuseki.net/" + manifest["privatePath"]}
    if manifest.get("managed"):
        expected.update({"UiAuthFilePath": "/var/lib/multiimageclient-control/auth.json",
            "UiLoginLinksFilePath": "/var/lib/multiimageclient-control/state/links.json",
            "UiEnvironmentRegistryPath": "/var/lib/multiimageclient-control/state/registry.json", "UiEnvironmentController": False})
    require(all(settings.get(k) == v for k, v in expected.items()), "The bundle's data or authentication paths do not match its environment.")
    run("nginx", "-t")
    # Nothing before this point changes the running host.
    run("useradd", "--system", "--user-group", "--home-dir", str(data), "--shell", "/usr/sbin/nologin", account)
    if manifest.get("managed"): run("usermod", "-a", "-G", "mic-auth", account)
    user = pwd.getpwnam(account)
    create_configuration_directory(config, user.pw_gid)
    shutil.copyfile(bundle / "manifest.json", config / "manifest.json"); os.chmod(config / "manifest.json", 0o600)
    data.mkdir(mode=0o750); os.chown(data, user.pw_uid, user.pw_gid)
    for subdir in ("saves", "logs"):
        folder = data / subdir; folder.mkdir(mode=0o750); os.chown(folder, user.pw_uid, user.pw_gid)
    for filename in ("settings.json", "ui-auth.json", "grok-cookies.txt"):
        if (bundle / filename).exists():
            shutil.copyfile(bundle / filename, config / filename)
            os.chown(config / filename, 0, user.pw_gid); os.chmod(config / filename, 0o640)
    if not manifest.get("managed"):
        shutil.copyfile(bundle / "login-links.json", data / "login-links.json")
        os.chown(data / "login-links.json", user.pw_uid, user.pw_gid); os.chmod(data / "login-links.json", 0o600)
    shutil.copytree(publish, target, ignore=shutil.ignore_patterns("settings.json"))
    for file in target.rglob("*"):
        os.chmod(file, 0o755 if file.is_dir() or os.access(file, os.X_OK) else 0o644)
    os.chmod(target, 0o755)
    unit.write_text(f"""[Unit]
Description=MultiImageClient environment {ident}
After=network-online.target
[Service]
Type=simple
User={account}
Group={account}
WorkingDirectory={target}
Environment=MULTIIMAGECLIENT_SETTINGS={config}/settings.json
ExecStart={manifest['dotnet']} {target}/MultiImageClient.dll --ui --ui-port {port} --ui-no-open
Restart=on-failure
RestartSec=5
UMask=0077
MemoryHigh={high}M
MemoryMax={maximum}M
CPUQuota=100%
TasksMax=256
Nice=10
IOWeight=25
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ProtectHome=read-only
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictSUIDSGID=yes
CapabilityBoundingSet=
ReadWritePaths={data}
[Install]
WantedBy=multi-user.target
""")
    os.chmod(unit, 0o644)
    run("systemctl", "daemon-reload")
    run("systemctl", "start", service)
    ready = False
    for _ in range(30):
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/healthz", timeout=2) as response:
                ready = response.status == 200
        except OSError: pass
        if ready: break
        time.sleep(1)
    require(ready, "The new environment failed its health check. Its public route was not installed.")
    include.write_text(locations(manifest)); os.chmod(include, 0o600)
    # Preserve the old file for exact recovery. Never print its contents or private routes.
    backup_root = Path("/etc/nginx/multiimageclient-backups")
    backup_root.mkdir(mode=0o700, exist_ok=True)
    backup = backup_root / (site.name + ".before-" + service)
    require(not backup.exists(), "The nginx backup already exists.")
    shutil.copy2(site, backup)
    try:
        site.write_text(replacement)
        run("nginx", "-t")
        run("systemctl", "reload", "nginx")
    except Exception:
        shutil.copy2(backup, site)
        raise
    run("systemctl", "enable", service)
    print("Installed the new environment. The original service was not restarted. Managed environments use the existing global login.")


def update(args):
    require(sys.platform == "linux" and os.geteuid() == 0, "Updating requires Linux root.")
    ident = validate_id(args.id)
    service = "multiimageclient-env-" + ident
    target = Path("/opt") / service
    previous = target.with_name(service + ".previous")
    staged = target.with_name(service + ".staged")
    manifest = json.loads((Path("/etc") / service / "manifest.json").read_text())
    require(manifest["id"] == ident and manifest["service"] == service, "Environment identity mismatch.")
    require(target.is_dir() and not previous.exists() and not staged.exists(), "Resolve an existing previous/staged release before updating.")
    publish = Path(args.publish).resolve()
    require((publish / "MultiImageClient.dll").is_file() and (publish / "Ui/wwwroot/people.html").is_file(), "Missing environment-enabled publish.")
    require(shutil.disk_usage(target).free >= 4 * 1024**3, "Updating requires at least 4 GiB free disk.")
    shutil.copytree(publish, staged, ignore=shutil.ignore_patterns("settings.json"))
    os.chmod(staged, 0o755)
    for file in staged.rglob("*"):
        os.chmod(file, 0o755 if file.is_dir() or os.access(file, os.X_OK) else 0o644)
    run("systemctl", "stop", service)
    target.rename(previous)
    staged.rename(target)
    run("systemctl", "start", service)
    run("systemctl", "is-active", service)
    ready = False
    for _ in range(30):
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{manifest['port']}/healthz", timeout=2) as response:
                ready = response.status == 200
        except OSError: pass
        if ready: break
        time.sleep(1)
    require(ready, "The selected environment failed its health check. Its previous binary remains available for recovery.")
    print("Updated only the selected environment. The previous binary remains in its .previous directory for operator recovery.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prep = commands.add_parser("prepare")
    for arg in ("id", "name", "settings-source", "output", "nginx-site"):
        prep.add_argument("--" + arg, required=True)
    prep.add_argument("--slug")
    prep.add_argument("--managed", action="store_true")
    prep.add_argument("--dotnet", default="/home/tparkour/.dotnet/dotnet")
    prep.add_argument("--port", type=int, required=True)
    prep.add_argument("--memory-high-mib", type=int, required=True)
    prep.add_argument("--memory-max-mib", type=int, required=True)
    prep.add_argument("--max-requests", type=int, required=True)
    prep.add_argument("--providers", required=True, help="Comma-separated default provider keys; an empty string selects none.")
    prep.add_argument("--copy-grok-session", action="store_true")
    apply = commands.add_parser("install")
    apply.add_argument("--bundle", required=True)
    apply.add_argument("--publish", required=True)
    upd = commands.add_parser("update")
    upd.add_argument("--id", required=True)
    upd.add_argument("--publish", required=True)
    args = parser.parse_args()
    try:
        if args.command == "prepare": prepare(args)
        elif args.command == "install": install(args)
        else: update(args)
    except (ValueError, OSError, KeyError, json.JSONDecodeError) as exc:
        # OS errors may contain a private filename. Do not include exception text.
        print(str(exc) if isinstance(exc, ValueError) and not isinstance(exc, json.JSONDecodeError)
              else "Environment operation failed. Inspect the private files on the host.", file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
