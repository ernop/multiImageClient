# Image and prompt deletion permissions

## Requirements settled 2026-09-07

- Instances with authentication disabled permit image deletion and whole-prompt deletion for all existing jobs, including legacy jobs.
- Authenticated instances permit deletion only by the recorded creator or the existing `ernieMultiZone` override.
- A missing login on an authenticated instance never grants local-owner permissions.
- Browser-supplied display names never establish ownership.
- A configured but invalid authentication file remains a startup error.

The local instance previously hid every deletion control because it had no authenticated login.
Use the configured authentication mode to determine permissions, rather than requiring login in both modes.
An instance without authentication is a trusted, single-owner installation; everyone with access can manage its jobs.

Apply this policy to live job listings, event replay, archive entries, favorites, new cards, and the deletion endpoint.
The configuration response exposes `auth.localVisibilityManagement` for immediate rendering of newly submitted cards.
Local deletion records use `local-instance-owner` as their audit identity, without creating an authenticated account.

Existing confirmations, exact resource checks, and permanent local/B2 deletion behavior remain in place.
Hiding a whole prompt deletes its images; hiding one image deletes only that result and associated artifacts.
There is no undo control.

Validate permission rules and event replay with `UiVisibilityStoreTests`.
Deployment checks must read permissions without deleting user content.

## Running-job deletion controls — 2026-09-09

Vibecoders returned HTTP 409 for three deletion requests at 07:36:15, 07:36:19, and 07:36:28 UTC.
The endpoint rejected deletion because the selected jobs were still running.
No visibility records were created, and no file deletion began.
The browser previously enabled deletion when an individual image appeared, before the entire job finished.
It then showed "delete failed" and placed the server explanation only in a tooltip.

- Disable prompt and viewer deletion when the loaded job is queued or running.
- Show "delete available when job finishes" until every generator and contact-sheet finalization finishes.
- Enable the controls on the job-done event, including an already-open viewer and matching favorite cards.
- Use the loaded live job's state for favorite cards when available.
- Preserve server validation for favorites without a loaded job and for concurrent state changes.
- Display the returned deletion error directly, including errors after a partial purge.
- Do not change ownership checks, artifact identities, or the server's running-job prohibition.

Validate browser transitions with `node --test tools/test-visibility-controls.cjs`.
This frontend-only correction can replace Vibecoders' exact `app.js` atomically after source comparison and backup.
Verify the served file checksum and unchanged service start time; no process restart or content deletion is required.

Applied this correction to production Vibecoders on 2026-09-09.
The deployed candidate passed all three deletion-control tests; the local suite also passed both FableBot viewer tests.
The authenticated public response matched the candidate's SHA-256 checksum, and the loopback health check passed.
The service start time remained unchanged, preserving active jobs.
The original frontend remains backed up outside the served directory.
Concurrent unrelated frontend edits were excluded from this targeted update.
