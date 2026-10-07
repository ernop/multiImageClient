# Directed prompt rewrites

Settled 2026-09-05. Flesh-out models, the advice model, and in-place editing updated 2026-10-06.

## Requirements and behavior

| Requirement | Behavior |
|---|---|
| Immediate expansion | Two buttons beside **get Claude's advice** start a rewrite without opening a dialog. |
| Exact model choice | **flesh out · Opus 5.5** sends `claude-opus-5-5`; **flesh out · Sonnet 5.5** sends `claude-sonnet-5-5`. |
| Anthropic only | The area offers no OpenAI model. The server rejects every other model identifier. |
| Preserve intent | Expand one coherent image prompt while preserving explicit subjects, constraints, counts, names, style, and requested text. |
| Explore specifics | Develop relevant composition, action, setting, spatial relationships, materials, color, lighting, and expressive details. |
| Lighting defaults | Use bright daytime lighting unless the source explicitly requests another condition. |
| In-place result | Replace the composer text with the complete response. Leave prompt history closed (2026-10-06). |
| One-click undo | **undo Claude edit** restores the text from before the latest applied flesh out or advice edit. |
| Restorable history | Every source and successful replacement in **prompt history** has a restore button. |
| Preserve displaced text | A restore records the current composer text before applying the selected saved version. |
| Durable history | Retain all exchanges in SQLite, including failures, manual source edits, advice, rewrites, and restores. |
| Bounded reads | Display 20 complete exchanges per page, newest first. Older/newer controls reach the entire history. |
| Readable versions | Source and replacement text remain expanded, without clipping or inner scroll boxes. Wire details may collapse. |
| Concurrent editing | Keep edits made during a provider request. Prompt history keeps the reply for explicit restoration. |
| Failure behavior | Preserve the composer on errors. Reject incomplete, refused, empty, oversized, or wrong-model replies. |
| Identity | Scope history and restore lookup to the resolved account or local display identity. |

Each rewrite uses only the current prompt and the fixed direction.
Images attached to the composer are not sent to these text rewrite calls.
Earlier prompts remain available for restoration but do not enter the provider conversation automatically.
Manual typing becomes durable when a rewrite, advice call, or restore captures it as source text.
History is a sequence of saved changes, not a record of every keystroke.
The ordinary Claude advice dialog retains its custom instruction.
Since 2026-10-06 it uses Opus 5.5 instead of Haiku 4.5.
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

## Advice uses Opus 5.5 (2026-10-06)

The owner moved **get Claude's advice** from Haiku 4.5 to Opus 5.5.
This decision supersedes the earlier Haiku advice model.

- Advice keeps its dialog, editable instruction, default instruction, and saved instruction.
- Advice sends `claude-opus-5-5` through the same Anthropic Messages request as flesh out.
- The request omits `temperature`. Opus 5.5 rejects it with HTTP 400: "`temperature` is deprecated for this model."
- The old Haiku request used temperature 0 and 8,192 output tokens.
- Advice now uses the flesh-out reply contract below, including exact reply text. The old Haiku path trimmed surrounding whitespace.
- Advice keeps its refusal-phrase check. A matching reply fails and leaves the prompt unchanged.
- Advice and flesh out share the two-call limit per server process.
- `/api/config` `claudeAdvice` adds `model` and `label`. The dialog submit button reads **send to Opus 5.5**.
- Saved advice records store `claude-opus-5-5`. Older Haiku records remain restorable.
- CLI batch rewrites through `ClaudeService.RewritePromptAsync` still use Haiku 4.5. They are outside this area.

## In-place edits (2026-10-06)

The owner asked for one-click edits.
A rewrite button only changes the prompt text.
The owner then chooses **Generate** as usual.
This decision supersedes the 2026-09-05 inline history display.

