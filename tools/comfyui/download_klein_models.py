#!/usr/bin/env python3
"""Resumable downloader for the local FLUX.2 Klein 4B ComfyUI model files.

Dependency-free (Python 3 stdlib only) — run it with any python3, in a console,
so you can watch and manage the (multi-GB) downloads directly.

These files are served from Hugging Face's Xet backend via SHORT-LIVED presigned
URLs (us.aws.cdn.hf.co). The trick that makes a plain downloader work on a slow
link: every (re)connect requests the ORIGINAL huggingface.co/.../resolve/... URL,
which mints a FRESH presigned redirect each time. So when a presigned URL expires
mid-download (what made aria2c die with 403), we just re-request and resume via an
HTTP Range header from the byte we left off at. Already-complete files are skipped.

Examples:
    python3 download_klein_models.py                 # uncensored (default)
    python3 download_klein_models.py --mode standard
    python3 download_klein_models.py --mode both
    python3 download_klein_models.py --comfyui ~/ComfyUI --list
"""

import argparse
import os
import socket
import sys
import time
import urllib.request
import urllib.error

# This host's IPv6 route is broken (SYN never completes), and Python's urllib
# tries IPv6 first with no Happy-Eyeballs fallback, so it hangs. Force IPv4.
_orig_getaddrinfo = socket.getaddrinfo
def _getaddrinfo_ipv4(host, port, family=0, *args, **kwargs):
    return _orig_getaddrinfo(host, port, socket.AF_INET, *args, **kwargs)
socket.getaddrinfo = _getaddrinfo_ipv4

HF = "https://huggingface.co"
KLEIN = f"{HF}/Comfy-Org/vae-text-encorder-for-flux-klein-4b/resolve/main/split_files"
# Cordux/flux2-klein-4B-uncensored-text-encoder is gated (401 for anon); use the
# ungated WeReCooking mirror, which hosts the identical qwen3-4b-abl-q4_0.gguf.
UNC = f"{HF}/WeReCooking/flux2-klein-4B-uncensored-text-encoder/resolve/main"

# key -> (url, dest-subdir-under-models, dest-filename, approx GB, mode-groups)
FILES = {
    "diffusion": (f"{KLEIN}/diffusion_models/flux-2-klein-4b.safetensors",
                  "diffusion_models", "flux-2-klein-4b.safetensors", 7.2, {"standard", "uncensored", "both"}),
    "vae": (f"{KLEIN}/vae/flux2-vae.safetensors",
            "vae", "flux2-vae.safetensors", 0.4, {"standard", "uncensored", "both"}),
    "encoder_standard": (f"{KLEIN}/text_encoders/qwen_3_4b.safetensors",
                         "text_encoders", "qwen_3_4b.safetensors", 8.0, {"standard", "both"}),
    "encoder_uncensored": (f"{UNC}/qwen3-4b-abl-q4_0.gguf",
                           "text_encoders", "qwen3-4b-abl-q4_0.gguf", 2.5, {"uncensored", "both"}),
}

CHUNK = 1 << 20
HEADERS = {"User-Agent": "klein-dl/3.0", "Accept-Encoding": "identity"}


def human(n):
    n = float(n)
    for u in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or u == "TB":
            return f"{n:.1f}{u}"
        n /= 1024


def remote_size(url):
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, headers={**HEADERS, "Range": "bytes=0-0"})
            with urllib.request.urlopen(req, timeout=60) as r:
                cr = r.headers.get("Content-Range")
                if cr and "/" in cr:
                    tot = cr.rsplit("/", 1)[1]
                    if tot.isdigit():
                        return int(tot)
                cl = r.headers.get("Content-Length")
                if cl:
                    return int(cl)
        except Exception:
            time.sleep(2)
    return None


def download(url, dest, total):
    part = dest + ".part"
    if os.path.exists(dest) and total and os.path.getsize(dest) == total:
        print(f"  already complete: {os.path.basename(dest)} ({human(total)})")
        return
    os.makedirs(os.path.dirname(dest), exist_ok=True)

    attempt = 0
    stall = 0
    while True:
        have = os.path.getsize(part) if os.path.exists(part) else 0
        if total and have >= total:
            break
        attempt += 1
        # Always hit the ORIGINAL hf.co url -> fresh presigned redirect each time.
        headers = dict(HEADERS)
        if have:
            headers["Range"] = f"bytes={have}-"
        try:
            req = urllib.request.Request(url, headers=headers)
            with urllib.request.urlopen(req, timeout=120) as r:
                mode = "ab" if (have and r.status == 206) else "wb"
                if mode == "wb":
                    have = 0
                t0 = time.time()
                last = t0
                base = have
                with open(part, mode) as f:
                    while True:
                        buf = r.read(CHUNK)
                        if not buf:
                            break
                        f.write(buf)
                        have += len(buf)
                        now = time.time()
                        if now - last >= 0.5:
                            spd = (have - base) / max(now - t0, 1e-6)
                            pct = f"{have/total*100:5.1f}%" if total else "  ?  "
                            eta = ((total - have) / spd) if (total and spd > 0) else 0
                            sys.stdout.write(
                                f"\r  {os.path.basename(dest)[:30]:30} {pct} "
                                f"{human(have):>9}/{human(total) if total else '?':>9} "
                                f"{human(spd)}/s ETA {int(eta//3600)}h{int(eta%3600//60):02d}m   ")
                            sys.stdout.flush()
                            last = now
            sys.stdout.write("\n")
            stall = 0
        except (urllib.error.URLError, urllib.error.HTTPError, ConnectionError,
                TimeoutError, OSError) as e:
            got = os.path.getsize(part) if os.path.exists(part) else 0
            # if we made no progress several times in a row, back off harder
            stall = stall + 1 if got <= have else 0
            wait = min(30, 2 ** min(stall, 5))
            sys.stdout.write(f"\n  ! {type(e).__name__}: {e}; re-resolving & resuming "
                             f"from {human(got)} in {wait}s\n")
            time.sleep(wait)

    os.replace(part, dest)
    print(f"  done: {dest} ({human(os.path.getsize(dest))})")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--comfyui", default=os.path.expanduser("~/ComfyUI"))
    ap.add_argument("--mode", choices=["uncensored", "standard", "both"], default="uncensored")
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()

    models = os.path.join(os.path.abspath(os.path.expanduser(args.comfyui)), "models")
    plan = [(k, *v) for k, v in FILES.items() if args.mode in v[4]]
    print(f"ComfyUI models dir : {models}\nMode               : {args.mode}")
    approx = 0.0
    for key, url, sub, name, gb, _ in plan:
        approx += gb
        print(f"  - {sub}/{name}  (~{gb:g} GB)")
    print(f"Approx total       : ~{approx:g} GB\n")
    if args.list:
        return

    for key, url, sub, name, gb, _ in plan:
        dest = os.path.join(models, sub, name)
        print(f"[{key}] {url}")
        download(url, dest, remote_size(url))

    print("\nAll requested files present. Next: export your ComfyUI workflow in API "
          "format with {{PROMPT}} in the positive prompt, then set ComfyUIBaseUrl + "
          "ComfyUIFlux2KleinWorkflowPath in settings.json.")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\ninterrupted — rerun to resume from where it stopped.")
        sys.exit(130)
