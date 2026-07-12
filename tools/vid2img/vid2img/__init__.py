"""vid2img: video -> frames + transcript -> gpt-image-2, iteratively.

One module per pipeline step:
    acquire.py    yt-dlp download / local-file symlink
    frames.py     ffmpeg frame extraction (fps sampling + scene cuts)
    sheets.py     timestamp-labeled contact sheets
    transcribe.py faster-whisper transcription
    generate.py   prompt composition + gpt-image-2 /edits call
    cli.py        argparse wiring
"""
