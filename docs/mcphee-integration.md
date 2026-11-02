# Composer spelling library

Updated 2026-11-02.

## Release decision

Load the current McPhee library from the owner's checkout into every MultiImageClient deployment.
This release includes the local UI, the original production environment, and Vibecoders AI Generation.
All three use the same committed application assets.

The vendored copy is upstream McPhee 3.12.2 (commit `cc974b2`), unchanged (2026-11-02).
The library reports `McPhee.version === "3.12.2"`.
`mcphee.js` and `mcphee.css` are the upstream files; `README.md` is upstream `docs/integration.md`.
Distribution remains a complete folder copy, with no runtime dependency on the source checkout.

3.12.2 keeps the page still during the overlay check (owner report 2026-11-02).
The check collapses the prompt for one layout pass to measure its text.
That collapse shortened the page as well.
Firefox then clamped the scroll position whenever the view reached past the prompt's bottom edge.
Clicking the lower part of a long prompt moved the page up by that distance.
McPhee now holds its wrapper's height during the measurement.
An empty prompt with its long placeholder also failed the check.
The overlay then hid itself and re-rendered on every poll; an empty prompt now passes.
The composer's own click jump in Chromium is in [the composer layout record](ui-composer-layout.md#pointer-caret-placement--2026-11-02).

3.12.1 changes panel navigation (2026-10-20).
Hovering a row only highlights its marks and never scrolls.
Clicking a row selects its word and centers it.
Keeping the caret's row in view scrolls only the panel, never the page.

3.12.0 adds the caps level (owner request 2026-10-06).
The spelling panel reports the prompt's capitalization style and whether it is consistent.
It shows how closely the prompt matches each style as a percentage: traditional (capital sentence starts, one space between sentences) or lcstyle (lowercase sentence starts).
Departures from the prompt's own majority are marked.
Either style can be previewed and applied all at once or one change at a time, with caps undo and redo.
The `caps` checker is stored like every other checker under `spelling.ruleOverrides.checkers`.

## Requirements and behavior

| Requirement | Behavior |
|---|---|
| Current dictionaries | Load both the original Hunspell files and the ESDB/SCOWL 2026.02.25 size-60 files. |
| Initial dictionary | Use the 2026 checker by default. Keep the original checker available in spelling configuration. |
| Dictionary selection | A word fails spelling only when every enabled dictionary rejects it. |
| Checker controls | Preserve each checker's enabled state, display order, and supported parameters. |
| Existing preferences | Preserve personal words, ignored words, not-rare words, formality, and legacy rule/parameter overrides. |
| Configuration persistence | Save checker settings inside `spelling.ruleOverrides.checkers` in the canonical personal configuration. |
| Configuration validation | Reject unknown checkers, fields, parameters, invalid types, fractional values, and out-of-range numbers. |
| Load failure | Disable spelling controls when required dictionary loading fails. Keep the existing required-frequency validation. |
| Control tap | Apply the nearest backward correction when an input method emits a Process key while Control remains held. |
| Asset refresh | Version both library assets and the composer script so returning browsers load this release. |

Checker settings have the shape `{ enabled?, order?, params? }` under the library's exact checker ID.
Order accepts nonnegative safe integers.
Parameters accept integers from 1 through 1,000,000, matching the existing parameter limits.
The library catalog defines which parameters belong to each checker.
Legacy `rules` and `params` retain their existing meaning.
The optional `checkers` field extends version-2 spelling overrides; existing exports remain valid without rewriting them.

The established `mic_spellwell_custom_dict` storage key remains unchanged.
Existing account and environment storage boundaries continue to apply.
Spelling checks run in the browser and do not send prompts to a provider.

## Files and verification

- `MultiImageClient/Ui/wwwroot/mcphee/` contains the library, styles, dictionaries, frequency list, and upstream integration guide.
- `vendor/typo/README_en_US_2026.txt` within that folder preserves the new dictionary's source and license notices.
- `MultiImageClient/Ui/wwwroot/app.js` loads both dictionaries and validates persisted checker settings.
- `MultiImageClient/Ui/wwwroot/index.html` versions the scripts and stylesheet.
- `tools/tests/mcphee-integration.test.cjs` checks dictionary loading, configuration round trips, and invalid-setting rejection.
- `tools/test-mcphee-browser.cjs` checks the live composer, correction undo, checker persistence, and class-specific fonts.

Run the library's Node and browser suites before releasing this copy.
Run the application's release gate and verify the loaded version and dictionary on each deployed composer.

## Application overlay corrections

Since 3.12.2 the vendored copy carries no application patches (2026-11-02).
Both corrections below are now upstream: the class order since 3.12.1 and flex growth since 3.12.2.
Fix future library defects in the McPhee checkout, then copy the released files here.

These patches were first applied to 3.11.2 and carried forward through 3.12.0.
Browser verification found two failures in the incoming 3.11.1 source.
Copy text metrics while the textarea retains its overlay class.
Temporarily remove that class only when reading its original background color.
Removing it earlier loses fonts selected through that class and changes wrapping.

Temporarily disable flex growth when measuring content height at zero height.
Restore the previous flex value before returning.
Otherwise, the stretched composer retains its full height and falsely fails overlay validation.
The fixes preserve the overlay's existing content and wrap checks.

## Release validation — 2026-11-02 (3.12.2)

Upstream `npm test` passed every Node suite.
The upstream browser suite passed, including the new checks in Chromium and Firefox.
Those checks failed on 3.12.1: Firefox moved the page 200 px, and the flex-host and placeholder checks failed in both engines.
The application passed 662 C# tests, 45 JavaScript tests, 19 tool tests, and 26 deploy tests.
The text-entry, McPhee, phone-layout, draft, and prompt-rewrite browser suites passed against the local UI.
The draft suite's account-name step now opens Settings first; that field has been inside Settings since 2026-09-09.
The text-entry, phone-layout, and draft suites aborted every request other than GET.
The prompt-rewrite suite used a mocked origin and reached no server.
The McPhee suite sets no request block.
A rerun that logged and aborted every other method recorded no such request.
No check created a job, made a paid call, or posted to Discord.
Release `0c7899a` went to the restarted local UI and to the original service through `agent-redeploy.sh`.
Vibecoders AI Generation was then updated from the same verified publish and runs the identical build.
Both production services serve McPhee 3.12.2 and the new asset queries.
The original answers its loopback health check, and its private vhost route returns the login gate.
Vibecoders answers its loopback health check, and its private route returns the login gate.

## Release validation — 2026-10-06 (3.12.0)

The merged copy passed all upstream Node suites, including the caps suite, and upstream's own browser suite passed on 3.12.0.
The application passed 513 C# tests, 30 JavaScript tests, and all 10 local-launcher tests.
`tools/test-mcphee-browser.cjs` passed against the restarted local UI.
A targeted composer check showed the caps section (mixed style, per-style percentages) and applied traditional style with no page errors.
Release `f46fd02` went to the original service through `agent-redeploy.sh`.
Vibecoders AI Generation was then updated from the same verified publish and runs the identical binary.
Both serve McPhee 3.12.0 with the new asset query and answer loopback and vhost health checks.
These checks made no paid generation calls or Discord posts.

## Release validation — 2026-09-11

The exact vendored copy passed all upstream Node and browser suites.
The live composer passed highlighting, Control-tap correction, undo, checker persistence, reload, and class-font checks.
The application passed 388 C# tests, the JavaScript suites, and all 10 local-launcher tests.
The shared composer and goal-picker browser suite also passed.
These checks made no paid generation calls or Discord posts.
