# UI viewer composer activation

Settled 2026-09-03. Updates the 2026-08-06 viewer controls.

## Requirement

The generated-image viewer must expose prompt copy and composer activation
without scrolling past the prompt.

## Behavior

- A two-overlapping-squares **copy prompt** control sits in the status-column
  upper right. One click copies the exact current viewer prompt. The flash
  text is **prompt copied**, matching the job-card control.
- **set image active**, **set image + prompt active**, and **set as active
  prompt** sit above the prompt, after the generator/actions row.
- **set image active** replaces composer attachments with the viewed original
  and leaves the composer prompt unchanged.
- **set image + prompt active** replaces composer attachments and the composer
  prompt.
- **set as active prompt** replaces the composer prompt and leaves composer
  attachments unchanged.
- None of these actions change generator selection or output options.
- Missing prompt text hides copy and disables prompt-only activation. The
  viewer does not copy or apply an empty or guessed prompt.
- These controls are item-specific chrome. They swap with the visible image.

## Rationale

The set-active buttons sat below a long prompt. In the side column that put
them off-screen. Copy existed on job cards and was absent from the viewer.
