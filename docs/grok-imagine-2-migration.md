# Grok Imagine 2.0 API migration

## Decision

Date: 2026-09-08

The stable `grok-api-pro` key now sends `grok-imagine-image-2.0`.
Its user-facing name is `grok-api 2.0`.

The stable key remains unchanged because jobs, preferences, groups, and archives
use it as durable identity.

The `grok-api` key still sends `grok-imagine-image` 1.0.
xAI states that this model is not affected by the November 2 retirement.

The two grok.com transports remain unchanged.
The API announcement does not apply to their consumer-web protocol.

## Request behavior

- Grok Imagine 1.0 receives no `quality` field.
- Grok Imagine 2.0 accepts `low`, `medium`, or `auto`.
- UI `high`, `xhigh`, and `max` map to 2.0 `medium`.
- UI `low`, `medium`, and `auto` pass through unchanged.
- CLI compatibility workflows use 2.0 `medium`.
- Generation and editing both send the selected 2.0 quality.
- Editing sends up to five ordered source images to 2.0.
- Legacy 1.0 editing sends only the primary source image.
- Source-shape matching recognizes the new `21:9` and `5:2` ratios.
- Existing direct aspect-ratio inputs can send `21:9` or `5:2`.

This mapping avoids the unsupported `high` value that older code sent.
It also avoids xAI's retiring `grok-imagine-image-pro` alias.

## Pricing

The authenticated model catalog reported this matrix on 2026-09-08:

- `grok-imagine-image`: $0.02 per output image.
- 2.0 low 1k: $0.04.
- 2.0 low 2k: $0.06.
- 2.0 medium 1k: $0.06.
- 2.0 medium 2k: $0.08.

Provider-reported usage remains in diagnostic logs when xAI returns it.

## Retirement

xAI retires `grok-imagine-image-quality` on November 2, 2026.
The `grok-imagine-image-pro` alias already redirects to that model.

After retirement, both aliases serve 2.0 at `quality: low`.
Explicit 2.0 requests preserve the application's selected quality instead.

## Verification

Automated tests cover exact model identity, quality mapping, current pricing,
five-reference serialization, and the two new aspect ratios.

Canonical sources:

- <https://docs.x.ai/developers/migration/imagine-image-quality-nov-2>
- <https://docs.x.ai/developers/model-capabilities/images/generation>
- <https://docs.x.ai/developers/models/grok-imagine-image-2.0>
