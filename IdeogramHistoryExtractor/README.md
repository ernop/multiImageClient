Local Ideogram web history archive. Not committed to git.

Setup:

1. cp config.template.json config.json
2. Fill in browser session auth (see tools/ideogram-export/README.md)
3. python tools/ideogram-export/fetch_history.py --archive-root .
4. python tools/ideogram-export/extract_prompts.py --archive-root .
