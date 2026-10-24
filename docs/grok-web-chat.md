# grok-web chat

UI target `grok-web-chat`, label **grok-web chat**. It sends the prompt to grok.com as a normal chat message. A chat model, grok-3, reads it and calls grok.com's own image tool. The direct targets differ: `grok-web` sends the prompt straight to the imagine WebSocket or to `imagine-image-edit`.

## Text-only jobs (2026-10-23)

Owner request, 2026-10-23: the local grok-web chat endpoint "should be on".

Before this date the chip was disabled until an image was attached. `/api/config` sent `requiresImage: true` for it. That restriction dated from the first implementation on 2026-08-11, which supported edits only.

Decision:

- The chip is selectable with or without an attached image.
- Without an image, the message carries no attachment (`fileAttachments: []`). grok-3 generates from the text alone.
- With an image, the primary input uploads first. The message carries its asset id (`fileAttachments: [assetId]`), and grok-3 edits it through `imagine-image-edit`.
- `requiresImage` is now true only for the describe and layout-map targets.

## Behavior

- **Availability:** the target needs only the grok.com cookie file. Without one it is disabled with "grok-web cookie file not found". Both production instances have one since 2026-10-23; see [the Vibecoders grok-web session](workspaces-prd.md#vibecoders-grok-web-session-2026-10-23).
- **Transport:** `POST /rest/app-chat/conversations/new`, without a signature header. See [the transport document](grok-web-video-browser-transport.md#unsigned-app-chat-2026-10-23).
- **Image input:** the target is image-capable. Its chip shows the image icon.
- **Aspect ratio:** the chat request sends none.
  - Text-only jobs: grok-3 chooses the orientation. An explicit AR has no effect; the job still runs, as the option-mapping policy requires.
  - Image jobs: an explicit AR is rejected, as for Recraft and Ideogram 4.5. The composer disables the chip, and `POST /api/jobs` refuses the job. Choose **match input image** instead.
  - The result label shows the source's nearest ratio for edits and no ratio for text-only jobs.
- **Image count:** grok-3 decides how many images to make. `n` is not sent. Every returned image is saved.
- **No image:** a reply with text and no image is a hard failure. The error quotes the model's reply, for example a refusal or a question.
- **Prompt:** grok-3 can rewrite the prompt before it calls the image tool. In the 2026-10-23 test it passed the prompt through unchanged.
- **History flag:** the request sends `temporary: false`, unchanged since 2026-08-11. The flag name suggests grok.com keeps these conversations in account history. This was not verified.
- **Prompt length:** no published limit. The full prompt is sent. See [prompt length limits](prompt-length-limits.md).
- **Scheduling:** the target shares the `grok-web-ws` lane with direct grok-web image jobs. The lane runs one request at a time by default. Local and original production settings allow four; see [the shared-host memory budget](shared-host-memory-budget.md#grok-web-image-concurrency-2026-09-09).
- **Cost:** shown as 0, because the consumer site has no metered price.

## Goal loops

Goal loops exclude this target. The goal-loop contract requires the generator to receive the manager's exact prompt, and grok-3 can rewrite it. `/api/config` sends the reason as `goalLoopProblem`, so the goal page hides the chip. The server rejects the key with the same reason.

Grok-web video is also excluded, because it makes video from an input image.

## Verification (2026-10-23)

- `--grok-web-chat-probe` without an input image returned HTTP 200.
- grok-3 called the image tool with orientation "landscape" and the prompt unchanged.
- The probe saved one 1168×784 image that matched the prompt.
- The existing response parser read the reply without change.
- After the local restart, `/api/config` sent `requiresImage: false`, `imageCapable: true`, and `imageAspectOverride: false` for this target.
- The composer chip was enabled with no image attached.
- A text-only UI job returned one 1168×784 image in 18.4 s. It matched the prompt.
- A UI image job sent a 768×512 source to grok-web and grok-web chat.
- Each target returned one 1728×1152 edit that followed the instruction, in 6.7 s and 9.9 s.
- After the production release, the text-only probe ran on the production host with Vibecoders' cookie copy. It returned HTTP 200 and one image.

## Files

- `MultiImageClient/ImageGenerators/GrokWebImagineChatGenerator.cs`: the generator. `CreateForText` builds text-only jobs, and `CreateAsync` uploads the source for edits.
- `MultiImageClient/GrokWebClient.cs`: `BuildImageChatPayload` and `RunImageChatAsync`.
- `MultiImageClient/Implementation/UiJobs.cs`: `BuildGrokWebChatAsync`, availability, and `ImageCapableKeys`.
- `MultiImageClient/Workflows/UiWorkflow.cs`: the catalog entry and the aspect-ratio rule.
- `MultiImageClient/Implementation/UiGoalLoops.cs`: `GoalLoopExclusion`.
- `MultiImageClient/GrokWebChatProbe.cs`: the development probe, `--grok-web-chat-probe [--input-image <path>]`.
