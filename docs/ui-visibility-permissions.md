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
