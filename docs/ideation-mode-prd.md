# Ideation Mode (Claude Concept Generation) — PRD

Status: **proposed / core top-level product need**. Not yet implemented. This document
is the durable statement of the idea so it stays a first-class goal and doesn't get
lost between sessions.

## 1. The Insight

Frontier text models (Claude Opus/Fable class) cannot make images, but they can
generate varied concepts from a fuzzy goal, a frustration log, or a pile of failed
attempts. The motivating manual workflow:

> "i'm trying iteratively to make a super good image to show this concept
> \<text log w/prompt and past attempts\> — can you grind up the attached log and then
> see what you can make of it? and make 5 separate, highly variable proposals? feel
> free completely to go way out of band for this one — by no means do they all have
> to take the same form!"

In the motivating manual trials, proposals varied in form and metaphor and were
ready to convert into image prompts. Ideation Mode productizes that loop inside the
`--ui` web app.

Why it belongs in THIS app: MultiImageClient's core thesis is fan-out comparison —
one prompt across M generators. Ideation adds fan-out on the orthogonal axis: one
fuzzy *intent* across N *concepts*. The N×M matrix (5 wildly different concepts ×
4 providers = 20 candidate images from a single brief) is something no provider's
own UI can do. Concept selection precedes renderer choice and expands the search
space from M outputs to N×M outputs.

## 2. User-Facing Flow

Ideation is a second input mode in the web UI, alongside the existing direct
composer. The user:

1. **Briefs Claude.** Freeform text (the goal, a frustration log, past prompts that
   failed, tone constraints) and/or pasted image(s) (past attempts, reference
   material). Sets N = number of ideas wanted (default 5).
2. **Claude returns N structured ideas.** Each idea is deliberately *highly
   variable* — different forms, different metaphors, explicitly encouraged to go out
   of band. Each arrives as an **idea card**: short title, the form/approach in one
   line, the full ready-to-send image prompt, and a brief rationale.
3. **The user disposes of ideas one of two ways:**
   - **Manual (curate):** read each card; per card, edit the prompt if desired, pick
     generators (the normal generator checklist), and Send — or Skip. Each send is a
     normal UI job.
   - **Auto (run all):** one click sends *every* idea to *every* currently-selected
     generator. N ideas → N jobs → N×M results, each job producing its usual
     contact sheet.
4. **Iterate.** The ideation session is a conversation: "more like #3", "less
   allegorical, more literal", "combine 2 and 5" — follow-up messages refine or
   extend the idea list without restating the brief.

Result cards link back to the idea that produced them (idea title shown on the job
card), so the user can see which *concept* won, not just which provider.

## 3. Parseable Output Through Forced Tool Use

Forced tool use requires a tool call matching the declared schema; invalid or
incomplete output remains an error. We define a tool whose `input_schema` is our idea
list:

```json
{
  "name": "submit_ideas",
  "input_schema": {
    "type": "object",
    "properties": {
      "ideas": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "title":     { "type": "string", "description": "3-6 word handle" },
            "form":      { "type": "string", "description": "one-line description of the approach/visual form" },
            "prompt":    { "type": "string", "description": "complete, self-contained image-generation prompt, ready to send verbatim" },
            "rationale": { "type": "string", "description": "1-2 sentences: why this angle might work" }
          },
          "required": ["title", "form", "prompt", "rationale"]
        }
      }
    },
    "required": ["ideas"]
  }
}
```

With `tool_choice = {"type": "tool", "name": "submit_ideas"}` the model MUST respond
with a JSON object validated against this schema — no markdown fences, no prose
preamble, no scraping. `Anthropic.SDK 4.1.1` (already referenced by the solution)
supports tool use and image content blocks (vision), so both text and pasted-image
briefs work with the existing dependency.

If a response arrives malformed, fail the ideation request. Preserve the raw response
only in redacted diagnostics for debugging; never present unvalidated prose as idea
output. Fix the schema, forced-tool request, or parser instead of substituting text.

## 4. System Prompt Requirements

The ideation system prompt is where the value lives. It must encode:

- **Variability is the point.** N *separate, highly variable* proposals; they must
  not share the same form; explicitly permitted and encouraged to go way out of
  band (diagram vs. photo vs. allegory vs. typographic poster vs. diorama...).
- **Prompts must be complete and self-contained** — ready to paste into any
  text-to-image model verbatim, no "as above" references to the brief.
