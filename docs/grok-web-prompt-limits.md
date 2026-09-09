# Grok-web prompt truncation

## Decision — 2026-09-09

The user requires automatic truncation instead of a shortening dialog or rewrite.
Grok-web image generation sends the longest complete Unicode prefix within 8,192 UTF-8 bytes.
Truncation occurs after prompt composition and before request capture and transmission.
Other generators retain their existing behavior.
The original composer text remains unchanged.
The composer warns when prompt text plus endpoint additions exceeds this byte budget.
The server logs the original and transmitted character counts without logging prompt text there.

The previous cutoff used 8,192 UTF-16 units.
Non-ASCII prompts can exceed 8,192 UTF-8 bytes within that cutoff.
The July 30 boundary observation did not establish how Grok counts Unicode text.
This byte budget is a local policy, not a newly verified provider limit.
Further provider rejections remain visible errors without automatic retries.

## Contract and files

`GET /api/config` exposes `maxPromptUtf8Bytes` alongside the existing `maxPromptChars` field.
`GrokWebClient.TruncateImagePrompt` applies the byte budget to WebSocket image requests.
`Ui/wwwroot/app.js` measures UTF-8 bytes for the notice.
`MultiImageClient.Tests/GrokWebPromptTests.cs` checks ASCII, accented text, CJK text, emoji, and prefix boundaries.
