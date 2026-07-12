# vid2img

Video → frames + transcript → gpt-image-2. For when you want to hand an image
model rich context about a video and iterate on a prompt against it.

## Pipeline

1. You provide a YouTube URL (or local file).
2. `yt-dlp` downloads it (skipped for local files; those are symlinked).
3. `ffmpeg` extracts frames: 1/second plus extra frames at scene cuts.
4. Frames are packed into 4x4 contact sheets with [mm:ss] timestamp labels
   (gpt-image-2 edits accepts max 16 input images; frames are thinned evenly
   if the video is too long to fit).
5. `faster-whisper` transcribes the audio with timestamps.
6. `gen` sends sheets + transcript + your prompt to gpt-image-2
   (`/v1/images/edits`; note the model rejects `input_fidelity`, it is always
   high). The standing project clarity baseline (bright, readable, daytime —
   see AGENTS.md "Universal Image Prompt Defaults") is appended visibly to
   every composed prompt; pass `--allow-dark` when you actually want dark output.
7. Iterate: edit `sessions/<name>/prompt.txt`, run `gen` again. Every
   generation is numbered and logged to `session.json`.

## Code layout

One module per pipeline step, under `vid2img/`:

```
vid2img/
  cli.py         subcommand wiring; `new` chains all prep steps
  acquire.py     step 1-2: yt-dlp download / local symlink
  frames.py      step 3a: ffmpeg fps sampling + scene-cut extraction
  sheets.py      step 3b: timestamp-labeled contact sheets
  transcribe.py  step 4: faster-whisper transcription
  generate.py    step 5: prompt composition + gpt-image-2 call
  common.py      session paths, history, api key lookup
```

## Usage

```bash
cd ~/proj/multi-image-client/tools/vid2img

# one-time prep per video (runs acquire -> frames -> sheets -> transcribe)
./v new "https://www.youtube.com/watch?v=XXXX"
./v new ~/video/some-file.webm --name myvid

# generate + iterate
./v gen myvid -p "Make an infographic explaining the product claims in this ad"
vim sessions/myvid/prompt.txt   # tweak
./v gen myvid                   # re-run with edited prompt.txt
./v gen myvid --size 1024x1536 --quality medium -n 2
./v gen myvid --dry-run         # inspect the exact composed prompt, no API call

# redo a single prep step without rebuilding the session
./v transcribe myvid --whisper-model medium   # e.g. better non-English pass
./v frames myvid --fps 2                      # denser sampling (rebuilds sheets)
./v sheets myvid                              # rebuild sheets only

# review
./v list
xdg-open sessions/myvid/out/
```

`./v` is a wrapper that uses the tool venv; `python -m vid2img` works too if
the venv is activated.

## Options

- `new`/`frames`: `--fps 2` — denser sampling; `--no-scene` — skip cut detection.
- `new`/`transcribe`: `--whisper-model medium` — better transcription (default
  `small`; use `medium`/`large-v3` for non-English audio if `small` is rough).
- `gen`: `--size` — 1536x1024 default; also 1024x1024, 1024x1536, 2048x2048,
  2560x1440, or "auto". `--allow-dark` — skip the clarity baseline.
  `--dry-run` — print the composed prompt and exit.

## Setup

```bash
cd tools/vid2img
uv venv --python 3.14 && uv pip install -r requirements.txt
```

Needs `yt-dlp` and `ffmpeg` on PATH.

## API key

Read from `$OPENAI_API_KEY`, falling back to the repo's
`MultiImageClient/settings.json` (`OpenAIApiKey`).

## What it reads / writes (per project tool policy)

- Reads: the video you point it at, `MultiImageClient/settings.json` (key only).
- Writes: everything under `tools/vid2img/sessions/<name>/` — downloaded
  video, frames, contact sheets, transcript, prompts, generated images.
- Sessions can contain personal data (video content, prompts); they are
  gitignored and stay local.

## Session layout

```
sessions/<name>/
  source.webm       downloaded video (or symlink to local file)
  frames/           frame_NNNN_tSSSS.SS.jpg
  sheets/           sheet_NN.jpg      <- what actually gets sent to the model
  transcript.txt    [mm:ss]-stamped text (edit if whisper got things wrong)
  transcript.json   segments with timing
  prompt.txt        working prompt; edit and re-run gen to iterate
  out/              01.png, 02.png, ... generated images
  session.json      full generation history (prompt, settings, timing)
```
