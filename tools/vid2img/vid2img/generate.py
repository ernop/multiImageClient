"""Step 5: send contact sheets + transcript + prompt to gpt-image-2 and iterate.

Uses /v1/images/edits. Do NOT send `input_fidelity` -- gpt-image-2 rejects it
on both /generations and /edits (confirmed 2026-07-06, `invalid_input_fidelity_model`).
"""

import base64
import json
import time
from datetime import datetime
from pathlib import Path

import requests

from .common import api_key, die, load_history, save_history
from .sheets import MAX_INPUT_IMAGES

OPENAI_URL = "https://api.openai.com/v1/images/edits"
MODEL = "gpt-image-2"

# Standing project-wide clarity preference (see AGENTS.md "Universal Image
# Prompt Defaults"). gpt-image-2 drifts into dark cinematic drama unless the
# prompt says otherwise, so it is appended visibly to every composed prompt.
CLARITY_DEFAULT = (
    "Clear, bright, full normal daytime lighting by default. Not dim, murky, "
    "grimy, muddy, gloomy, shadow-choked, underexposed, dusk-like, night-like, "
    "or dark. Prefer readable, coherent, visually organized images with clean "
    "composition, clear separation of subjects or groups, concise high-contrast "
    "text when text is needed, and attractive balanced color. Favor clarity "
    "over murky cinematic drama."
)


def resolve_prompt(sdir: Path, prompt: str | None, prompt_file: str | None) -> str:
    if prompt:
        text = prompt
    elif prompt_file:
        text = Path(prompt_file).read_text()
    else:
        pfile = sdir / "prompt.txt"
        if not pfile.exists():
            die(f"no prompt -- pass -p, or create {pfile}")
        text = pfile.read_text()
    text = "\n".join(
        l for l in text.splitlines() if not l.lstrip().startswith("#")).strip()
    if not text:
        die(f"empty prompt -- edit {sdir / 'prompt.txt'} or pass -p")
    # prompt.txt is the working copy for the next iteration
    if prompt or prompt_file:
        (sdir / "prompt.txt").write_text(text + "\n")
    return text


def build_full_prompt(user_prompt: str, sdir: Path, allow_dark: bool = False) -> str:
    tfile = sdir / "transcript.txt"
    transcript = tfile.read_text().strip() if tfile.exists() else ""
    n_sheets = len(list((sdir / "sheets").glob("*.jpg")))
    parts = [user_prompt.strip()]
    if not allow_dark:
        parts.append(f"Style baseline: {CLARITY_DEFAULT}")
    context = (
        f"--- CONTEXT ---\n"
        f"The {n_sheets} attached image(s) are contact sheets of video frames in "
        f"chronological order, sampled about once per second; each thumbnail is "
        f"labeled with its [mm:ss] timestamp."
    )
    if transcript:
        context += f"\n\nVideo transcript:\n{transcript}"
    parts.append(context)
    return "\n\n".join(parts) + "\n"


def generate(sdir: Path, prompt: str | None, prompt_file: str | None,
             size: str, quality: str, n: int,
             allow_dark: bool = False, dry_run: bool = False) -> None:
    user_prompt = resolve_prompt(sdir, prompt, prompt_file)
    full_prompt = build_full_prompt(user_prompt, sdir, allow_dark=allow_dark)
    sheets = sorted((sdir / "sheets").glob("*.jpg"))[:MAX_INPUT_IMAGES]
    if not sheets:
        die(f"no contact sheets in {sdir / 'sheets'} (run 'sheets' or 'new' first)")

    if dry_run:
        print(f"--dry-run: would send {len(sheets)} sheet(s), size={size}, "
              f"quality={quality}, n={n}\n")
        print(full_prompt)
        return

    odir = sdir / "out"
    odir.mkdir(exist_ok=True)
    history = load_history(sdir)
    gen_no = len(history) + 1

    print(f"gen #{gen_no}: {len(sheets)} sheets, size={size}, "
          f"quality={quality}, n={n}")
    files = [("image[]", (p.name, p.read_bytes(), "image/jpeg")) for p in sheets]
    data = {
        "model": MODEL,
        "prompt": full_prompt,
        "size": size,
        "quality": quality,
        "n": str(n),
    }
    t0 = time.time()
    r = requests.post(
        OPENAI_URL, headers={"Authorization": f"Bearer {api_key()}"},
        data=data, files=files, timeout=600)
    elapsed = time.time() - t0
    if r.status_code != 200:
        die(f"API {r.status_code}: {r.text[:2000]}")

    outputs = []
    for i, item in enumerate(r.json()["data"]):
        suffix = chr(ord("a") + i) if n > 1 else ""
        out = odir / f"{gen_no:02d}{suffix}.png"
        out.write_bytes(base64.b64decode(item["b64_json"]))
        outputs.append(str(out))
        print(f"saved: {out}")

    history.append({
        "gen": gen_no,
        "time": datetime.now().isoformat(timespec="seconds"),
        "prompt": user_prompt,
        "allow_dark": allow_dark,
        "size": size,
        "quality": quality,
        "elapsed_s": round(elapsed, 1),
        "outputs": outputs,
    })
    save_history(sdir, history)
    print(f"done in {elapsed:.0f}s. Iterate: edit {sdir / 'prompt.txt'} "
          f"and re-run gen, or pass -p \"new prompt\".")
