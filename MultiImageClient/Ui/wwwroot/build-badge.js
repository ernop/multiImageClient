"use strict";

// Renders the header #build-info badge with a browser-relative freshness label
// ("updated 7 minutes ago") computed from compile-time build metadata so
// idle-sleep restarts never fake an update.
(function publishBuildBadge(root, factory) {
  const api = factory(root);
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.MultiImageBuildBadge = api;
  }
})(typeof globalThis === "object" ? globalThis : this, function createBuildBadge(root) {
  const TickIntervalMs = 30_000;
  const SkewToleranceMs = 60_000;
  const MinuteMs = 60_000;
  const HourMs = 60 * MinuteMs;
  const DayMs = 24 * HourMs;
  const RelativeMaxDays = 30;

  function parseTimestampMs(iso) {
    if (typeof iso !== "string" || !iso.trim()) return Number.NaN;
    return Date.parse(iso);
  }

  // Prefer compile-time builtAtUtc so local rebuilds and production deploys
  // reflect the exact running build without resetting on idle-sleep wakeups.
  function resolveUpdatedTimestampMs(build) {
    if (!build || typeof build !== "object") return Number.NaN;
    const built = parseTimestampMs(build.builtAtUtc);
    if (Number.isFinite(built)) return built;
    const committed = parseTimestampMs(build.commitDate);
    if (Number.isFinite(committed)) return committed;
    return Number.NaN;
  }

  function formatLocalStamp(ms, locale) {
    if (!Number.isFinite(ms)) return "";
    try {
      return new Intl.DateTimeFormat(locale, {
        year: "numeric",
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit",
      }).format(new Date(ms));
    } catch {
      return new Date(ms).toISOString();
    }
  }

  function formatRelativeUpdate(timestampMs, nowMs = Date.now(), locale = undefined) {
    if (!Number.isFinite(timestampMs) || !Number.isFinite(nowMs)) return "";
    const diffMs = nowMs - timestampMs;
    if (diffMs < -SkewToleranceMs) {
      return `updated ${formatLocalStamp(timestampMs, locale)}`;
    }
    if (diffMs < MinuteMs) return "updated just now";
    if (diffMs < HourMs) {
      const mins = Math.floor(diffMs / MinuteMs);
      return mins === 1 ? "updated 1 minute ago" : `updated ${mins} minutes ago`;
    }
    if (diffMs < DayMs) {
      const hours = Math.floor(diffMs / HourMs);
      return hours === 1 ? "updated 1 hour ago" : `updated ${hours} hours ago`;
    }
    const days = Math.floor(diffMs / DayMs);
    if (days < RelativeMaxDays) {
      return days === 1 ? "updated 1 day ago" : `updated ${days} days ago`;
    }
    try {
      const dateStr = new Intl.DateTimeFormat(locale, {
        year: "numeric",
        month: "short",
        day: "numeric",
      }).format(new Date(timestampMs));
      return `updated ${dateStr}`;
    } catch {
      return `updated ${new Date(timestampMs).toISOString().slice(0, 10)}`;
    }
  }

  function formatTooltip(build, locale = undefined) {
    if (!build || !build.commit) return "";
    const parts = [`Build ${build.commit}`];
    const builtMs = parseTimestampMs(build.builtAtUtc);
    if (Number.isFinite(builtMs)) {
      parts.push(`updated ${formatLocalStamp(builtMs, locale)}`);
    }
    const commitMs = parseTimestampMs(build.commitDate);
    if (Number.isFinite(commitMs)) {
      parts.push(`committed ${formatLocalStamp(commitMs, locale)}`);
    }
    return parts.join(" · ") + (build.commitUrl ? " — click commit hash to open on GitHub" : "");
  }

  const tracked = [];
  let timerId = 0;
  let listenersWired = false;

  function shouldTick() {
    const doc = root.document;
    if (!doc || doc.visibilityState === "hidden") return false;
    const activity = root.MultiImageTabActivity;
    if (activity && typeof activity.shouldPoll === "function" && !activity.shouldPoll()) {
      return false;
    }
    return true;
  }

  function refreshAll() {
    const now = Date.now();
    for (let i = tracked.length - 1; i >= 0; i--) {
      const item = tracked[i];
      if (!item.node.isConnected) {
        tracked.splice(i, 1);
        continue;
      }
      if (item.ageEl && Number.isFinite(item.updatedMs)) {
        item.ageEl.textContent = formatRelativeUpdate(item.updatedMs, now);
      }
    }
    if (tracked.length === 0 && timerId) {
      clearInterval(timerId);
      timerId = 0;
    }
  }

  function ensureTicker() {
    if (!root.document) return;
    if (!listenersWired) {
      listenersWired = true;
      root.document.addEventListener("visibilitychange", () => {
        if (root.document.visibilityState === "visible") refreshAll();
      });
      const activity = root.MultiImageTabActivity;
      if (activity && typeof activity.onResume === "function") {
        activity.onResume(() => refreshAll());
      }
    }
    if (!timerId && tracked.length > 0) {
      timerId = setInterval(() => {
        if (shouldTick()) refreshAll();
      }, TickIntervalMs);
    }
  }

  function render(node, build) {
    if (!node || !build || !build.commit) return;
    const shortCommit = String(build.commit);
    const label = `build ${shortCommit}`;
    node.textContent = "";
    if (build.commitUrl) {
      const a = root.document.createElement("a");
      a.href = build.commitUrl;
      a.target = "_blank";
      a.rel = "noopener";
      a.textContent = label;
      node.appendChild(a);
    } else {
      node.appendChild(root.document.createTextNode(label));
    }

    const updatedMs = resolveUpdatedTimestampMs(build);
    let ageEl = null;
    if (Number.isFinite(updatedMs)) {
      const sep = root.document.createElement("span");
      sep.className = "build-sep";
      sep.textContent = " · ";
      ageEl = root.document.createElement("span");
      ageEl.className = "build-updated";
      ageEl.textContent = formatRelativeUpdate(updatedMs, Date.now());
      node.append(sep, ageEl);
    }
    node.title = formatTooltip(build);

    const existing = tracked.find((entry) => entry.node === node);
    if (existing) {
      existing.updatedMs = updatedMs;
      existing.ageEl = ageEl;
    } else {
      tracked.push({ node, updatedMs, ageEl });
    }
    ensureTicker();
  }

  return Object.freeze({
    resolveUpdatedTimestampMs,
    formatRelativeUpdate,
    formatTooltip,
    render,
    refreshAll,
  });
});
