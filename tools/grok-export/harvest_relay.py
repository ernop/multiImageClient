"""Local relay receiver for harvesting Grok Imagine REST data out of a logged-in browser.

The authoritative Grok media graph is only reachable from a logged-in grok.com
browser session (the internal `rest/media/post/list` endpoint). This tiny HTTP
server runs on loopback and accepts the harvested JSON that the browser POSTs to
it, appending it verbatim to `<archive-root>/rest_posts.jsonl`.

Loopback (`127.0.0.1`) is a "potentially trustworthy" origin, so an https grok.com
page is allowed to `fetch()` it despite mixed-content rules. CORS is wide open
because the only client is the user's own browser on their own machine.

This server writes user/archive data (personal prompts, IDs, NSFW content). Keep
its output outside the repo.

Usage:
    python tools/grok-export/harvest_relay.py --archive-root "C:\\GrokArchive\\WebExport" --port 8777

Endpoints:
    OPTIONS *          -> CORS preflight
    POST   /reset      -> truncate rest_posts.jsonl (start a fresh harvest)
    POST   /append     -> append the raw request body (expected: JSONL text) to the file
    GET    /status     -> JSON: { lines, bytes }
"""

import argparse
import json
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qs

SAFE_ID = re.compile(r"^[0-9a-fA-F-]{36}$")
SAFE_EXT = re.compile(r"^[a-z0-9]{2,5}$")


def make_handler(out_path: Path, media_dir: Path):
    class Handler(BaseHTTPRequestHandler):
        def _cors(self):
            self.send_header("Access-Control-Allow-Origin", "*")
            self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
            self.send_header("Access-Control-Allow-Headers", "content-type")

        def _send(self, code: int, body: bytes = b"", content_type: str = "text/plain"):
            self.send_response(code)
            self._cors()
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if body:
                self.wfile.write(body)

        def do_OPTIONS(self):
            self._send(204)

        def do_GET(self):
            route = urlparse(self.path).path.rstrip("/")
            if route == "/needlist":
                need = out_path.parent / "_need_images.json"
                body = need.read_bytes() if need.exists() else b"[]"
                self._send(200, body, "application/json")
                return
            if self.path.rstrip("/") == "/status":
                lines = 0
                size = 0
                if out_path.exists():
                    size = out_path.stat().st_size
                    with out_path.open("r", encoding="utf-8") as f:
                        lines = sum(1 for _ in f)
                self._send(200, json.dumps({"lines": lines, "bytes": size}).encode("utf-8"), "application/json")
            else:
                self._send(404, b"not found")

        def do_POST(self):
            length = int(self.headers.get("Content-Length", "0"))
            raw = self.rfile.read(length) if length else b""
            parsed = urlparse(self.path)
            route = parsed.path.rstrip("/")
            if route == "/media":
                q = parse_qs(parsed.query)
                mid = (q.get("id") or [""])[0]
                ext = (q.get("ext") or ["jpg"])[0].lower().lstrip(".")
                if not SAFE_ID.match(mid) or not SAFE_EXT.match(ext):
                    self._send(400, b'{"ok":false,"err":"bad id/ext"}', "application/json")
                    return
                media_dir.mkdir(parents=True, exist_ok=True)
                dest = media_dir / f"{mid}.{ext}"
                if dest.exists() and dest.stat().st_size > 0:
                    self._send(200, b'{"ok":true,"skip":true}', "application/json")
                    return
                dest.write_bytes(raw)
                self._send(200, json.dumps({"ok": True, "bytes": len(raw)}).encode("utf-8"), "application/json")
                return
            if route == "/reset":
                out_path.parent.mkdir(parents=True, exist_ok=True)
                out_path.write_bytes(b"")
                self._send(200, b'{"ok":true,"reset":true}', "application/json")
            elif route == "/append":
                out_path.parent.mkdir(parents=True, exist_ok=True)
                text = raw.decode("utf-8", errors="replace")
                if text and not text.endswith("\n"):
                    text += "\n"
                with out_path.open("a", encoding="utf-8") as f:
                    f.write(text)
                self._send(200, json.dumps({"ok": True, "appended": len(raw)}).encode("utf-8"), "application/json")
            else:
                self._send(404, b"not found")

        def log_message(self, fmt, *args):
            print("relay " + (fmt % args), flush=True)

    return Handler


def main() -> int:
    parser = argparse.ArgumentParser(description="Local loopback relay that writes harvested Grok REST data to rest_posts.jsonl")
    parser.add_argument("--archive-root", required=True, type=Path)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--media-dir", type=Path)
    parser.add_argument("--port", type=int, default=8777)
    args = parser.parse_args()

    root = args.archive_root.resolve()
    out_path = (args.out or root / "rest_posts.jsonl").resolve()
    media_dir = (args.media_dir or root / "ApiImages").resolve()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(out_path, media_dir))
    print(f"relay listening on http://127.0.0.1:{args.port} -> {out_path} | media -> {media_dir}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
