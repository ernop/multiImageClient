# Composer spelling library

Updated 2026-09-11.

## Release decision

Load the current McPhee library from the owner's checkout into every MultiImageClient deployment.
This release includes the local UI, the original production environment, and Vibecoders AI Generation.
All three use the same committed application assets.

The vendored source includes the library's pending 3.11.1 Control-tap correction.
The application copy adds two overlay fixes and reports version 3.11.2.
Its upstream base commit is `2fd740d`; the copied files include the working changes after that commit.
The library reports `McPhee.version === "3.11.2"`.
Distribution remains a complete folder copy, with no runtime dependency on the source checkout.

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

## Overlay corrections in 3.11.2

Browser verification found two failures in the incoming 3.11.1 source.
Copy text metrics while the textarea retains its overlay class.
Temporarily remove that class only when reading its original background color.
Removing it earlier loses fonts selected through that class and changes wrapping.

Temporarily disable flex growth when measuring content height at zero height.
Restore the previous flex value before returning.
Otherwise, the stretched composer retains its full height and falsely fails overlay validation.
The fixes preserve the overlay's existing content and wrap checks.
These are application-copy patches; the independent source checkout remains unchanged.

## Release validation — 2026-09-11

The exact vendored copy passed all upstream Node and browser suites.
The live composer passed highlighting, Control-tap correction, undo, checker persistence, reload, and class-font checks.
The application passed 388 C# tests, the JavaScript suites, and all 10 local-launcher tests.
The shared composer and goal-picker browser suite also passed.
These checks made no paid generation calls or Discord posts.
