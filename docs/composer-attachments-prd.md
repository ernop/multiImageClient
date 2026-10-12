# Composer image attachments

Reviewed 2026-10-12. This document records how images enter the composer,
which attached images each generator receives, and which pastes attach
nothing.

## Requirement

- An image reaches a generator only when the user attached it on purpose.
- The composer shows which attached images each selected generator receives.
- The server routing table is the only source of those counts.

## Entry points

The composer holds up to five ordered images (`UiJobRunner.MaxInputImages`).
Image 1 is the primary image. It sets the default aspect ratio, the job-card
thumbnail, and the viewer comparison image.

| Gesture | Composer images | Composer prompt |
|---|---|---|
| Paste outside a text field | Adds the clipboard image | Unchanged |
| Paste into a text field | Adds the clipboard image, unless the clipboard carries document text | Receives any pasted text |
| Paste while the image viewer or another dialog is open | Unchanged | Unchanged |
| Paste while the sketch editor is open | Unchanged; the sketch canvas takes the image | Unchanged |
| Drop on the paste zone | Adds each dropped image until the cap | Unchanged |
| Paste-zone click or **+ add** | Adds the chosen file | Unchanged |
| **load a previous image** | Adds the chosen archived input | Unchanged |
| Sketch editor **attach** | Removes its own earlier sketch by file identity, then adds the new sketch last | Updates the marked legend paragraph |
| Job-card **set active** | Replaces all images with the job's recorded inputs; a job without inputs clears them | Replaced |
| Viewer **set image active** | Replaces all images with the viewed original | Unchanged |
| Viewer **set image + prompt active** | Replaces all images with the viewed original | Replaced |
| Viewer **set as active prompt** | Unchanged | Replaced |
| Shared page **Make your own** | Replaces all images with the shared inputs; a share without inputs clears them | Replaced |
| **Generate** | Kept after a successful submit | Kept |
| **× clear all**, or one image's **×** | Removes all images, or that image | Unchanged |

The composer has no page-level drop target. A drop outside the paste zone
attaches nothing.

## Paste rules — 2026-10-12

The page-level paste handler in `app.js` checks each paste in this order:

1. A clipboard without an image file is an ordinary paste. The handler does
   nothing.
2. A text field keeps document text. The image is not attached.
3. The open sketch editor puts the image on its canvas.
4. The open image viewer or another open modal dialog blocks the paste.
5. Otherwise, the composer adds the image.

Definitions:

- A **text field** is a `textarea`, a text-like `input`, or a
  `contenteditable` element. Disabled and read-only fields do not count.
- **Document text** is non-blank `text/plain` together with `text/rtf`, or
  together with `text/html` that has visible body text.

Office suites and LibreOffice can put a picture of copied text beside the text.
Browser **Copy image** adds at most a URL or an `<img>` tag. Screenshots and
file-manager copies carry no document text. These image copies still attach
when pasted into the prompt. An Office copy pasted outside a text field still
attaches its picture.

Rationale: the old handler attached the picture and discarded the text. Text
pasted from a word processor became an input image for every image-capable
generator. A paste behind the viewer or a dialog added an attachment that the
user could not see.

## Per-generator routing — 2026-10-12

`UiJobRunner.InputImageLimit(key)` is the only routing table. Each generator
receives that many leading images, in composer order.

| Targets | Images received |
|---|---|
| `gpt2`, `gpt25-sunburst`, `gpt25-flare` | First four (`GptImageEditInputLimit`). OpenAI `/edits` accepts 16. |
| `grok-api-pro` (grok-api 2.0) | All five, as ordered edit references |
| `ideogram-v45` (Ideogram 4.5) | All five. Image 1 is the Precise Edit source; the others are references. |
| `grok-web` | Image 1 only. None while its signing material is unusable. |
| Other image-capable targets | Image 1 only |
| Text-only targets | None. They run from the prompt alone. |
| Describe and layout-map targets | Every image, one call per image |

- `BuildGenerator` passes `InputImagesFor(job, key)` to the multi-input
  routes.
- `/api/config` publishes each target's share as `inputImageLimit`.
- The job-start log line lists the share of each selected generator.
- With two or more attachments, a note under the images groups the selected
  generators. Its headings are **All N images**, **Images 1–K**, **Image 1
  only**, **No image (text only)**, and **One call per image**.
- With no generator selected, the note reads **No generator selected.**
- Generator chip tooltips state each share of the current attachments.
- Job-card count badges state the attached count. They also state that the
  thumbnail shows image 1. They make no routing claim, because routing changed
  over time.
- `app.js` reads `inputImageLimit`. Never hardcode per-generator image counts
  in the page.

Rationale: four places said that only the gpt-image family receives more than
image 1. They were the composer note, the paste-zone tooltip, the prompt
placeholder, and the server log. grok-api 2.0 and Ideogram 4.5 also receive
all five images. The gpt-image family receives four of five. Routing did not
change.

## Viewer activation download — 2026-10-12

**set image active** and **set image + prompt active** reuse the viewer's
original loader. Navigation can discard that loader entry. The action then
downloads the original once, directly. Before this fix, the direct path threw a
`ReferenceError`, and the button read **set active failed**.

## Open owner decisions

These current behaviors can send images that the user no longer intends to
send. This review did not change them.

1. **Generate keeps attachments.** After a successful submit, the next
   Generate sends the same images again, even with an unrelated prompt.
   Image-capable generators then edit the old images.
   - A: Keep this behavior. It supports fast variations of one edit.
   - B: Clear the images after a successful submit. Keep the prompt.
   - C: Keep the images. Label them as carried over until the user changes
     them.
2. **set as active prompt keeps attachments.** The owner settled this on
   2026-09-03 in [ui-viewer-composer-activation.md](ui-viewer-composer-activation.md).
   A prompt from a text-only job then runs as an edit of the held images.
   - A: Keep this behavior.
   - B: Replace the images with the recorded inputs of the viewed job, as
     job-card **set active** does.

Measurement: count jobs whose input hash matches the same user's previous job
while the prompt changed. Intended re-edits also match, so the rate is an
upper bound for unintended carry-over.

## Tests

- `tools/test-composer-attachments.cjs` runs page functions in `node:vm`. It
  covers the viewer download paths, the routing note, chip tooltips, and the
  paste rules.
- `MultiImageClient.Tests/ComposerInputRoutingTests.cs` checks the share of
  each routing class. It also checks grok-web while its signing is unusable.

## Files

- `MultiImageClient/Implementation/UiJobs.cs`: `MaxInputImages`,
  `GptImageEditInputLimit`, `InputImageLimit`, `InputImagesFor`, and the
  job-start routing log.
- `MultiImageClient/Workflows/UiWorkflow.cs`: `inputImageLimit` in
  `/api/config`.
- `MultiImageClient/Ui/wwwroot/app.js`: the paste handler, routing note, chip
  tooltips, job-card badges, and viewer activation.
- `MultiImageClient/Ui/wwwroot/index.html`: paste-zone and prompt wording.
