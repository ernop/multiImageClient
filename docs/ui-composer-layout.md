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
| Replacement | Replacing the whole text, such as an AI rewrite, fits the field to the new text. A dragged height stays (2026-11-03). |
| Shared coverage | Apply automatically to editable textareas, including dynamically inserted and initially hidden fields. |
| Read-only output | Leave read-only textareas unchanged. |
| Layout changes | Recalculate after field resizing, viewport changes, and font loading. These passes never scroll to the caret (2026-10-08). |
| Pointer placement | A click or tap places the caret where the user is looking. Never scroll to a caret that a pointer placed (2026-11-02). |

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
Replace a whole text through `MultiImageTextEntry.replace(field, text)`, which also dispatches the bubbling `input` event (2026-11-03).
After a smaller programmatic edit in an already visible field, dispatch a bubbling `input` event.
Use `data-text-entry="fixed"` only for a deliberately fixed editor with its own caret visibility contract.
Future prompt editors must use the shared module; the binding rule appears in `AGENTS.md`.

Browser regression: `tools/test-text-entry-browser.cjs` exercises Chromium and Firefox without submitting generation requests.
It checks Enter, wrapping, spare lines, page/panel scrolling, undo, draft restoration, resizing, spelling, and dynamic/hidden fields.
Since 2026-10-08 it also checks that typing stays above the sticky phone Generate row at 390×640.
Since 2026-11-02 it also checks that clicks and taps leave the page still.
Since 2026-11-03 it also checks that a shorter AI rewrite fits the field to its text.

Validation passed in Firefox and Chromium on 2026-09-20.
Checks include synchronous input growth, actual video/rewrite dialogs, and the existing draft restoration suite.
The local UI restarted successfully. The Workflow Lab build and its shared asset paths also passed verification.
Django verification covered Python syntax and the static source path; its server was not started.

## Pointer caret placement — 2026-11-02

Owner report: clicking the lower part of a prompt taller than the screen made the page jump up.
The text the owner wanted to see and edit then left its place on the screen.

Requirement: a click or tap never scrolls the page or an enclosing panel.
The caret lands where the pointer placed it, and the page stays still.

| Event | Behavior |
|---|---|
| Pointer pressed in a field | No reveal while it stays pressed, including the focus and drag selection it causes. |
| Pointer released | Record the field's selection. No reveal while the selection stays the same. |
| Touch tap | Record the selection again on `click`, because the tap moves the caret after the pointer is released. |
| Typing or composition input | Clear the record. Reveal the caret as before. |
| Arrow keys or programmatic selection | The selection differs from the record, so reveal the caret as before. |
| Focus leaves the field | Clear the record. Keyboard focus later reveals the caret as before. |

Two causes moved the page:

- Chromium focuses a clicked field before it moves the caret. The focus pass revealed the previous caret, often far above the click. The next pass revealed the clicked caret near the bottom edge. The net move in the regression test was 289 px.
- McPhee's overlay check collapsed the prompt for one layout pass. Firefox then clamped the page's scroll position whenever the view reached past the prompt's bottom edge. McPhee 3.12.2 fixes this; see [mcphee-integration.md](mcphee-integration.md).

Rationale: the reveal exists so typing never runs off the screen.
A pointer already shows the user where the caret is, so a reveal can only move the text being read.
Keeping the two-line reveal margin for clicks would also scroll a click near the bottom edge.

`tools/test-text-entry-browser.cjs` now covers both engines.
It clicks the lower part of a blurred prompt with its caret at the start.
It clicks with the view 250 px past the prompt's bottom edge.
It clicks a focused prompt 10 px above the bottom of the view.
It taps a phone-sized touch view.
Each case requires an unchanged page position and the caret on the clicked line.
Arrow-key movement after the clicks must still reveal the caret.
With the previous `text-entry.js`, Chromium failed the first case.
With McPhee 3.12.0, Firefox failed the second case by 250 px.
A matrix of 12 clicks also passed: both engines, an old caret at either end, and the view inside or past the bottom edge.
None moved the page, every caret landed on the clicked character, and no console warning appeared.

Implementation: `text-entry.js`, McPhee 3.12.2, and asset versions in `index.html`, `goal.html`, the Workflow Lab page, and the Django text-input template.

## Replacing the prompt — 2026-11-03

Owner request: when an AI rewrite makes the prompt shorter, shrink a prompt box that the longer text had stretched.

Requirement: replacing the whole prompt fits the box to the new text.
Typing and deleting still never shrink the box.

| Action | Behavior |
|---|---|
| AI rewrite, Claude advice, restored version, **undo Claude edit** | Fit the box to the new text. |
| **set as active prompt** in the viewer, set active on a job card, a reused shared prompt | Fit the box to the new text. |
| Typing, deleting, paste, spelling corrections | Grow only, as before. Only clearing the text resets the height. |
| Character descriptions, inspirations, sketch legend | Edit the text in place. Grow only, as before. |
| Height the user dragged | Keep it. A replacement removes only the growth that the application added. |

The box's top edge decides what stays still on screen.
When that edge is above the visible area, the bottom edge keeps its place.
The buttons under the prompt then stay where the user clicked.
When the top edge is in view, the page does not scroll, and the box grows or shrinks downward.
The page cannot scroll above its top, so a short result there moves the bottom edge up.

Rationale: shrinking during typing or deleting would pull the text away from the caret.
A replacement changes the whole text at once, so a fitted box hides nothing that the user is editing.
The rewrite buttons sit under the prompt, so a still bottom edge keeps them in place.

`text-entry.js` exports `MultiImageTextEntry.replace(field, text)`.
The module records each height it grows a field to, so it can tell that growth from a dragged height.
`replace` fits the box and corrects the page scroll first.
It then dispatches the bubbling `input` event, so revealing a focused caret is the last scroll.
`applyClaudeEdit`, `applyViewedPromptToComposer`, and `setActiveFromJob` in `app.js` use it.

`tools/test-text-entry-browser.cjs` covers this in Chromium and Firefox.
It clicks **flesh out · Sonnet 5.5** with a fixed local reply, so no provider receives the request.
The shorter result must be as tall as the same text typed into an empty prompt.
Its bottom edge must stay within 1 px of its old place.
The spelling overlay must stay visible.
Replacements with the box's top in view must leave the page still.
Typing over a long prompt must keep its height, and a dragged height must survive a replacement.
With the previous `text-entry.js` and `app.js`, the rewritten box stayed 2,775 px tall instead of 975 px.

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

## Sketch window size — 2026-10-20

The sketch window opens centered at the full viewport height and up to 1500 px wide, with a 14 px margin on each side.
That keeps the drawing surface at least as large as the earlier scrolling dialog drew it.
The closed window is hidden; only an open window uses the flex layout.
**full screen** expands it to the viewport.
**restore** returns the size from before that expansion.
Drag the bottom-right grip to change width and height.
The top-left corner stays in place.
Arrow keys on the grip move an edge by 24 px, or by 80 px with Shift.
The window stays inside the viewport.
Its floor is 640×420 px, or the viewport when the viewport is smaller.

The drawing surface fills the free space on both axes and keeps the canvas pixel aspect ratio.
Changing the window does not change the sketch's pixel size.
The legend and the action list use the side column.
A shorter window scrolls those lists.
A taller window shows more actions.
Under 900 px of window width, the legend stacks under the drawing surface and takes about a third of the height.
Tool groups reflow to two columns under 1100 px and to one column under 720 px of window width.

The size lasts until the page reloads.
It is not part of portable personal configuration.
A reload opens the default size again.
