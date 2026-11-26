"use strict";

// Respectful, bounded local UI usage telemetry client.
// Records UI workflow actions (never prompt text, drafts, keystrokes, or
// credentials), batches events in memory, flushes only when events exist, and
// uses window.fetch so shared environments attach X-Mic-Identity automatically.
(function publishTelemetry(root, factory) {
  const api = factory(root);
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.MultiImageTelemetry = api;
  }
})(typeof globalThis === "object" ? globalThis : this, function createTelemetry(root) {
  const TabStorageKey = "mic_telemetry_tab_v1";
  const MaxBatchSize = 24;
  const MaxQueuedEvents = 64;
  const FlushIntervalMs = 15_000;
  const MaxStringChars = 80;
  const MaxDataKeys = 16;
  const EventNamePattern = /^[a-z][a-z0-9_.-]{0,47}$/;
  const DataKeyPattern = /^[a-zA-Z0-9_]{1,32}$/;
  const ForbiddenKeyFragments = [
    "prompt", "text", "instruction", "description", "notes", "draft",
    "cookie", "password", "token", "secret", "email", "address",
    "url", "query", "content", "body", "header", "useragent", "ua",
  ];

  function isForbiddenKey(key) {
    const lower = String(key).toLowerCase();
    if (lower === "ip") return true;
    return ForbiddenKeyFragments.some((frag) => lower.includes(frag));
  }

  function sanitizeData(data) {
    if (!data || typeof data !== "object" || Array.isArray(data)) return undefined;
    const out = {};
    let count = 0;
    for (const [key, val] of Object.entries(data)) {
      if (count >= MaxDataKeys) break;
      if (!DataKeyPattern.test(key) || isForbiddenKey(key)) continue;
      if (val === undefined) continue;
      if (val === null || typeof val === "boolean") {
        out[key] = val;
        count++;
      } else if (typeof val === "number") {
        if (!Number.isFinite(val)) continue;
        out[key] = val;
        count++;
      } else if (typeof val === "string") {
        const cleaned = val.replace(/[\x00-\x08\x0b-\x1f]/g, " ").trim().slice(0, MaxStringChars);
        out[key] = cleaned;
        count++;
      }
    }
    return count > 0 ? out : undefined;
  }

  function viewportBucket(width) {
    const w = Number(width) || 0;
    if (w > 0 && w < 700) return "phone";
    if (w >= 700 && w < 1024) return "tablet";
    return "desktop";
  }

  function generateTabId() {
    if (root.crypto && typeof root.crypto.getRandomValues === "function") {
      const bytes = new Uint8Array(6);
      root.crypto.getRandomValues(bytes);
      return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
    }
    return Math.random().toString(16).slice(2, 14).padEnd(12, "0");
  }

  function resolveTabId() {
    try {
      const existing = root.sessionStorage?.getItem(TabStorageKey);
      if (existing && /^[a-zA-Z0-9_-]{4,32}$/.test(existing)) return existing;
      const fresh = generateTabId();
      root.sessionStorage?.setItem(TabStorageKey, fresh);
      return fresh;
    } catch {
      return generateTabId();
    }
  }

  function createClient({ page, apiUrl, fetchImpl } = {}) {
    const pageName = String(page || "composer");
    const resolveUrl = typeof apiUrl === "function" ? apiUrl : (p) => p;
    const sendFetch = typeof fetchImpl === "function"
      ? fetchImpl
      : (...args) => root.fetch(...args);
    const tabId = resolveTabId();
    const queue = [];
    let seq = 0;
    let flushTimer = 0;
    let flushing = false;
    let visibleSince = Date.now();
    let hiddenEmittedForSpan = false;

    function scheduleFlush() {
      if (flushTimer || queue.length === 0) return;
      flushTimer = setTimeout(() => {
        flushTimer = 0;
        void flush({ keepalive: false });
      }, FlushIntervalMs);
    }

    function track(ev, data) {
      const name = String(ev || "");
      if (!EventNamePattern.test(name)) {
        if (root.console && typeof root.console.error === "function") {
          root.console.error(`Telemetry ignored invalid event name: ${name}`);
        }
        return false;
      }
      const cleaned = sanitizeData(data);
      if (queue.length >= MaxQueuedEvents) {
        queue.shift();
      }
      const entry = { ev: name, seq: seq++ };
      if (cleaned) entry.d = cleaned;
      queue.push(entry);
      if (queue.length >= MaxBatchSize) {
        void flush({ keepalive: false });
      } else {
        scheduleFlush();
      }
      return true;
    }

    async function flush({ keepalive = false } = {}) {
      if (flushTimer) {
        clearTimeout(flushTimer);
        flushTimer = 0;
      }
      if (queue.length === 0 || (flushing && !keepalive)) return false;
      const batch = queue.splice(0, MaxBatchSize);
      const body = JSON.stringify({
        tab: tabId,
        page: pageName,
        events: batch,
      });
      flushing = true;
      try {
        const resp = await sendFetch(resolveUrl("api/telemetry"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body,
          keepalive: Boolean(keepalive),
        });
        if (resp && resp.status >= 400 && resp.status < 500 && resp.status !== 401 && resp.status !== 429) {
          if (root.console && typeof root.console.error === "function") {
            root.console.error(`Telemetry batch rejected with HTTP ${resp.status}`);
          }
        }
        return Boolean(resp && resp.ok);
      } catch {
        return false;
      } finally {
        flushing = false;
        if (queue.length > 0 && !keepalive) scheduleFlush();
      }
    }

    function startLifecycle() {
      if (!root.document || !root.addEventListener) return;
      track("page.open", {
        viewport: viewportBucket(root.innerWidth),
        w: Math.round(Number(root.innerWidth) || 0),
        h: Math.round(Number(root.innerHeight) || 0),
      });

      const emitHide = (reason) => {
        if (hiddenEmittedForSpan) return;
        hiddenEmittedForSpan = true;
        const visibleMs = Math.max(0, Date.now() - visibleSince);
        track("page.hide", { reason, visibleMs });
        void flush({ keepalive: true });
      };

      root.document.addEventListener("visibilitychange", () => {
        if (root.document.visibilityState === "hidden") {
          emitHide("hidden");
        } else {
          visibleSince = Date.now();
          hiddenEmittedForSpan = false;
        }
      });
      root.addEventListener("pagehide", () => emitHide("pagehide"));

      root.addEventListener("error", (event) => {
        const file = String(event?.filename || "").split("/").pop()?.split("?")[0] || "unknown";
        track("client.error", { kind: "error", src: file.slice(0, 60) });
      });
      root.addEventListener("unhandledrejection", () => {
        track("client.error", { kind: "rejection" });
      });
    }

    return Object.freeze({
      tabId,
      page: pageName,
      track,
      flush,
      startLifecycle,
      pendingCount: () => queue.length,
    });
  }

  let activeClient = null;

  function init(options) {
    activeClient = createClient(options);
    activeClient.startLifecycle();
    return activeClient;
  }

  function track(ev, data) {
    return activeClient ? activeClient.track(ev, data) : false;
  }

  function flush(options) {
    return activeClient ? activeClient.flush(options) : Promise.resolve(false);
  }

  return Object.freeze({
    init,
    track,
    flush,
    createClient,
    sanitizeData,
    viewportBucket,
    isForbiddenKey,
  });
});