- **The universal image defaults** (workspace rule): clear, bright, full normal
  daytime lighting unless the brief explicitly asks for dark/night/gloom; readable,
  coherent, organized composition; concise high-contrast text when text is needed.
  Baked into every generated prompt's text (required for gpt-image-2 especially).
- **Grind the attachments.** If a log of past attempts is provided, diagnose *why*
  they failed before proposing; if images are attached, read them as evidence of
  what didn't work, not as style targets (unless the brief says otherwise).

## 5. Architecture

Small delta over the existing `--ui` machinery — the job model already absorbs the
expensive half of this feature.

### New pieces

- **`TextLLMs/ClaudeIdeationService.cs`** — NOT `ClaudeService`, which is hardwired
  to Claude 3 Haiku with rewrite-specific refusal heuristics. Ideation needs a
  top-tier model, a large output budget (N full prompts + rationales), higher
  temperature, vision input, and forced tool use. Model name comes from a new
  optional `Settings.IdeationModel` (default: current best Opus/Fable-class model);
  reuses `Settings.AnthropicApiKey`. Availability surfaces through the same
  pattern as generators: no key → ideation panel visible but disabled with the
  exact problem string.
- **Ideation session** (`Implementation/UiIdeation.cs`) — mirrors `UiJob`:
  server-side object with an id, the conversation history (briefs, attached image
  refs, every returned idea list), an append-only event log for SSE replay, and a
  registry for page-reload hydration. Sessions persist for the process lifetime
  like jobs.
- **Endpoints** (same minimal-API style in `UiWorkflow`):
  - `POST /api/ideation` — multipart: brief text, N, optional image(s) → `{id}`;
    fires the Claude call async.
  - `GET  /api/ideation` — session summaries for hydration.
  - `GET  /api/ideation/{id}/events` — replayable SSE (`accepted` / `ideas` /
    `error`).
  - `POST /api/ideation/{id}/followup` — continue the conversation (refine/extend).
- **Dispatch reuses `POST /api/jobs` unchanged**, with one optional extra form
  field (`ideaRef` = session id + idea index) so the job card can display the idea
  title and the archive records lineage. Auto mode is the frontend looping N job
  posts — no new server orchestration needed.

### Frontend (`Ui/wwwroot/app.js`)

- Mode toggle or panel: "Ideate" alongside the direct composer.
- Brief box (+ the existing paste/drag image affordance), N selector, Send-to-Claude.
- Idea cards: title, form, rationale, editable prompt textarea, per-card generator
  send (defaults to the composer's current generator selection), Skip,
  and a session-level **Run all N × selected generators** button with a cost/count
  confirmation ("This will start 5 jobs across 4 generators").
- Follow-up input under the cards for conversational refinement.

### Provenance & archive

- Every ideation exchange is appended to `saves/<day>/Ideation/<sessionId>.jsonl`
  (brief, model, raw tool-use JSON) — same spirit as `meta-web-capture` diagnostics.
- Add `TransformationType.ClaudeIdeation`; dispatched jobs record the idea title +
  session ref in `PromptDetails`, so annotated images and contact sheets show the
  concept lineage.

## 6. Cost & Failure Notes

- One Opus-class call with a few thousand output tokens costs cents — noise
  compared to the N×M image generation it steers. No throttling needed beyond a
  sane N cap (say N ≤ 10) and the existing job concurrency limit (4).
- Refusals: unlike the Haiku rewriter, don't pre-filter with `claude-bad.txt` word
  lists — briefs are conversational, and a strong model with tool use rarely
  refuses ideation. If it declines, show the response text in the session and let
  the user rephrase.
- Auto mode multiplies spend deliberately: the confirmation dialog states the job
  count before firing. `--fast`-style cheap tiers remain available per job.

## 7. Phasing

1. **Phase 1 (core loop):** brief → N idea cards → manual per-card send. Text-only
   briefs. This alone captures most of the value.
2. **Phase 2:** image attachments in the brief (vision); Run-all auto mode;
   follow-up conversation.
3. **Phase 3 (speculative):** close the loop — feed the N×M results (as a contact
   sheet) back to Claude for critique and a next round of ideas; "tournament"
   mode where Claude ranks its own concepts against the rendered results.

## 8. Non-Goals

- Not a general chat UI. The session exists to produce dispatchable idea cards;
  freeform chat that never yields structured ideas is out of scope.
- No CLI surface initially (`--ui` only); the REPL/batch flows keep their existing
  Haiku rewrite step. Revisit after the web flow proves out.
- No automatic selection of "the best" result — the human curates.
