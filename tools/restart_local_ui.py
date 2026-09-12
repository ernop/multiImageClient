"""Build and restart only this checkout's Linux loopback UI."""

import fcntl
import os
from pathlib import Path
import re
import select
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


def parse_port(value):
    if not re.fullmatch(r"[0-9]{1,5}", value) or not 1 <= int(value) <= 65535:
        raise ValueError("MIC_UI_PORT must be an integer from 1 to 65535.")
    return int(value)


def listener_pid(port):
    result = subprocess.run(
        ["ss", "-H", "-ltnp", f"sport = :{port}"],
        check=True, capture_output=True, text=True,
    )
    lines = result.stdout.splitlines()
    if not lines:
        return None
    # Refuse wildcard, foreign, hidden-owner, and shared listeners.
    if len(lines) != 1 or len(lines[0].split()) < 4 or lines[0].split()[3] != f"127.0.0.1:{port}":
        raise RuntimeError(f"Port {port} does not have one loopback listener.")
    pids = re.findall(r"pid=(\d+),", lines[0])
    if len(pids) != 1:
        raise RuntimeError(f"Cannot prove the process owning port {port}.")
    return int(pids[0])


def validate_process(pid, root, port, proc_root=Path("/proc")):
    process = proc_root / str(pid)
    if process.stat().st_uid != os.getuid():
        raise RuntimeError("The listener belongs to another user.")
    cwd = Path(os.readlink(process / "cwd"))
    args = (process / "cmdline").read_bytes().decode().rstrip("\0").split("\0")
    exe = os.readlink(process / "exe").removesuffix(" (deleted)")
    if cwd not in (root, root / "MultiImageClient"):
        raise RuntimeError("The listener belongs to another checkout.")
    # Match argv tokens, never substrings in an arbitrary process command.
    if Path(exe).name == "dotnet":
        target = Path(args[1]) if len(args) > 1 else Path()
        options = args[2:]
        valid_name = target.name == "MultiImageClient.dll"
    else:
        target = Path(exe)
        options = args[1:]
        valid_name = target.name == "MultiImageClient"
    target = (cwd / target).resolve()
    allowed_roots = (root / "MultiImageClient" / "bin", root / ".local-ui" / str(port))
    if not valid_name or not any(target.is_relative_to(base) for base in allowed_roots):
        raise RuntimeError("The listener is not this checkout's UI executable.")
    if options not in (["--ui", "--ui-no-open"], ["--ui"]):
        if options not in (
            ["--ui", "--ui-port", str(port), "--ui-no-open"],
            ["--ui", "--ui-port", str(port)],
        ):
            raise RuntimeError("The listener has an unrecognized UI command.")
    elif port != 5960:
        raise RuntimeError("The listener's default port does not match MIC_UI_PORT.")


def stop_process(pidfd):
    # A pidfd keeps the signal tied to this process even if its PID is reused.
    try:
        signal.pidfd_send_signal(pidfd, signal.SIGTERM)
    except ProcessLookupError:
        return
    if not select.select([pidfd], [], [], 40)[0]:
        signal.pidfd_send_signal(pidfd, signal.SIGKILL)
        if not select.select([pidfd], [], [], 5)[0]:
            raise RuntimeError("The local UI process did not stop.")


def healthy(port):
    # Ignore proxy settings; do not follow redirects to another server.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):
            return None

    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        with opener.open(f"http://127.0.0.1:{port}/healthz", timeout=2) as response:
            return response.status == 200 and response.read(3) == b"ok"
    except (OSError, urllib.error.URLError):
        return False


def restart(root, port, dotnet, log):
    state = root / ".local-ui" / str(port)
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (state / "restart.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise RuntimeError("A local UI restart is already running.") from error
        old_pid = listener_pid(port)
        old_fd = os.pidfd_open(old_pid) if old_pid is not None else None
        release = None
        child = None
        succeeded = False
        try:
            if old_pid is not None:
                validate_process(old_pid, root, port)
            release = Path(tempfile.mkdtemp(prefix="release-", dir=state))
            # Keep every build intermediate and DLL separate from the running app.
            subprocess.run([
                dotnet, "publish", str(root / "MultiImageClient/MultiImageClient.csproj"),
                "-c", "Release", "--nologo", "--artifacts-path", str(release / "build"),
                "--output", str(release / "app"),
            ], cwd=root, check=True)
            assembly = release / "app" / "MultiImageClient.dll"
            if not assembly.is_file():
                raise RuntimeError("The publish did not produce MultiImageClient.dll.")
            # An invalid log path must not stop a working server.
            with log.open("ab") as output:
                if listener_pid(port) != old_pid:
                    raise RuntimeError("The port owner changed during the build.")
                if old_fd is not None:
                    validate_process(old_pid, root, port)
                    stop_process(old_fd)
                if listener_pid(port) is not None:
                    raise RuntimeError("The local UI port is still occupied.")
                child = subprocess.Popen([
                    dotnet, str(assembly), "--ui", "--ui-port", str(port), "--ui-no-open",
                ], cwd=root, stdin=subprocess.DEVNULL, stdout=output, stderr=output,
                    start_new_session=True)
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                if child.poll() is not None:
                    raise RuntimeError(f"The local UI exited with code {child.returncode}; see {log}.")
                owner = listener_pid(port)
                if owner is not None and owner != child.pid:
                    raise RuntimeError("Another process took the local UI port.")
                if owner == child.pid and healthy(port) and child.poll() is None:
                    (state / "server.pid").write_text(f"{child.pid}\n")
                    succeeded = True
                    break
                time.sleep(0.25)
            if not succeeded:
                raise RuntimeError(f"The local UI did not become healthy; see {log}.")
            # Only generated releases for this port are disposable.
            shutil.rmtree(release / "build")
            for previous in state.glob("release-*"):
                if previous != release and previous.is_dir() and not previous.is_symlink():
                    shutil.rmtree(previous)
            print(f"local UI listening on 127.0.0.1:{port} pid={child.pid}")
        finally:
            if old_fd is not None:
                os.close(old_fd)
            if not succeeded:
                if child is not None and child.poll() is None:
                    child.terminate()
                    try:
                        child.wait(timeout=40)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait(timeout=5)
                if release is not None:
                    shutil.rmtree(release)


def main():
    if len(sys.argv) != 1:
        raise ValueError("Use MIC_UI_PORT and MIC_UI_LOCAL_LOG to configure this launcher.")
    if not sys.platform.startswith("linux") or not hasattr(os, "pidfd_open"):
        raise RuntimeError("This launcher requires Linux and Python with pidfd support.")
    os.umask(0o077)
    root = Path(__file__).resolve().parent.parent
    port = parse_port(os.environ.get("MIC_UI_PORT", "5960"))
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        dotnet = str(Path(os.environ.get("DOTNET_ROOT", str(Path.home() / ".dotnet"))) / "dotnet")
    if not os.access(dotnet, os.X_OK) or shutil.which("ss") is None:
        raise RuntimeError("Install .NET 10 and ss before restarting the local UI.")
    os.environ.setdefault("DOTNET_EnableWriteXorExecute", "0")
    suffix = "" if port == 5960 else f"-{port}"
    log = Path(os.environ.get("MIC_UI_LOCAL_LOG", f"/tmp/mic-ui-local{suffix}.log"))
    restart(root, port, dotnet, log)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
