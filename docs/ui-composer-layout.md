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
| Visible viewport | Account for the browser's visual viewport, the site's sticky header, and marked sticky bottom bars (2026-10-08). |
| Stable editing | Preserve native text, selection, undo, manual enlargement, and the composer's stretched row. |
| Clearing | Restore the field's original height when its text becomes empty. |
| Shared coverage | Apply automatically to editable textareas, including dynamically inserted and initially hidden fields. |
| Read-only output | Leave read-only textareas unchanged. |
| Layout changes | Recalculate after field resizing, viewport changes, and font loading. These passes never scroll to the caret (2026-10-08). |

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
Since 2026-10-08 it also checks that typing stays above the sticky phone Generate row at 390×640.

Validation passed in Firefox and Chromium on 2026-09-20.
Checks include synchronous input growth, actual video/rewrite dialogs, and the existing draft restoration suite.
The local UI restarted successfully. The Workflow Lab build and its shared asset paths also passed verification.
Django verification covered Python syntax and the static source path; its server was not started.

## Phone layout — 2026-10-08

Owner report: on mobile browsers the prompt was very narrow and took too much vertical space.
The rest of the page also worked poorly.

Phone use cases, in priority order:

1. Write a prompt and press Generate.
2. Attach an image.
3. Watch results arrive in job cards.
4. Step through results in the viewer.
5. Browse the archive and favorites.
6. Reach settings, logs, and activity.

| Requirement | Behavior |
|---|---|
| No sideways overflow | No row may be wider than the screen. One wide row widens the layout viewport, and fixed panels and the viewer then sit off-screen. |
| One-row header | The header is one 46 px row that scrolls sideways. Navigation comes first; RAM and build diagnostics come last. The environment switcher replaces the title. |
| Filter rows | Creator chips and favorites chips each form one sideways-scrolling row. |
| Full-width prompt | In portrait, a 64 px paste zone sits above a full-width prompt. The prompt shows six lines of 16 px text and grows with the text. |
| Landscape phones | Landscape keeps the paste zone beside the prompt. All other phone rules apply. |
| Reachable Generate | The Generate row sticks to the screen bottom while the composer is visible. The collapsed activity bar sits beside it. |
| Caret above Generate | Typing keeps the caret line above the sticky Generate row. A soft keyboard covers that row, so the row then does not count. |
| Touch input | Touch screens use 16 px form text, which stops iOS zoom on focus. Tap targets grow to 30–40 px. The paste zone says **tap to choose an image**. |
| Popovers | The input library, inspiration, and option help open as full-width fixed panels with internal scrolling. |
| Job cards | The prompt takes the first line beside the input thumbnail. Prompt actions and job status follow on the next line. Result cells fill the width. |
| Long job prompts | A job prompt shows at most six lines. **show full prompt** appears under a clamped prompt; **show less** restores six lines. Short prompts get no toggle. Copy, viewer captions, video prompts, and **set active** still use the complete text. |
| Collapse keeps place | Collapsing keeps the prompt and its toggle on screen. Chromium and Firefox do this through scroll anchoring. Safari has no scroll anchoring, so `app.js` scrolls the prompt start back below the header. |
| Viewer size | The viewer window always fits the screen. Its minimum size is 300×260 px. |
| Viewer status | The bottom bar takes at most 36% of the screen height. It scrolls as one region; the prompt has no separate scroll box. |
| Viewer chrome identity | A different item's chrome starts scrolled to its top, which shows its generator and place in the list. The same item keeps its scroll position when the original replaces the preview. This applies on every screen. |
| Viewer gestures | Swipe the image left for the next item and right for the previous item. Pinch zoom works normally. While the page is zoomed in, a swipe pans. |
| Back closes the viewer | The phone or browser Back control closes the viewer. The page keeps its address and scroll position. Opening adds one same-address history entry; any close removes it. |
| Activity while viewing | On phones the activity bar hides while the viewer is open. It otherwise shows through the translucent backdrop behind the preload ticks. |
| No gray text | Placeholders use the accent color on every page that loads `style.css`. The night filter toggle stays quiet through small text, not opacity. |

The `?` viewer glossary names the new controls **Touch swipe stepping** and **Back-button exit**.

Media queries:

- Phones in both orientations: `(max-width: 700px), (pointer: coarse) and (max-height: 500px)`.
- Portrait only: `(max-width: 700px)`. It adds the single-column composer, the narrow chip grid, and the dialog and log column changes.
- Touch screens: `(hover: none) and (pointer: coarse)`.
- `PhoneLayoutQuery` in `app.js` must equal the phone query. It selects the viewer minimum size.

Shared text entry changes:

- Only editing and caret movement scroll to the caret. Mobile address bars resize the viewport during every scroll; revealing on resize pulled the page back to a focused field.
- `selectionchange` events from the measuring mirror are ignored. Reacting to them reran sizing on every frame.
- A page marks each sticky bottom bar with `data-text-entry-inset="bottom"`. The composer marks `#send-row`.

Rationale:

- The old phone rules sat in the middle of `style.css`, so later base rules overrode them. Phone rules now stay at the end of the file.
- The header was wider than the screen. Fixed panels and the viewer then sat partly off-screen.
- Landscape phones are about 400 px tall. Under the desktop rules they overflowed sideways: a 961 px layout on an 863 px screen.
- On a Pixel 7, the old job-card prompt column was 13 px wide, about one letter per line. A 123-character prompt stood 1,638 px tall.
- Full width alone left a 2,663-character prompt 1,029 px tall. Its first result started 1,136 px below the top of the prompt, past the first screen. The six-line clamp moves it to 269 px.
- Flesh-out and goal-loop prompts are often that long, so the clamp serves the job feed and archive use cases.
- A fade over the last line would render gray text. The clamp ends with an ellipsis instead.

Verification used Playwright emulation of Pixel 7, iPhone 13, Galaxy S8, Pixel 7 landscape, and a 1400×900 desktop.
Every run blocked requests other than GET, so no job was submitted.
Checks covered sideways overflow, header height, the sticky Generate row, and the caret position beside it.
Without the bottom-bar marker, the regression test fails at 390×640: the caret line ends 11 px behind the Generate row.
Further checks covered scrolling away from a focused prompt, job card layout, and viewer fit for portrait and landscape images.
Swipes step through items; vertical swipes do not. Back and Escape both remove the viewer history entry.
The desktop layout above 700 px did not change.

Browser regression: `tools/test-phone-layout-browser.cjs` runs against the local UI and blocks requests other than GET.
It covers Pixel 7, Galaxy S8, and Pixel 7 landscape in Chromium, Firefox at 390 px, and a desktop control.
It checks the phone query against the CSS, the header, composer orientation, sideways overflow, and the sticky Generate row.
It builds synthetic job cards and checks the six-line clamp, both toggle labels, short prompts, and collapse with and without scroll anchoring.
Without the `app.js` collapse scroll, the pass without scroll anchoring fails: the prompt start ends 610 px above the screen.

Implementation: `style.css`, `index.html`, `app.js`, `text-entry.js`, and asset versions in every page that loads them.
