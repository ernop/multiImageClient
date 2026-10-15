# Dynamic Prompt Tagging and Subject Synthesis PRD (2026-10-14)

## Overview

Users accumulate personal details across past prompts, family stories, location descriptions, and Claude prompt rewrites. Dynamic prompt tagging lets a user query the local database and prompt history for a name or phrase (such as `Jing` or `Grandma's Courtyard`), synthesize a self-contained visual description with a configured LLM (`Opus 5.5` by default, or any model from `ManagerCatalog.All`), and store that subject description in `UiCommunityStore` (`ui_subject_tags`).

While typing in `#prompt`, the vendored McPhee checker (`MultiImageClient/Ui/wwwroot/mcphee/mcphee.js`) recognizes stored subject names dynamically. McPhee highlights matched subject spans in the backdrop (`.mcphee-mark-subject`), suppresses false `unknown` / `misspelled` / `obscureRepeat` flags on those names, and surfaces single-line controls immediately below `#prompt` and inside the McPhee panel so the user can either expand the stored visual description in place or keep the literal word.

## Requirements and Settled Decisions (2026-10-14)

1. **Multi-source prompt history search (`PromptSubjectSynthesis.SearchAllHistory`):**
   - Queries four local sources without mutating history or hydrating cold `UiHistory` jobs into resident memory:
     - `generation-history.sqlite3` (`GenerationArchive.SearchPrompts`)
     - `UiHistory/` job directories (`UiJobRegistry.SearchPrompts` / `UiJobStorage.TryReadJobPrompt`)
     - `ui-community.sqlite3` (`UiCommunityStore.SearchClaudePromptExchanges`)
     - Configured `PromptFiles` and `TypedPromptsAppendFile`
   - Whole-word matches rank ahead of substring matches; duplicate prompts are collapsed case-insensitively and capped at 40 matched prompts (`MaxMatchedPrompts`).
2. **Fail-closed LLM synthesis (`PromptSubjectSynthesis.SynthesizeSubjectAsync`):**
   - Uses `ManagerChatClients` (`ManagerCatalog.All`, default `claude-opus-5-5` / Opus 5.5).
   - Sends matched historical prompts plus optional search terms and user notes under a strict JSON reply contract (`{kind, description, summary}`).
   - Enforces Universal Image Prompt Defaults (clear, bright daytime lighting unless explicitly dark/night) and requires a self-contained visual description suitable for direct insertion into an image generation prompt.
   - Non-JSON, missing fields, invalid `kind` (`character`, `location`, `object`, `general`), or empty `description` fails closed with `InvalidDataException`.
3. **Durable SQLite storage (`UiCommunityStore` `ui_subject_tags`):**
   - Keyed by `(identity_key, normalized_name)`.
   - Stores `id`, `identity_key`, `actor_display`, `name`, `normalized_name`, `kind`, `search_query`, `model`, `description`, `source_summary`, `matched_prompts_json`, `matched_prompts_count`, `raw_response`, `created_at_unix_ms`, and `updated_at_unix_ms`.
   - `TransferLogin` migrates `ui_subject_tags` rows when an account login is renamed.
4. **McPhee dynamic subject tagging (`MultiImageClient/Ui/wwwroot/mcphee/mcphee.js`):**
   - `Checker` exposes `setSubjects`, `getSubjects`, `getSubject`, `ignoreSubject`, `unignoreSubject`, `isSubjectIgnored`, `listIgnoredSubjects`, `formatSubjectExpansion`, `findSubjectMatches`, and `expandSubjectInTextarea`.
   - Registered subject words classify as `"ok"` so personal names and place names are never flagged as `unknown` or `misspelled`.
   - `renderHtml` emits `<mark class="mcphee-mark-subject" data-start="..." data-subject="...">...</mark>` with identical character metrics to the underlying textarea.
   - Multi-word subjects match longest-first on word boundaries, including trailing possessives (`'s` / `’s`).
   - Already-expanded text (`Jing (<description>)`) is detected and not re-offered for redundant expansion.
5. **Composer controls and placement (`index.html`, `app.js`, `style.css`):**
   - `#prompt-subject-matches` sits immediately below `#prompt` and renders one single-line bar per matched subject with `use <kind> description`, `keep as word`, and `edit` actions.
   - `#prompt-subjects-toggle` (`subjects & memory`) and `#prompt-subject-undo` (`undo subject expand`) sit in `#prompt-tools` directly under `#prompt`.
   - `#prompt-subjects-panel` provides history search, LLM synthesis, manual creation/editing, and stored tag management.

## API and CLI Surface

### HTTP Endpoints (`UiWorkflow.cs`)

- `GET /api/prompt/subjects?user=<user>` — list stored subject tags for the active user/account.
- `POST /api/prompt/subjects/search` — search historical prompts for `name` and optional `query` terms.
- `POST /api/prompt/subjects/synthesize` — search history, synthesize a visual description with `model` (`claude-opus-5-5` default), and save the resulting subject tag (unless `save=false`).
- `POST /api/prompt/subjects` — create or update a subject tag directly.
- `POST /api/prompt/subjects/delete` — delete a stored subject tag by `id` or `name`.

### CLI Flags (`RunOptions.cs`, `Program.cs`)

- `--subject-list [--subject-user <user>]`
- `--subject-search <name> [--subject-query <terms>] [--subject-user <user>]`
- `--subject-synthesize <name> [--subject-kind character|location|object|general] [--subject-query <terms>] [--subject-model <key>] [--subject-notes <text>] [--subject-user <user>]`
