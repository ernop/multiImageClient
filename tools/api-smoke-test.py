#!/usr/bin/env python3
"""Functional smoke test for every AVAILABLE MultiImageClient UI generator.

Submits one real minimal job per generator (standard detail, n=1), streams the
job's SSE events, and reports pass/fail + cost + error from the `gen-result`
event. Run against the live --ui service (default 127.0.0.1:5960).
"""
import json
import subprocess
import sys
import urllib.request

BASE = "http://127.0.0.1:5960"
PROMPT = "a single ripe red apple centered on a plain white background, simple, clean"


def get_available():
    with urllib.request.urlopen(f"{BASE}/api/config") as r:
        cfg = json.load(r)
    return [(g["key"], g["label"]) for g in cfg["generators"] if g["available"]]


def submit(gen_key):
    # multipart/form-data with just the text fields
    boundary = "----smoke"
    parts = []
    for name, val in [("prompt", PROMPT), ("generators", gen_key),
                      ("detail", "standard"), ("shape", "square"),
                      ("moderation", "low"), ("n", "1")]:
        parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{val}\r\n")
    body = ("".join(parts) + f"--{boundary}--\r\n").encode()
    req = urllib.request.Request(f"{BASE}/api/jobs", data=body,
                                 headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    with urllib.request.urlopen(req) as r:
        return json.load(r)


def stream_result(job_id, timeout=300):
    # events endpoint blocks until the job is done, then closes
    p = subprocess.run(["curl", "-sN", "--max-time", str(timeout),
                        f"{BASE}/api/jobs/{job_id}/events"],
                       capture_output=True, text=True)
    result = None
    for line in p.stdout.splitlines():
        if not line.startswith("data: "):
            continue
        try:
            evt = json.loads(line[6:])
        except json.JSONDecodeError:
            continue
        if evt.get("type") == "gen-result":
            result = evt
    return result


def main():
    gens = get_available()
    print(f"Testing {len(gens)} available generators\n")
    rows = []
    total_cost = 0.0
    for key, label in gens:
        sys.stdout.write(f"{key:<14} {label:<26} ... ")
        sys.stdout.flush()
        try:
            sub = submit(key)
            if "id" not in sub:
                print(f"SUBMIT-REJECTED: {sub}")
                rows.append((key, "REJECTED", 0.0, str(sub)))
                continue
            res = stream_result(sub["id"])
        except Exception as e:  # noqa
            print(f"HARNESS-ERROR: {e}")
            rows.append((key, "HARNESS-ERR", 0.0, str(e)))
            continue
        if res is None:
            print("NO-RESULT (timeout/no gen-result event)")
            rows.append((key, "NO-RESULT", 0.0, ""))
            continue
        ok = res.get("ok")
        cost = float(res.get("cost") or 0)
        ms = res.get("ms")
        total_cost += cost
        if ok:
            print(f"OK  {ms} ms  ~${cost:.4f}  [{res.get('label','')}]")
            rows.append((key, "OK", cost, res.get("label", "")))
        else:
            err = (res.get("error") or "")[:160]
            print(f"FAIL  {ms} ms  :: {err}")
            rows.append((key, "FAIL", 0.0, err))

    print("\n===== SUMMARY =====")
    ok_n = sum(1 for r in rows if r[1] == "OK")
    print(f"{ok_n}/{len(rows)} passed   total est. cost ~${total_cost:.4f}\n")
    for key, status, cost, note in rows:
        print(f"  {status:<12} {key:<14} {note}")


if __name__ == "__main__":
    main()
