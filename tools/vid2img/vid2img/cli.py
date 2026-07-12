"""Argparse wiring for the vid2img pipeline. Each subcommand maps to one step
module; `new` runs the whole prep pipeline, and the per-step commands let you
redo a single stage (e.g. re-transcribe with a bigger whisper model) without
touching the rest of the session."""

import argparse

from . import acquire, frames, generate, sheets, transcribe
from .common import SESSIONS, die, load_history, session_dir, session_source


def cmd_new(args) -> None:
    name, is_url = acquire.resolve_name(args.source, args.name)
    sdir = SESSIONS / name
    if sdir.exists() and not args.force:
        die(f"session '{name}' exists (use --force to redo, or 'gen {name}' to iterate)")
    sdir.mkdir(parents=True, exist_ok=True)

    source = acquire.acquire(sdir, args.source, is_url)
    frames.extract_frames(sdir, source, fps=args.fps, scene=not args.no_scene)
    sheets.build_sheets(sdir)
    transcribe.transcribe(sdir, source, model_size=args.whisper_model)

    prompt_file = sdir / "prompt.txt"
    if not prompt_file.exists():
        prompt_file.write_text(
            f"# Write your prompt here, then run: ./v gen {name}\n")

    print(f"\nsession ready: {sdir}")
    print(f"next: edit {prompt_file} then run: ./v gen {name}")


def cmd_frames(args) -> None:
    sdir = session_dir(args.name)
    source = session_source(sdir)
    frames.extract_frames(sdir, source, fps=args.fps, scene=not args.no_scene)
    sheets.build_sheets(sdir)


def cmd_sheets(args) -> None:
    sheets.build_sheets(session_dir(args.name))


def cmd_transcribe(args) -> None:
    sdir = session_dir(args.name)
    transcribe.transcribe(sdir, session_source(sdir), model_size=args.whisper_model)


def cmd_gen(args) -> None:
    generate.generate(
        session_dir(args.name), args.prompt, args.prompt_file,
        size=args.size, quality=args.quality, n=args.n,
        allow_dark=args.allow_dark, dry_run=args.dry_run)


def cmd_list(_args) -> None:
    if not SESSIONS.exists():
        print("no sessions")
        return
    for sdir in sorted(SESSIONS.iterdir()):
        if not sdir.is_dir():
            continue
        fdir = sdir / "frames"
        n_frames = len(list(fdir.glob("*.jpg"))) if fdir.exists() else 0
        hist = load_history(sdir)
        print(f"{sdir.name}: {n_frames} frames, {len(hist)} generations")
        for h in hist:
            p = h["prompt"].replace("\n", " ")
            print(f"  #{h['gen']} [{h['time']}] {p[:80]}{'...' if len(p) > 80 else ''}")


def add_frames_args(p) -> None:
    p.add_argument("--fps", type=float, default=1.0, help="frames per second (default 1)")
    p.add_argument("--no-scene", action="store_true", help="skip scene-change detection")


def add_whisper_args(p) -> None:
    p.add_argument("--whisper-model", default="small",
                   help="faster-whisper model: tiny/base/small/medium/large-v3 (default small)")


def main() -> None:
    ap = argparse.ArgumentParser(
        prog="vid2img",
        description="video -> frames + transcript -> gpt-image-2, iteratively")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p_new = sub.add_parser("new", help="download/prepare a video session (all prep steps)")
    p_new.add_argument("source", help="youtube URL or local video file")
    p_new.add_argument("--name", help="session name (default: from title/filename)")
    add_frames_args(p_new)
    add_whisper_args(p_new)
    p_new.add_argument("--force", action="store_true", help="redo existing session")
    p_new.set_defaults(func=cmd_new)

    p_frames = sub.add_parser("frames", help="re-extract frames + rebuild sheets")
    p_frames.add_argument("name")
    add_frames_args(p_frames)
    p_frames.set_defaults(func=cmd_frames)

    p_sheets = sub.add_parser("sheets", help="rebuild contact sheets from existing frames")
    p_sheets.add_argument("name")
    p_sheets.set_defaults(func=cmd_sheets)

    p_tr = sub.add_parser("transcribe", help="re-transcribe (e.g. with a bigger model)")
    p_tr.add_argument("name")
    add_whisper_args(p_tr)
    p_tr.set_defaults(func=cmd_transcribe)

    p_gen = sub.add_parser("gen", help="generate image from session + prompt")
    p_gen.add_argument("name", help="session name")
    p_gen.add_argument("-p", "--prompt", help="prompt text (also saved to prompt.txt)")
    p_gen.add_argument("-f", "--prompt-file", help="read prompt from file")
    p_gen.add_argument("--size", default="1536x1024")
    p_gen.add_argument("--quality", default="high", choices=["low", "medium", "high", "auto"])
    p_gen.add_argument("-n", type=int, default=1, help="images per call")
    p_gen.add_argument("--allow-dark", action="store_true",
                       help="skip the standing bright/clear style baseline")
    p_gen.add_argument("--dry-run", action="store_true",
                       help="print the composed prompt without calling the API")
    p_gen.set_defaults(func=cmd_gen)

    p_list = sub.add_parser("list", help="show sessions and history")
    p_list.set_defaults(func=cmd_list)

    args = ap.parse_args()
    args.func(args)
