# Characters PRD (2026-10-15)

Supersedes the 2026-10-14 "dynamic prompt tagging and subject synthesis" draft
from [PR #16](https://github.com/ernop/multiImageClient/pull/16). The API paths
keep the `subjects` name; every user-facing surface says **characters**.

## Purpose

People draw the same people, pets, places, and things again and again. A
**character** is a saved name plus a short description of its look. The user
types the name in a prompt and adds the description with one click. The
writer model can also read the user's own past prompts about that name and
write one consistent description from them.

## Composer behavior

1. **Characters button.** `#characters-toggle` sits in `#prompt-tools` under
   the prompt. It shows the saved count and opens `#characters-panel`.
2. **Strip under the prompt.** `#character-strip` is one line directly under
   `#prompt`. It lists each saved character that the prompt mentions:
   - `describe <name>` adds ` (<description>)` after the first mention.
   - `<name> ✓` means the prompt already describes it. Selecting it opens
     that character for editing.
   - `describe all N` appears when two or more mentions lack descriptions.
   - `undo` restores the exact earlier prompt. It is offered only while the
     prompt still holds the text this feature produced. Any other edit
     removes it, so undo never discards later typing.
3. **Matching** (`prompt-characters.js`, unit-tested in
   `Ui/tests/prompt-characters.test.js`):
   - Whole words only, case-insensitive. "Jing" never matches "Beijing".
   - Longer names win: "Jing Li" is one mention, not "Jing" plus "Li".
   - Possessives match; the description goes after the suffix:
     `Biscuit's (a small cream Shiba Inu…) ball`.
   - Straight and curly apostrophes match each other. Any run of spaces
     matches one space.
   - A parenthesis right after a mention counts as a description. The
     feature then never adds a second one and never changes the user's text.
   - Each character is described once per prompt, at its first mention.
     Repeating the description at every mention made prompts long without
     improving images.
4. **Saved list.** Each row is one line: name, kind, description, **insert**,
   **edit**. **insert** puts the name at the cursor, with its description if
   the prompt does not describe it yet.
5. **Editor.** Name, kind (character, place, thing, other), other names in old
   prompts, and description. The hint shows the expected form: words that
   read well in parentheses after the name.
   - **find in my prompts** lists matching past prompts with the text around
     the name. It makes no model call.
   - **write it from my prompts** sends those prompts and optional notes to
     the selected writer (default Opus 5.5) and saves the result.
     It asks before replacing a description that is already filled in.
   - **delete** asks first. Prompts keep their text.
   - Closing or switching characters with unsaved edits asks first.
6. **Spelling.** Every word of every saved name joins McPhee's public
   `extraWords` set, so names never show as misspellings. The vendored McPhee
   file is unchanged; the PR's McPhee patches were removed. Names are not
   highlighted inside the prompt; the strip shows the mentions instead.

## Ownership and search scope

- Characters belong to the signed-in account (`login:<login>`), not to the
  "creating as" name. A local auth-off instance has one owner (`local`), so
  every "creating as" name there shares one collection.
- Login renames carry characters through `UiCommunityStore.TransferLogin`.
- On shared instances, search reads only that account's own prompts:
  UI jobs whose `CreatorLogin` is the account, and the account's own Claude
  prompt rewrites. It never reads other members' prompts, the generation
  archive, or the server's prompt files.
- The local owner instance also reads the generation archive, every UI job,
  all Claude rewrites, `PromptFiles`, and `TypedPromptsAppendFile`.
- Scoring: a whole-word match of the name scores 100; each other name that
  matches as a whole word adds 15. Prompts that match neither are dropped.
  Duplicates collapse. At most 40 prompts (`MaxMatchedPrompts`) go to the
  writer, each cut to 3,000 characters, 48,000 characters in total.

## Writer contract

- Models: the goal-loop manager catalog (`ManagerCatalog.All`). Requests and
  records use exact catalog keys such as `manager-claude-opus-5-5`.
- Reply: one JSON object with exactly `kind`, `description`, and `summary`,
  all non-empty strings. Anything else fails; nothing is saved.
- The description is a noun phrase without the name, so it reads as
  `<Name> (<description>)`. It keeps lasting traits, uses clear bright
  daylight defaults, and omits scene details from single prompts.
- The saved kind is the one the user chose, not the model's guess.
- With no matching prompts and no notes, the request fails before any call.
- One write runs at a time per account; a second gets HTTP 409.
- The server checks the save target before the paid call: a duplicate name
  or a deleted character fails first.
- Prompt rewriting's environment switch also controls writing. With it off,
  `/api/prompt/subjects/synthesize` is refused and `#character-write` is
  hidden. Find, manual save, insert, and describe stay available.

## Storage

`UiCommunityStore` table `ui_subject_tags`: `id`, `identity_key`,
`actor_display`, `name`, `normalized_name` (unique per identity), `kind`,
`search_query`, `model` (catalog key, or `manual` after a hand edit of the
description), `description`, `source_summary`, `matched_prompts_json`,
`matched_prompts_count`, `raw_response`, `created_at_unix_ms`,
`updated_at_unix_ms`.

- Create refuses a name the account already uses; it names the stored
  character. Edit and delete use the exact id. Rename keeps the id.
- Names: 1–80 characters, one line. Kind must be one of the four values.

## Portable personal configuration (decision, 2026-10-15)

Characters are account content, like favorites, and stay out of the portable
personal configuration JSON. Reasons: the server already stores them per
account, so they follow the user to every browser; each record carries model
provenance and the user's past prompts, which the transfer schema excludes;
and adding them would require schema version 5 and a migration of every
stored browser document. The owner can reverse this decision.

## API

All routes are scoped to the caller's identity.

- `GET /api/prompt/subjects` → `{subjects:[{id, name, kind, searchQuery, model, description, sourceSummary, matchedPromptsCount, updatedAtUnixMs}]}`
- `POST /api/prompt/subjects/search` form `name`, `query` → `{matches:[{source, recordId, createdAtUtc, prompt}]}`
- `POST /api/prompt/subjects/synthesize` form `id` (empty = new), `name`, `kind`, `query`, `model`, `notes` → `{subject, matches}`; 409 while another write runs
- `POST /api/prompt/subjects` form `id` (empty = new), `name`, `kind`, `query`, `description` → `{subject}`
- `POST /api/prompt/subjects/delete` form `id` → `{deleted:true}`; 404 for an unknown id
- `/api/config.promptSubjects` → `{defaultModel, maxNameChars, maxQueryChars, maxDescriptionChars, maxNotesChars, models:[{key, label, availabilityProblem}]}`

## CLI (local owner collection)

- `--subject-list`
- `--subject-search NAME [--subject-query NAMES]`
- `--subject-synthesize NAME [--subject-kind KIND] [--subject-query NAMES] [--subject-model KEY] [--subject-notes TEXT]`
  replaces the local character with that name.

## Files

- `MultiImageClient/TextLLMs/PromptSubjectSynthesis.cs` — search, writer call, strict reply parser, single-flight lease
- `MultiImageClient/Implementation/UiCommunity.cs` — `ui_subject_tags` storage
- `MultiImageClient/Implementation/UiJobs.cs` — `UiJobRegistry.SearchPrompts` filtered by `CreatorLogin`
- `MultiImageClient/Workflows/UiWorkflow.cs` — routes and rewrite-switch gate
- `MultiImageClient/Ui/wwwroot/prompt-characters.js` — mention matching and text edits
- `MultiImageClient/Ui/wwwroot/app.js`, `index.html`, `style.css` — composer strip and panel
- `MultiImageClient.Tests/SubjectTagTests.cs`, `MultiImageClient/Ui/tests/prompt-characters.test.js`, `tools/test-characters-browser.cjs` (local server; never calls the writer)
