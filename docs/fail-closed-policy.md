# Fail-Closed Policy: No Fallbacks

Fallback recovery is prohibited across MultiImageClient unless the user has explicitly required one specific fallback as product behavior.

## Required behavior

- Preserve exact identity across request ids, UI job ids, generator keys, image indexes, asset ids, post ids, hashes, and provider responses.
- Treat missing, malformed, ambiguous, rejected, incomplete, or uncorrelated data as a hard failure.
- Surface the same failure through logs, generation archives, APIs, SSE events, and UI state.
- Fix the upstream request, provider contract, parser, correlation, or validation. Do not compensate downstream.
- Reject partial results when the requested contract requires a complete set.

## Prohibited behavior

- Guessing an absent identifier or index.
- Fuzzy-matching by prompt or other non-unique text.
- Selecting the latest/nearest/first available resource.
- Reusing an older response, image, video, asset, or post.
- Treating previews, placeholders, partials, or malformed data as final output.
- Changing formats, dimensions, providers, models, transports, or configuration after failure.
- Catching an exception and continuing with substitute data or apparent success.

## Narrow exception

A fallback may exist only when the user explicitly defines that exact fallback as a product requirement. It must be documented at its call site and independently validated so that it cannot select unrelated data.

Defaults chosen before execution begins are configuration, not recovery. Once execution starts, failed or missing output must never be replaced with different output.
