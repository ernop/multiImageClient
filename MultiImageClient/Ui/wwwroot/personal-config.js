"use strict";

(function publishPersonalConfigurationSchema(root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.MultiImagePersonalConfiguration = api;
  }
})(typeof globalThis === "object" ? globalThis : this, function createPersonalConfigurationSchema() {
  const Format = "MultiImageClient personalized configuration";
  const Version = 2;
  const StorageKey = "mic_personal_configuration_v2";
  const Scopes = new Set(["browser", "account", "hybrid"]);

  function assertRegistry(fields) {
    if (!Array.isArray(fields) || fields.length === 0) {
      throw new Error("personal configuration field registry is empty");
    }
    const names = new Set();
    for (const field of fields) {
      if (!field || typeof field.name !== "string" || !field.name ||
          typeof field.read !== "function" || typeof field.normalize !== "function" ||
          !Scopes.has(field.scope)) {
        throw new Error("personal configuration field registry contains a malformed entry");
      }
      if (names.has(field.name)) {
        throw new Error(`personal configuration field registry contains duplicate ${field.name}`);
      }
      names.add(field.name);
    }
    return names;
  }

  function requireExactDocumentFields(raw, fieldNames) {
    if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
      throw new Error("configuration must be an object");
    }
    const expected = new Set(["format", "version", ...fieldNames]);
    const actual = Object.keys(raw);
    const missing = [...expected].filter((key) => !Object.hasOwn(raw, key));
    const unknown = actual.filter((key) => !expected.has(key));
    if (missing.length || unknown.length) {
      const details = [
        missing.length ? `missing ${missing.join(", ")}` : "",
        unknown.length ? `unknown ${unknown.join(", ")}` : "",
      ].filter(Boolean).join("; ");
      throw new Error(`configuration has the wrong fields: ${details}`);
    }
  }

  function build(fields) {
    assertRegistry(fields);
    const document = { format: Format, version: Version };
    for (const field of fields) {
      document[field.name] = field.read();
    }
    return document;
  }

  function normalize(raw, fields, migrate) {
    const names = assertRegistry(fields);
    let candidate = raw;
    if (candidate?.version !== Version) {
      if (typeof migrate !== "function") {
        throw new Error(
          `unsupported configuration version ${String(candidate?.version)}; expected ${Version}`);
      }
      candidate = migrate(candidate, Version);
    }
    requireExactDocumentFields(candidate, names);
    if (candidate.format !== Format) {
      throw new Error(`format must be exactly "${Format}"`);
    }
    if (candidate.version !== Version) {
      throw new Error(
        `unsupported configuration version ${String(candidate.version)}; expected ${Version}`);
    }
    const normalized = { format: Format, version: Version };
    for (const field of fields) {
      normalized[field.name] = field.normalize(candidate[field.name]);
    }
    return normalized;
  }

  function parseStored(text) {
    if (text === null) return null;
    let parsed;
    try {
      parsed = JSON.parse(text);
    } catch (error) {
      throw new Error(`stored personal configuration is not valid JSON: ${error.message || error}`);
    }
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      throw new Error("stored personal configuration must be an object");
    }
    if (parsed.format !== Format) {
      throw new Error(`stored personal configuration format must be exactly "${Format}"`);
    }
    if (parsed.version !== Version) {
      throw new Error(
        `stored personal configuration version ${String(parsed.version)} is unsupported; expected ${Version}`);
    }
    return parsed;
  }

  function browserFields(fields) {
    assertRegistry(fields);
    return fields.filter((field) => field.scope !== "account").map((field) => field.name);
  }

  return Object.freeze({
    Format,
    Version,
    StorageKey,
    assertRegistry,
    build,
    normalize,
    parseStored,
    browserFields,
  });
});