- Flesh out and advice replace the composer text in place.
- They do not open prompt history.
- Before this change, each rewrite opened prompt history with the newest 20 saved exchanges.
- That list included records from earlier months. One click therefore appeared to add many rows.
- Prompt history opens only from the **prompt history** button.
- An open history list refreshes after each rewrite, advice call, or restore.
- **undo Claude edit** appears after each applied flesh out or advice edit.
- Undo uses the saved restoration path. It restores the text from before that edit.
- The undo button disappears after any restore.
- The status line under the prompt tools shows progress, errors, and concurrent edits.
- Progress uses the short model name, for example "Opus 5.5 is expanding the prompt…".
- `/api/config` `promptRewrites[]` adds `label` from the shared manager catalog.
- The page does not re-check replies from this server. The server and page ship together.
- Provider replies keep their strict server-side checks.

## Provider contract

- Use Anthropic Messages for flesh out and advice.
- Send no `thinking` field. Claude 5 models keep their default adaptive thinking.
- Send no `temperature` field. Claude 5.5 models reject it.
- Allow 16,000 output tokens and five minutes per call.
- Admit at most two flesh-out or advice calls per process. Reject excess calls without an in-memory queue.
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

`GET /api/config` adds `promptRewrites[]` with `model`, `label`, `available`, and `availabilityProblem`.
Its `claudeAdvice` object reports `available`, `availabilityProblem`, `model`, and `label`.
Both labels come from the shared manager catalog.
Availability checks the existing provider key settings; it does not prove account access to a model.

`POST /api/prompt/advice` accepts form fields `prompt`, `instruction`, and `user`.
The browser uses URL-encoded forms to preserve exact newlines; multipart forms remain accepted.
An omitted `model` selects custom advice through Opus 5.5.
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

- `TextLLMs/DirectedPromptRewrite.cs`: fixed direction, flesh-out and advice models, shared request body, bounded HTTP calls, strict output parsing.
- `TextLLMs/ClaudeService.cs`: system prompt, wire format, and advice refusal phrases.
- `Workflows/UiWorkflow.cs`: availability, model labels, rewrite/advice/restore handling, and paged history API.
- `Implementation/UiCommunity.cs`: exact record lookup and stable history pagination.
- `Ui/wwwroot/prompt-rewrites.js`: in-place rewrite controls, the prompt history panel, restoration, and concurrent-edit protection.
- `Ui/wwwroot/app.js`, `index.html`, `style.css`: composer integration, the shared undo button, and layout.
- `MultiImageClient.Tests/DirectedPromptRewriteTests.cs`: Anthropic-only model set, advice model, model labels, request body without `temperature`, completion contracts, identity, exact text, and durable pagination.
- `tools/test-prompt-rewrites.cjs`: browser regression for in-place edits with history closed, undo identity, history, restoration, failures, and layout.

## Verification

2026-10-06, in-place edits:

- The full solution passed 519 C# tests. The JavaScript suites passed 30 tests.
- The browser regression confirms that flesh out leaves prompt history closed and sends no history request.
- It also checks the undo exchange identity. History opens only from its button.
- The restarted local server labelled both flesh-out models and advice with short names.
- One live Sonnet 5.5 flesh out replaced the prompt in place. Prompt history stayed closed.
- **undo Claude edit** appeared and restored the exact source text.
- One live Opus 5.5 advice call fixed two typos in place. The page sent no history request.
- Opening **prompt history** then showed the three saved exchanges for the test user.

2026-10-06, advice on Opus 5.5:

- A direct Opus 5.5 call with `temperature` 0 returned HTTP 400.
- The full solution passed 518 C# tests. The JavaScript suites passed 30 tests. The browser regression passed.
- The restarted local server reported advice as `claude-opus-5-5` with the label Opus 5.5.
- The advice dialog showed **send to Opus 5.5**.
- One live advice call fixed three typos in 2.6 seconds and changed nothing else.
- The saved record stored `claude-opus-5-5`, `end_turn`, and one text block.
- The call used 224 input and 27 output tokens, about $0.0014.

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
