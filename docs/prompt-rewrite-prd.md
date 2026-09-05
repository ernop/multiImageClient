# Directed prompt rewrites

Settled 2026-09-05.

## Requirements and behavior

| Requirement | Behavior |
|---|---|
| Immediate expansion | Two buttons beside **get Claude's advice** start a rewrite without opening a dialog. |
| Exact model choice | **flesh out · Fable 5.1** sends `claude-fable-5-1`; **flesh out · GPT-6** sends `gpt-6-astra`. |
| Preserve intent | Expand one coherent image prompt while preserving explicit subjects, constraints, counts, names, style, and requested text. |
| Explore specifics | Develop relevant composition, action, setting, spatial relationships, materials, color, lighting, and expressive details. |
| Lighting defaults | Use bright daytime lighting unless the source explicitly requests another condition. |
| Inline result | Apply the complete response to the composer and show complete source and replacement text below it. |
| Restorable history | Every displayed source and successful replacement has a restore button. |
| Preserve displaced text | A restore records the current composer text before applying the selected saved version. |
| Durable history | Retain all exchanges in SQLite, including failures, manual source edits, advice, rewrites, and restores. |
| Bounded reads | Display 20 complete exchanges per page, newest first. Older/newer controls reach the entire history. |
| Readable versions | Source and replacement text remain expanded, without clipping or inner scroll boxes. Wire details may collapse. |
| Concurrent editing | Keep edits made during a provider request. Show the saved reply for explicit restoration. |
| Failure behavior | Preserve the composer on errors. Reject incomplete, refused, empty, oversized, or wrong-model replies. |
| Identity | Scope history and restore lookup to the resolved account or local display identity. |

Each rewrite uses only the current prompt and the fixed direction.
Images attached to the composer are not sent to these text rewrite calls.
Earlier prompts remain available for restoration but do not enter the provider conversation automatically.
Manual typing becomes durable when a rewrite, advice call, or restore captures it as source text.
History is a sequence of saved changes, not a record of every keystroke.
The ordinary Claude advice dialog retains its custom instruction and Haiku model.
Its undo control now uses the same durable restoration path.

## Provider contract

- Use Anthropic Messages for Fable 5.1, retaining its default adaptive thinking behavior.
- Use OpenAI Responses for GPT-6 Astra with `reasoning.effort=high` and `store=false`.
- Allow 16,000 output tokens and five minutes per call.
- Admit at most two directed calls per process. Reject excess calls without an in-memory queue.
- Bound buffered provider JSON to 2 MiB.
- Require normal completed output and the requested model identity.
- Extract only final text blocks. Exclude provider reasoning from the replacement.
- Retain the full response in the saved exchange, including unsuccessful responses.
- Reject replacement text above 100,000 characters. Never truncate it.
- Never switch models after a failure.

The GPT-6 identifier follows the [official model documentation](https://developers.openai.com/api/docs/models/gpt-6-astra).
The request follows the [Responses reference](https://developers.openai.com/api/reference/cli/resources/responses/methods/create).
Both sources were checked on 2026-09-05.
Fable uses the Messages contract already implemented by the goal-loop client.

## API and storage

`GET /api/config` adds `promptRewrites[]` with `model`, `available`, and `availabilityProblem`.
Availability checks the existing provider key settings; it does not prove account access to a model.

`POST /api/prompt/advice` accepts form fields `prompt`, `instruction`, and `user`.
The browser uses URL-encoded forms to preserve exact newlines; multipart forms remain accepted.
An omitted `model` retains custom Claude advice.
An exact directed model selects the fixed expansion instruction; client instructions cannot override it.

`model=restore` additionally requires `exchangeId` and `side=original|result`.
The server resolves that exact record within the current user's history.
A failed exchange has no restorable result.
Restoration needs no provider key and makes no provider call.

Successful responses contain `replacement`, `model`, `originalPrompt`, and `exchangeId`.
Provider failures return HTTP 502 with the saved exchange identifier.

`GET /api/prompt/advice/history` retains `user` and `limit`.
Optional `beforeTime` and `beforeId` form a complete cursor; both must be supplied together.
Ordering uses descending request time and record ID, including equal timestamps.
The existing `ui_claude_prompt_exchanges` table holds all model choices and restores.
Its historical name remains unchanged to preserve existing records.
No new server history cache exists.

## Files

- `TextLLMs/DirectedPromptRewrite.cs`: fixed direction, model selection, bounded HTTP calls, strict output parsing.
- `Workflows/UiWorkflow.cs`: availability, rewrite/restore handling, and paged history API.
- `Implementation/UiCommunity.cs`: exact record lookup and stable history pagination.
- `Ui/wwwroot/prompt-rewrites.js`: inline controls, version display, restoration, and stale-response protection.
- `Ui/wwwroot/app.js`, `index.html`, `style.css`: composer integration and layout.
- `MultiImageClient.Tests/DirectedPromptRewriteTests.cs`: completion contracts, identity, exact text, and durable pagination.

## Verification

- The project build passed on 2026-09-05.
- All 19 directed-rewrite and community tests passed.
- Browser fixtures tested both models, restoration, delayed replies, provider failure, pagination, newline preservation, and identity clearing.
- Desktop and 390-pixel layouts kept the new controls within the viewport.
- Browser verification used simulated provider replies; live provider access remains unverified.
- Run the browser regression with `node tools/test-prompt-rewrites.cjs` in an environment that provides Playwright.
