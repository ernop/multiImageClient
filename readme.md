# MultiImageClient

One prompt. Every model. Side by side.

Paste an idea — or an image — and see what today’s image generators do with it, together. Compare outputs, keep the prompts and provenance with the pictures, and iterate without hopping between tabs.

![MultiImageClient interface](.github/assets/ui-frontend.png)

## Highlights

- **Side-by-side generation** — one prompt fans out across the providers you care about
- **Web UI** — paste or drop an image, write a prompt, pick targets, watch results land live
- **Edit & reference** — use an attached image as edit source, image-to-image input, or style/reference
- **Prompt craft** — randomize, stylize, or rewrite with Claude before you generate
- **Round-trip** — describe an image, then regenerate from that caption across providers
- **Grok video** — turn any result into a short video follow-up
- **Full archive** — prompts, costs, timings, and file provenance stay with the work

## Providers

| Provider | What you can do |
|---|---|
| **OpenAI gpt-image-2** | Text-to-image and image edit; multiple images per run; high detail sizes |
| **OpenAI gpt-image-1 / mini** | Text-to-image; multiple images per run |
| **Ideogram V4** | Text-to-image with strong in-image text |
| **Ideogram V3** | Text-to-image and style/reference from an attached image |
| **Black Forest Labs (Flux)** | Text-to-image; optional style/reference from an attached image |
| **Recraft** | Text-to-image and image-to-image |
| **Google Gemini image** | Text-to-image; optional style/reference from an attached image |
| **Google Imagen 4** | Text-to-image via Vertex |
| **grok-api** | Text-to-image and image edit (official xAI API) |
| **grok-web** | Text-to-image, image edit, and image-to-video via grok.com |
| **meta-web** | Text-to-image via meta.ai |
| **Local ComfyUI** | Local Flux Klein and Z-Image targets when enabled |

## Web UI

The primary interface: a local page where you paste from the clipboard, drop a file, or pick a prior input, choose shape and detail, enable the providers you want, and generate.

- Live job cards as each provider finishes
- Honest labeling when a target is text-only (image attached but not sent)
- Cost totals as you work
- Input library of previous uploads
- Spell-fix helpers on the prompt
- **Make Grok video** on any successful result
- Shared-site mode for multi-user use: usernames, day archive, optional login

## Gallery

- [BFL / Flux gallery](https://photos.app.goo.gl/baJNz9SWX1fq1tT77)
- [Ideogram gallery](https://photos.app.goo.gl/QJn5xPUNEg1uuNdaA)

<p>
<img src="https://github.com/user-attachments/assets/f0bc3e11-0f3b-4200-beba-1159fe2fe61a" width="150" alt="sample 1">
<img src="https://github.com/user-attachments/assets/6d4ce05e-6221-4e82-aa72-8f7ea7649a5d" width="150" alt="sample 2">
<img src="https://github.com/user-attachments/assets/63174d3d-c683-48bf-a121-0d5f5cd01a80" width="150" alt="sample 3">
<img src="https://github.com/user-attachments/assets/f1e8b284-dcfc-41b0-9c8b-747f015a2ba3" width="150" alt="sample 4">
</p>

## Design principles

- **Fail closed** — missing or ambiguous results are errors; nothing is silently substituted
- **Raw saves are verbatim** — provider bytes are kept as returned
- **Identity preserved** — prompts, jobs, and outputs stay correlated end to end
- **Compare first** — one interesting prompt should show what every current generator would do, annotated and shareable
