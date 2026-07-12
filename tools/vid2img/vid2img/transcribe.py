"""Step 4: transcribe the audio with faster-whisper, timestamped."""

import json
from pathlib import Path

from .common import ts_label


def transcribe(sdir: Path, source: Path, model_size: str = "small") -> None:
    tfile = sdir / "transcript.txt"
    print(f"transcribing (faster-whisper {model_size})...")
    # Imported lazily: faster-whisper pulls in ctranslate2, which is slow to
    # load and unnecessary for every other subcommand.
    from faster_whisper import WhisperModel
    model = WhisperModel(model_size, device="cpu", compute_type="int8")
    segments, info = model.transcribe(str(source), vad_filter=True)

    segs = []
    lines = []
    for seg in segments:
        segs.append({"start": seg.start, "end": seg.end, "text": seg.text.strip()})
        lines.append(f"[{ts_label(seg.start)}] {seg.text.strip()}")
    (sdir / "transcript.json").write_text(json.dumps(
        {"language": info.language, "segments": segs}, ensure_ascii=False, indent=1))
    tfile.write_text("\n".join(lines) + "\n")
    print(f"transcript: language={info.language}, {len(segs)} segments -> {tfile}")
