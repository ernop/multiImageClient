# Directed prompt rewrites

Settled 2026-09-05. Model choice updated 2026-10-06.

## Requirements and behavior

| Requirement | Behavior |
|---|---|
| Immediate expansion | Two buttons beside **get Claude's advice** start a rewrite without opening a dialog. |
| Exact model choice | **flesh out · Opus 5.5** sends `claude-opus-5-5`; **flesh out · Sonnet 5.5** sends `claude-sonnet-5-5`. |
| Anthropic only | The area offers no OpenAI model. The server rejects every other model identifier. |
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

## Anthropic-only model pair (2026-10-06)

The owner limited **flesh out** to two models for now: Opus 5.5 and Sonnet 5.5.
This decision supersedes the 2026-09-05 Fable 5.1 and GPT-6 Astra pair.

- The area has no OpenAI support.
- The change removes the GPT-6 Astra button, the OpenAI request path, and the OpenAI key check.
- The change also removes the Fable 5.1 button.
- The server rejects any other `model` value with "Unknown rewrite model." It makes no provider call.
- This rejection includes `gpt-6-astra` and `claude-fable-5-1`.
- Availability depends only on `AnthropicApiKey`.
- Failure messages use the Anthropic billing and key recovery links.
- Saved exchanges keep their recorded model identifiers.
- Older Fable 5.1 and GPT-6 Astra versions remain restorable. Restoration makes no provider call.

## Provider contract

- Use Anthropic Messages for both models.
- Send no `thinking` field. Claude 5 models keep their default adaptive thinking.
- Allow 16,000 output tokens and five minutes per call.
- Admit at most two directed calls per process. Reject excess calls without an in-memory queue.
- Bound buffered provider JSON to 2 MiB.
- Require `stop_reason` `end_turn` and the requested model identity.
- Extract only final text blocks. Exclude thinking blocks from the replacement.
- Retain the full response in the saved exchange, including unsuccessful responses.
- Reject replacement text above 100,000 characters. Never truncate it.
- Never switch models after a failure.

Both identifiers match the shared goal-loop manager catalog.
That catalog live-verified both identifiers on 2026-10-02.
Both models use the Messages contract already implemented by the goal-loop client.

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
- `MultiImageClient.Tests/DirectedPromptRewriteTests.cs`: Anthropic-only model set, completion contracts, identity, exact text, and durable pagination.
- `tools/test-prompt-rewrites.cjs`: browser regression for both buttons, history, restoration, failures, and layout.

## Verification

2026-10-06, Opus 5.5 and Sonnet 5.5:

- The full solution passed 515 C# tests. The JavaScript suites passed 30 tests.
- The browser regression passed with both models. It checks that the page offers exactly these two models.
- The regression's identity step now opens the Settings panel first. The creating-as field moved there on 2026-09-09.
- Desktop and 390-pixel layouts show both labels in full.
- The restarted local server advertised only the two models.
- It rejected `gpt-6-astra` and `claude-fable-5-1` with HTTP 400.
- One live call per model succeeded through `POST /api/prompt/advice`.
- Each reply echoed its exact model identifier and stopped with `end_turn`.
- Each call used 454 input and 588 output tokens. The pair cost about $0.02.

2026-09-05, Fable 5.1 and GPT-6 Astra (superseded):

- The project build passed.
- All 19 directed-rewrite and community tests passed.
- Browser fixtures tested both models, restoration, delayed replies, provider failure, pagination, newline preservation, and identity clearing.
- Desktop and 390-pixel layouts kept the new controls within the viewport.
- Browser verification used simulated provider replies only.

Run the browser regression with `node tools/test-prompt-rewrites.cjs` in an environment that provides Playwright.
Set `NODE_PATH` to a `node_modules` directory that contains `playwright` when the repository has none.

## Unsubmitted prompt highlight (2026-09-05)

Use one subtle amber tint and left edge for changed composer text awaiting image submission.
Apply the same treatment to the exact matching version in visible prompt history.
Typing, advice, rewrites, and restores mark the current text as changed.
Clear the treatment only when an image-generation job accepts that same composer version.
Describe-only jobs and rejected submissions do not clear it.
A delayed acceptance cannot clear a newer edit.
The cue means “not yet submitted for images”; it does not claim that generation succeeded.
Keep the cue within the current page session; do not infer global generation history.
Use a hover title to explain the color without adding another visible label.
