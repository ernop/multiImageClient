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
Restore exact text and selection after reload, before editor initialization.
Shared text entry expands restored fields and reveals the saved caret when focused (2026-09-20).
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


## Shared text entry — 2026-09-20

Application code owns textarea growth and caret visibility.
McPhee continues to own spelling marks and corrections.
Typing must never leave the caret below the visible page or enclosing panel.

| Requirement | Behavior |
|---|---|
| Immediate growth | Measure wrapped text synchronously on input, including Enter, paste, and composition input. |
| Room to continue | Reserve two blank lines below the text. Remove textarea height caps and internal vertical scrolling. |
| Visible typing | Scroll enclosing panels, then the page, to keep the active line clear of their edges. |
| Visible viewport | Account for the browser's visual viewport and the site's sticky header. |
| Stable editing | Preserve native text, selection, undo, manual enlargement, and the composer's stretched row. |
| Clearing | Restore the field's original height when its text becomes empty. |
| Shared coverage | Apply automatically to editable textareas, including dynamically inserted and initially hidden fields. |
| Read-only output | Leave read-only textareas unchanged. |
| Layout changes | Recalculate after field resizing, viewport changes, and font loading. |

`Ui/wwwroot/text-entry.js` implements this behavior once.
It measures text separately from the live field to avoid flex sizing feedback and spelling-overlay disruption.
It releases observations and field references when an editor leaves the document.
It does not copy prompt height onto the paste zone.

The composer and goal pages load the module after draft restoration.
Coverage includes image prompts, new goals, fork editors, video prompts, rewrite instructions, and provider configuration text.
Other editable multiline fields on those pages receive the same behavior.
The Workflow Lab links the same source through its project file.
The experimental Django text-input page loads the same file through its prefixed static directory.
There are no separate sizing implementations or copied library files.

Load the shared script after page markup on future pages with editable multiline fields.
Dispatch a bubbling `input` event after programmatic text replacement in an already visible field.
Use `data-text-entry="fixed"` only for a deliberately fixed editor with its own caret visibility contract.
Future prompt editors must use the shared module; the binding rule appears in `AGENTS.md`.

Browser regression: `tools/test-text-entry-browser.cjs` exercises Chromium and Firefox without submitting generation requests.
It checks Enter, wrapping, spare lines, page/panel scrolling, undo, draft restoration, resizing, spelling, and dynamic/hidden fields.

Validation passed in Firefox and Chromium on 2026-09-20.
Checks include synchronous input growth, actual video/rewrite dialogs, and the existing draft restoration suite.
The local UI restarted successfully. The Workflow Lab build and its shared asset paths also passed verification.
Django verification covered Python syntax and the static source path; its server was not started.
