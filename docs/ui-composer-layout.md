# UI composer top-row layout

Settled 2026-09-03.

## Requirement

The image/prompt row must not leave unused vertical space beside the paste zone.
The two paste-zone actions must share one row.

## Behavior

- `#compose-top` stretches the image column and the prompt column to the same height.
- `#prompt` grows to fill the right column above `#prompt-tools`.
- McPhee wraps `#prompt` in `.mcphee-host`. That host grows with the textarea so spelling marks stay aligned.
- The empty paste zone keeps a 180 px floor so the drop target stays usable.
- **load a previous image** and **sketch a composition** sit side by side under the paste zone.
- Prompt-scoped actions stay immediately under the prompt. They do not move into a distant toolbar.
- Do not copy `#prompt` height onto `#paste-zone` in JavaScript. That equal-height sync left the action row unused and looped with CSS stretch.

## Rationale

The prompt field had a fixed 180 px height while the left column was taller.
The two action buttons each took a full row. That wasted vertical space in the typing field.
Stretching the row and sharing the button line gives that space to the prompt.

## Prompt drafts — 2026-09-12

Save composer text and new-loop goal text synchronously on each input event.
Keep one current draft per field in browser session storage.
Separate drafts by tab, page directory, environment, and authenticated account.
Restore exact text, selection, and scroll position after reload, before editor initialization.
Restore focus only when another control has not received focus.
Never replace text already restored by the browser or edited during startup.

Save programmatic prompt changes from history, inspiration, sketches, viewer activation, and rewrites through the same input events.
Save again when the page leaves or becomes hidden.
Manual clearing removes the saved text.
Successful goal submission clears only the exact submitted text; newer edits remain.
Show a nearby message when browser storage cannot save or restore the draft.

Server restarts do not require a browser reload solely because the build changed.
Existing authentication reloads remain; restoring drafts makes these reloads preserve typed text.
This change saves text, not attachment bytes or generator selections.
Existing open pages need the new script before they can save drafts.

Implementation: `text-drafts.js`, `index.html`, `app.js`, `goal.html`, and `goal.js`.
Browser coverage: `tools/test-text-drafts-browser.cjs`.
