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
