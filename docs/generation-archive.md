# Generation archive

Every image/video generator invocation is recorded in a local SQLite database.
The archive is enabled by default and is independent of the optional per-image
`SaveJsonLog` sidecars.

Default path:

```text
{ImageDownloadBaseFolder}/generation-history.sqlite3
```

Set `GenerationArchiveDbPath` to override it, or set
`EnableGenerationArchive` to `false` to disable structured capture.

## What is recorded

- One `generation_attempts` row for every provider invocation, including
  returned failures, thrown exceptions, unavailable-provider skips, timing,
  estimated cost, prompt details, runtime metadata, and a redacted snapshot of
  the configured generator.
- Ordered `prompt_steps` preserving prompt transformation lineage.
- One `provider_calls` row per HTTP, SSE, WebSocket, gRPC, Playwright, or
  ComfyUI transport operation, with request/response JSON, status, timing, and
  errors.
- `assets` rows pointing to input, raw, annotated, and generated-media files,
  including byte length and SHA-256. Image/video bytes stay on disk.
- `structured_fields` flattens nested prompt, generator, request, response,
  metadata, and result JSON into JSON-path/value rows for SQL queries.

Credentials, cookies, authorization headers, base64 image bodies, and binary
payloads are not stored. Signed URL query strings are stripped. Large values
are replaced by length/hash metadata.

## Useful queries

Recent attempts:

```sql
SELECT started_at_utc, generator_api_type, success, prompt, error_message
FROM generation_attempts
ORDER BY started_at_utc DESC
LIMIT 50;
```

Failures by provider:

```sql
SELECT generator_api_type, COUNT(*) AS failures
FROM generation_attempts
WHERE success = 0
GROUP BY generator_api_type
ORDER BY failures DESC;
```

All fields returned by one provider call:

```sql
SELECT json_path, value_type, text_value, number_value, bool_value
FROM structured_fields
WHERE owner_type = 'provider_call'
  AND owner_id = $call_id
  AND scope = 'response'
ORDER BY json_path;
```

Files produced by an attempt:

```sql
SELECT kind, image_index, variant, local_path, content_type, byte_length, sha256
FROM assets
WHERE attempt_id = $attempt_id
ORDER BY image_index, variant;
```

SQLite uses WAL mode so concurrent UI jobs can append safely. Archive failures
are logged but never fail the image-generation request.

The app dynamically loads the operating system SQLite library
(`winsqlite3.dll`, `libsqlite3.so.0`, or `libsqlite3.dylib`) instead of bundling
native SQLite. This avoids shipping a duplicate native database engine and its
associated security-update lifecycle.
