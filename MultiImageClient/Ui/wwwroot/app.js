"use strict";

// ---------- reverse-proxy-safe URLs ----------

// The shared-site deployment serves the app behind a secret nginx path
// prefix, so nothing may reference the origin root. Every API URL — including
// server-generated ones persisted inside events as "/api/..." — resolves
// through this helper against the page's own directory. Absolute http(s)
// URLs (B2-hosted result images) pass through untouched: prefixing them
// would corrupt them.
const appBase = location.pathname.replace(/[^/]*$/, "");
const apiUrl = (path) => {
  const value = String(path);
  if (/^https?:\/\//i.test(value)) return value;
  return appBase + value.replace(/^\//, "");
};

// ---------- state ----------

// Ordered composer attachments. gpt-image-2 /edits receives every entry;
// every other generator receives only index 0. Cap comes from /api/config.
let inputImageItems = [];    // [{ file: Blob, url: objectURL }]
let maxInputImages = 4;
let generators = [];         // from /api/config
let videoSource = null;       // { jobId, generator, index, url }
let videoGeneration = { available: false, availabilityProblem: "video configuration not loaded" };
let spellfix = { available: false, availabilityProblem: "configuration not loaded" };
let spellfixPrevious = null;  // prompt text as it was before the last fix, for undo
// gpt-image-2 anti-murk guidance defaults from /api/config; the live control
// state lives in the DOM and persists per-browser in localStorage.
let gpt2Guidance = { defaultEnabled: true, defaultText: "" };
// Standard instruction the server substitutes for blank describe-only jobs;
// the composer applies the same text at submit so the card shows exactly
// what was recorded.
let describeConfig = { defaultInstruction: "" };
const Gpt2GuidanceEnabledKey = "gpt2GuidanceEnabled";
// V2: the default guidance text was replaced on 2026-07-31 (user request);
// the key bump retires stored copies of the old default so every browser
// re-prefills from the current server default.
const Gpt2GuidanceTextKey = "gpt2GuidanceTextV2";
// Shared-site identity + filters. The creator name is browser-local state
// (localStorage) sent with every job; auth (when the server gate is on)
// seeds it with the login name. Filter selection persists per browser.
let authInfo = { enabled: false, user: "" };
const UsernameKey = "mic_username";
const UserFilterKey = "mic_user_filter_v1";
let knownUsers = new Map();  // user -> job count (live-accumulated)
let selectedUserFilter = new Set();  // empty = show everyone
try {
  for (const u of JSON.parse(localStorage.getItem(UserFilterKey) || "[]")) {
    selectedUserFilter.add(String(u));
  }
} catch { /* corrupted filter state just resets to everyone */ }
// Server-persistent shared favorites. Images are keyed by exact
// jobId|generator|imageIndex; whole prompts are keyed by their originating
// jobId. favoriteBrowseUser is null for the normal feed, "*" for everyone's
// favorites, or one exact display username.
let favoriteItems = new Map();
let promptFavoriteItems = new Map();
let favoriteUsers = new Map();
let favoriteBrowseUser = null;
let favoritesRefreshInFlight = false;
let favoritesLastRefreshAt = 0;
let favoritesSnapshotSignature = "";
let favoritesServerVersion = "";
let favoritesGalleryRenderPending = false;
let favoriteMutation = null;
let favoriteMutationError = null;
// Server-persistent, global stream visibility. A hidden prompt removes its
// whole job; a hidden image removes only that exact generated result.
let visibilityServerVersion = "";
let hiddenPromptJobIds = new Set();
let hiddenImageKeys = new Set();
let visibilityMutation = null;
let imageViewerState = null;  // stable { jobId, generator, imageIndex } identity
let imageViewerRenderVersion = 0;
let imageViewerActivationVersion = 0;
let imageViewerActivationController = null;
let imageViewerHelpOpen = false;
// Global "always compare with the input image" toggle (`c` in the viewer),
// sticky across images and page loads. Jobs without an input image show the
// normal single-image view even while the mode is on.
let imageViewerCompareInput = localStorage.getItem("imageViewerCompareInput") === "true";
// Close handback ("inner movement implies outer movement"): when ON, closing
// the viewer scrolls the page to the image that was on screen and focuses its
// thumbnail. OFF by default — the page stays exactly where it was. Toggled in
// the settings panel or instantly with `s` inside the viewer; per-browser.
let imageViewerReturnSync = localStorage.getItem("imageViewerReturnSync") === "true";
// Per-browser record of every image ever displayed full-size in the viewer;
// marks card thumbnails with a subtle accent edge. Unofficial bookkeeping
// only — capped, oldest entries retire first.
const ViewerSeenKey = "mic_viewer_seen_v1";
const ViewerSeenCap = 5000;
const viewerSeenSet = loadViewerSeen();
let imageViewerContentAr = null; // current output image's aspect ratio, for window shrink-wrap
let imageViewerFocusBeforeOpen = null;
let imageViewerWheelAccumulator = 0;
let imageViewerWheelResetTimer = null;
let imageViewerPreloadActive = 0;
// Waiters are { priority, resolve, reject, entry }; lower priority runs first.
// Re-sorted on every insert / bump so a direction change can promote the new
// ahead-of-travel neighbors over stale far-behind fetches still queued.
const imageViewerPreloadWaiters = [];
const imageViewerCache = new Map();
// ±10 is enough to scrub Left/Right without hitching; travel direction only
// reorders priority inside the window (ahead first), not its shape.
const ImageViewerPreloadAhead = 10;
const ImageViewerPreloadBehind = 10;
const ImageViewerPreloadConcurrency = 6;
const ImageViewerPageJumpSize = 5;
const ImageViewerWheelThreshold = 80;
// Last keyboard/wheel step: -1 = toward newer/previous, +1 = toward
// older/next, 0 = open / absolute jump with no travel bias.
let imageViewerNavDelta = 0;

const el = (id) => document.getElementById(id);
const startsAtDefaultView = window.location.search === "" && window.location.hash === "";
if (startsAtDefaultView) {
  history.scrollRestoration = "manual";
  window.scrollTo(0, 0);
  window.addEventListener("pageshow", () => window.scrollTo(0, 0), { once: true });
}
const pasteZone = el("paste-zone");
const pasteHint = el("paste-hint");
const clearBtn = el("clear-image");
const fileInput = el("file-input");
const promptBox = el("prompt");
const gensRow = el("gens-row");
const describeSection = el("describe-section");
const describeRow = el("describe-row");
const sendBtn = el("send");
const sendError = el("send-error");
const jobsSection = el("jobs");
const videoDialog = el("video-dialog");
const logsToggle = el("logs-toggle");
const logsPanel = el("logs-panel");
const logsLines = el("logs-lines");
const logsConnection = el("logs-connection");
const imageViewer = el("image-viewer");
const imageViewerWindow = el("image-viewer-window");
const imageViewerImage = el("image-viewer-image");
const imageViewerStage = el("image-viewer-stage");
const imageViewerInputImage = el("image-viewer-input-image");
const imageViewerInputLabel = el("image-viewer-input-label");
const imageViewerOutputLabel = el("image-viewer-output-label");
const imageViewerHelp = el("image-viewer-help");
const imageViewerHelpList = el("image-viewer-help-list");
const imageViewerPrompt = el("image-viewer-prompt");
const imageViewerActiveActions = el("image-viewer-active-actions");
const imageViewerSetImage = el("image-viewer-set-image");
const imageViewerSetImagePrompt = el("image-viewer-set-image-prompt");
const imageViewerDescribe = el("image-viewer-describe");
const imageViewerGuidance = el("image-viewer-guidance");
const imageViewerGenerator = el("image-viewer-generator");
const imageViewerDimensions = el("image-viewer-dimensions");
const imageViewerPosition = el("image-viewer-position");
const imageViewerFavorite = el("image-viewer-favorite");
const imageViewerVideo = el("image-viewer-video");
const imageViewerHide = el("image-viewer-hide");
const imageViewerStatus = el("image-viewer-status");
const favoritesGallery = el("favorites-gallery");
const favoritesGrid = el("favorites-grid");
let lastLogSequence = 0;

// ---------- live process log ----------

function parseLogLine(line) {
  const timestampMatch = String(line).match(/^(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2}:\d{2}\.\d{3})\s*([\s\S]*)$/);
  let time = "";
  let message = String(line);
  if (timestampMatch) {
    time = timestampMatch[2];
    message = timestampMatch[3];
  }

  let source = "system";
  let event = "·";
  const scopeMatch = message.match(/^\s*\[([^\]]+)\]\s*([\s\S]*)$/);
  if (scopeMatch) {
    source = scopeMatch[1];
    message = scopeMatch[2];
  }

  const arrowMatch = message.match(/^\s*(->|<-)\s*([\s\S]*)$/);
  if (arrowMatch) {
    event = arrowMatch[1] === "->" ? "→" : "←";
    message = arrowMatch[2];
  }

  if (source === "system") {
    const knownSources = [
      [/^Grok web/i, "grok-web"],
      [/^Grok Web/i, "grok-web"],
      [/^xAI Grok/i, "grok-api"],
      [/^\[?gpt-image/i, "gpt-image"],
      [/^From Recraft/i, "recraft"],
      [/^Downloading image/i, "download"],
      [/^Combined image/i, "grid"],
      [/^UI /i, "ui"],
    ];
    const known = knownSources.find(([pattern]) => pattern.test(message));
    if (known) source = known[1];
  }

  let tone = "";
  if (/\b(FAIL|ERROR|failed|exception|timed out|rejected|canceled)\b/i.test(message)) {
    tone = "error";
    if (event === "·") event = "!";
  } else if (/\b(OK|DONE|completed|saved)\b/i.test(message)) {
    tone = "success";
  } else if (event === "→" || /\bSTART\b/.test(message)) {
    tone = "start";
  }

  return { time, source, event, message, tone };
}

function appendLogLine(entry) {
  if (!(entry.sequence > lastLogSequence)) return;
  lastLogSequence = entry.sequence;

  const stayAtBottom = logsLines.scrollHeight - logsLines.scrollTop - logsLines.clientHeight < 48;
  const parsed = parseLogLine(entry.line);
  const row = document.createElement("div");
  row.className = `log-row${parsed.tone ? ` ${parsed.tone}` : ""}`;
  const jobMatch = String(entry.line).match(/\[ui #([A-Za-z0-9]+)\]/);
  if (jobMatch) row.dataset.jobId = jobMatch[1];
  for (const [className, value] of [
    ["log-time", parsed.time],
    ["log-source", parsed.source],
    ["log-event", parsed.event],
    ["log-message", parsed.message],
  ]) {
    const field = document.createElement("span");
    field.className = className;
    field.textContent = value;
    row.appendChild(field);
  }
  logsLines.appendChild(row);

  while (logsLines.childElementCount > 2000) {
    logsLines.firstElementChild.remove();
  }
  if (stayAtBottom) logsLines.scrollTop = logsLines.scrollHeight;
}

// Log lines arrive by short polling, not SSE: a persistent stream per window
// counts against the browser's ~6-connection HTTP/1.1 pool (shared across
// ALL tabs on plain-HTTP localhost), which must stay free for image loads.
let logsPollTimer = null;
let logsPollInFlight = false;

async function pollLogs() {
  if (logsPanel.hidden || logsPollInFlight) return;
  logsPollInFlight = true;
  if (logsPollTimer) {
    clearTimeout(logsPollTimer);
    logsPollTimer = null;
  }
  try {
    const resp = await fetch(apiUrl(`api/logs/poll?after=${lastLogSequence}`));
    if (resp.status === 401) { location.reload(); return; }
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    const body = await resp.json();
    for (const entry of body.entries) appendLogLine(entry);
    if (body.next > lastLogSequence) lastLogSequence = body.next;
    logsConnection.textContent = "live";
    logsConnection.className = "live";
  } catch {
    logsConnection.textContent = "server disconnected — retrying";
    logsConnection.className = "error";
  } finally {
    logsPollInFlight = false;
    if (!logsPanel.hidden) {
      logsPollTimer = setTimeout(pollLogs, 1000);
    }
  }
}

function setLogsOpen(open) {
  logsPanel.hidden = !open;
  logsToggle.setAttribute("aria-expanded", String(open));
  logsToggle.classList.toggle("open", open);
  document.body.classList.toggle("logs-open", open);
  if (open) {
    pollLogs();
    requestAnimationFrame(() => {
      logsLines.scrollTop = logsLines.scrollHeight;
    });
  } else if (logsPollTimer) {
    clearTimeout(logsPollTimer);
    logsPollTimer = null;
  }
}

logsToggle.addEventListener("click", () => setLogsOpen(logsPanel.hidden));
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && !logsPanel.hidden) setLogsOpen(false);
});

// ---------- config / generator toggles ----------

async function loadConfig() {
  const resp = await fetch(apiUrl("api/config"));
  if (resp.status === 401) { location.reload(); return; }
  if (!resp.ok) {
    // A 502 from the reverse proxy (server not running) has an empty body, so
    // resp.json() would throw the opaque "unexpected end of data" -- report the
    // real cause instead.
    throw new Error(`/api/config returned HTTP ${resp.status} — is the MultiImageClient server running on :5960?`);
  }
  const cfg = await resp.json();
  generators = cfg.generators;
  videoGeneration = cfg.videoGeneration || videoGeneration;
  spellfix = cfg.spellfix || spellfix;
  applySpellfixAvailability();
  gpt2Guidance = cfg.gpt2Guidance || gpt2Guidance;
  describeConfig = cfg.describe || describeConfig;
  if (Number.isInteger(cfg.maxInputImages) && cfg.maxInputImages >= 1) {
    maxInputImages = cfg.maxInputImages;
  }
  authInfo = cfg.auth || authInfo;
  applyAuthState();
  initGpt2Guidance();

  // Exact code identity of the running server, embedded at build time.
  // When the build carries a real hash it links to the commit in the public
  // repo (the page-wide no-referrer meta keeps the secret path out of the
  // outbound request).
  const buildNode = el("build-info");
  if (buildNode && cfg.build && cfg.build.commit) {
    const label = `build ${cfg.build.commit}`;
    buildNode.textContent = "";
    if (cfg.build.commitUrl) {
      const a = document.createElement("a");
      a.href = cfg.build.commitUrl;
      a.target = "_blank";
      a.rel = "noopener";
      a.textContent = label;
      buildNode.appendChild(a);
    } else {
      buildNode.textContent = label;
    }
    buildNode.title = (cfg.build.commitDate
      ? `Commit the running server was built from: ${cfg.build.commit}, committed ${cfg.build.commitDate}`
      : `Commit the running server was built from: ${cfg.build.commit}`)
      + (cfg.build.commitUrl ? " — click to open the commit on GitHub" : "");
  }

  const fillSelect = (selectEl, entries) => {
    selectEl.innerHTML = "";
    for (const e of entries) {
      const opt = document.createElement("option");
      opt.value = e.key;
      opt.textContent = e.displayLabel || e.label;
      opt.dataset.defaultLabel = e.displayLabel || e.label;
      if (e.inputLabel) opt.dataset.inputLabel = e.inputLabel;
      selectEl.appendChild(opt);
    }
  };
  // The AR picker puts the descriptive word at the far left and the numeric
  // ratio at the far right of a two-column layout: the select renders in a
  // monospace font, so NBSP padding between text and ratio forms a wide
  // gutter with the ratio column right-aligned. auto has no ratio and stays
  // a plain label (it also swaps to "match input image" with an input).
  const ratioShapes = cfg.shapes.filter((s) => s.ratio);
  const shapeTextWidth = Math.max(0, ...ratioShapes.map((s) => s.text.length));
  const shapeRatioWidth = Math.max(0, ...ratioShapes.map((s) => s.ratio.length));
  const shapeGutter = 5;
  fillSelect(el("opt-shape"), cfg.shapes.map((s) => s.ratio
    ? {
        ...s,
        displayLabel: s.text
          + "\u00a0".repeat(shapeTextWidth - s.text.length + shapeGutter + shapeRatioWidth - s.ratio.length)
          + s.ratio,
      }
    : s));
  fillSelect(el("opt-detail"), cfg.details);

  el("opt-shape").value = cfg.defaults.shape;
  el("opt-detail").value = cfg.defaults.detail;
  el("opt-quality").value = cfg.defaults.quality;
  el("opt-moderation").value = cfg.defaults.moderation;
  el("opt-n").value = cfg.defaults.n;
  updateShapeOptionLabel();

  const buildGenChip = (g) => {
    const label = document.createElement("label");
    label.className = "gen-toggle" + (g.available ? "" : " unavailable");
    label.title = g.available
      ? g.detail
      : `${g.detail} — NOT AVAILABLE: ${g.availabilityProblem || "missing configuration"}`;

    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.value = g.key;
    cb.dataset.available = String(g.available);
    cb.dataset.imageCapable = String(!!g.imageCapable);
    cb.dataset.imageAspectOverride = String(!!g.imageAspectOverride);
    cb.dataset.kind = g.kind || "image";
    cb.dataset.requiresImage = String(!!g.requiresImage);
    cb.disabled = !g.available;
    cb.checked = g.available && g.defaultOn;
    cb.addEventListener("change", () => {
      label.classList.toggle("checked", cb.checked);
      updateGeneratorCount();
    });

    label.appendChild(cb);
    label.appendChild(document.createTextNode(g.label));
    // Image-capability flag on every chip: capable targets always show a tiny
    // picture icon; text-only targets show a slashed one, but only while an
    // image is attached (CSS keys off #gens-row.has-image) — that's exactly
    // when "your attachment will NOT be sent here" matters.
    const imgFlag = document.createElement("span");
    imgFlag.className = "gen-img-flag " + (g.imageCapable ? "capable" : "text-only");
    imgFlag.innerHTML =
      '<svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true">' +
      '<rect x="1" y="2.5" width="14" height="11" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.5"/>' +
      '<circle cx="5.2" cy="6.4" r="1.3" fill="currentColor"/>' +
      '<path d="M3 12l3.2-3.6 2.4 2.7 1.9-2.2 2.5 3.1z" fill="currentColor"/>' +
      (g.imageCapable ? "" : '<line x1="0.5" y1="15.5" x2="15.5" y2="0.5" stroke="currentColor" stroke-width="1.7"/>') +
      "</svg>";
    label.appendChild(imgFlag);
    label.classList.toggle("checked", cb.checked);
    return label;
  };

  // Media generators and describe endpoints render as separate sections; the
  // describe chips only become selectable while an image is attached
  // (updateGeneratorCompatibility enforces it, matching the server's rule).
  gensRow.innerHTML = "";
  describeRow.innerHTML = "";
  for (const g of generators) {
    (g.kind === "describe" ? describeRow : gensRow).appendChild(buildGenChip(g));
  }
  // Visibility (needs an attached image + at least one describe target) is
  // owned by updateGeneratorCompatibility, called next.
  updateGeneratorCompatibility();
}

function hasInputImages() {
  return inputImageItems.length > 0;
}

function updateGeneratorCompatibility() {
  // imageCapable comes from /api/config so the server stays the single
  // source of truth for which targets accept an input image. Text-only
  // targets stay selectable on image jobs — the server runs them from the
  // prompt alone (user-specified behavior) — so the only hard disable left
  // is the AR-override gap on targets that actually consume the image
  // (Recraft image-to-image can't override output AR). Multi-image jobs do
  // not lock generator chips: non-gpt2 targets simply receive image 0.
  const hasImage = hasInputImages();
  gensRow.classList.toggle("has-image", hasImage);
  describeRow.classList.toggle("has-image", hasImage);
  // Without an attached image there is nothing to describe, so the whole
  // describe section disappears rather than showing a row of disabled chips.
  // (Any checked describe chips are unchecked below via requiresImage.)
  describeSection.hidden = !hasImage || describeRow.children.length === 0;
  for (const btn of document.querySelectorAll("#gen-controls .image-only-action")) {
    btn.hidden = !hasImage;
  }
  for (const cb of allGeneratorInputs()) {
    const providerAvailable = cb.dataset.available === "true";
    const imageCapable = cb.dataset.imageCapable === "true";
    const isDescribe = cb.dataset.kind === "describe";
    // Describe endpoints consume the image itself and ignore output-AR
    // options entirely, so the AR-override disable never applies to them.
    const aspectIncompatible =
      hasImage &&
      imageCapable &&
      !isDescribe &&
      el("opt-shape").value !== "auto" &&
      cb.dataset.imageAspectOverride !== "true";
    // requiresImage (describe endpoints): without an attached image there is
    // nothing to describe — mirror the server's hard rejection as a disable.
    const missingRequiredImage = cb.dataset.requiresImage === "true" && !hasImage;
    cb.disabled = !providerAvailable || aspectIncompatible || missingRequiredImage;
    if (aspectIncompatible || missingRequiredImage)
    {
      cb.checked = false;
    }
    const label = cb.closest(".gen-toggle");
    label.classList.toggle("unavailable", cb.disabled);
    label.classList.toggle("checked", cb.checked);
    if (missingRequiredImage)
    {
      label.title = `${genLabel(cb.value)} describes an attached image — attach one to enable it`;
    }
    else if (aspectIncompatible)
    {
      label.title = `${genLabel(cb.value)} cannot override output AR with an input image; choose match input image to use it`;
    }
    else if (hasImage && !imageCapable)
    {
      label.title = `${genLabel(cb.value)} doesn't accept input images — it will run from the prompt text only; the attached image is NOT sent to it`;
    }
    else if (hasImage && inputImageItems.length > 1 && cb.value !== "gpt2" && imageCapable && !isDescribe)
    {
      label.title = `${genLabel(cb.value)} will receive only the first of ${inputImageItems.length} attached images (gpt-image-2 receives all)`;
    }
    else if (hasImage && inputImageItems.length > 1 && isDescribe)
    {
      label.title = `${genLabel(cb.value)} will describe each of the ${inputImageItems.length} attached images separately`;
    }
    else
    {
      const g = generators.find((entry) => entry.key === cb.value);
      label.title = g?.available
        ? g.detail
        : `${g?.detail || cb.value} — NOT AVAILABLE: ${g?.availabilityProblem || "missing configuration"}`;
    }
  }
  updateGeneratorCount();
}

function allGeneratorInputs() {
  return [...gensRow.querySelectorAll("input"), ...describeRow.querySelectorAll("input")];
}

function updateShapeOptionLabel() {
  const shapeSelect = el("opt-shape");
  const autoOption = shapeSelect.querySelector('option[value="auto"]');
  if (!autoOption) return;
  autoOption.textContent = hasInputImages() && autoOption.dataset.inputLabel
    ? autoOption.dataset.inputLabel
    : autoOption.dataset.defaultLabel;
  shapeSelect.title = hasInputImages()
    ? "Default: match the first attached image's aspect ratio using each model's closest supported output geometry. Choose another option to override it."
    : "Default: let each model choose its output aspect ratio.";
}

function updateGeneratorCount() {
  // The visible "N of M enabled" counter was removed (2026-07-31), but every
  // generator-selection change still funnels through here, so this remains
  // the recompute point for the prompt-length notice.
  updatePromptLimitNotice();
}

// ---------- prompt length limits (non-blocking) ----------

// Some targets hard-cap prompt length (grok-web's imagine WebSocket rejects
// anything over 8192 chars). /api/config carries maxPromptChars per
// generator; an over-limit prompt still submits — the server truncates it at
// the send-to-provider stage — this notice just says so before the fact.
const promptLimitNotice = el("prompt-limit-notice");

function updatePromptLimitNotice() {
  const length = promptBox.value.trim().length;
  const affected = [...gensRow.querySelectorAll("input:checked")]
    .map((cb) => generators.find((g) => g.key === cb.value))
    .filter((g) => g && g.maxPromptChars && length > g.maxPromptChars);
  if (affected.length === 0) {
    promptLimitNotice.hidden = true;
    promptLimitNotice.textContent = "";
    return;
  }
  const parts = affected.map((g) => `${g.label} (max ${g.maxPromptChars.toLocaleString()})`);
  promptLimitNotice.textContent =
    `prompt is ${length.toLocaleString()} chars — over the limit for ${parts.join(", ")}. ` +
    `You can still generate; the prompt will be sent truncated to ` +
    `${affected.length === 1 ? "that target" : "those targets"} (other targets get the full text).`;
  promptLimitNotice.hidden = false;
}

promptBox.addEventListener("input", updatePromptLimitNotice);

// ---------- gpt-image-2 anti-murk guidance (lives in the settings panel) ----------

// gpt-image-2 habitually drifts into dark, murky, underexposed output, so a
// default-on toggle appends corrective guidance (editable below it) to every
// prompt sent to the gpt2 target — and only that target; the server does the
// appending and records it as a prompt-transformation step. State persists
// in this browser's localStorage only; defaults come from /api/config.
const gpt2GuidanceEnabledBox = el("gpt2-guidance-enabled");
const gpt2GuidanceTextBox = el("gpt2-guidance-text");

function applyGpt2GuidanceEnabledState() {
  gpt2GuidanceTextBox.disabled = !gpt2GuidanceEnabledBox.checked;
}

function initGpt2Guidance() {
  const storedEnabled = localStorage.getItem(Gpt2GuidanceEnabledKey);
  gpt2GuidanceEnabledBox.checked = storedEnabled === null
    ? gpt2Guidance.defaultEnabled
    : storedEnabled === "true";
  // A stored EMPTY text is treated as unset and re-prefilled: the checkbox is
  // the only off-switch. A browser that persisted an emptied textbox silently
  // stripped the guidance from every gpt2 call 2026-07-31 → 08-02 (ultra-dark
  // output) while the toggle still said on.
  const storedText = localStorage.getItem(Gpt2GuidanceTextKey);
  gpt2GuidanceTextBox.value = storedText === null || storedText.trim() === ""
    ? gpt2Guidance.defaultText
    : storedText;
  applyGpt2GuidanceEnabledState();
}

gpt2GuidanceEnabledBox.addEventListener("change", () => {
  localStorage.setItem(Gpt2GuidanceEnabledKey, String(gpt2GuidanceEnabledBox.checked));
  applyGpt2GuidanceEnabledState();
});
gpt2GuidanceTextBox.addEventListener("input", () => {
  // Never persist blank guidance text (see initGpt2Guidance).
  if (gpt2GuidanceTextBox.value.trim() === "") {
    localStorage.removeItem(Gpt2GuidanceTextKey);
  } else {
    localStorage.setItem(Gpt2GuidanceTextKey, gpt2GuidanceTextBox.value);
  }
});
el("gpt2-guidance-reset").addEventListener("click", () => {
  gpt2GuidanceTextBox.value = gpt2Guidance.defaultText;
  localStorage.removeItem(Gpt2GuidanceTextKey);
});

// ---------- settings & hide night mode ----------

// UI preferences live client-side in one localStorage JSON object; the server
// never sees them. "Hide night mode" hides entire job cards (prompt + result
// images) whose prompt matches a user-editable wordlist. The wordlist itself
// is only revealed by an explicit click inside the settings panel, and is
// blanked again whenever the panel closes.
const UiSettingsKey = "mic_ui_settings_v1";

function loadUiSettings() {
  try {
    const saved = JSON.parse(localStorage.getItem(UiSettingsKey) || "{}");
    return {
      nightHideEnabled: saved.nightHideEnabled === true,
      nightWords: typeof saved.nightWords === "string" ? saved.nightWords : "",
      // Costs off by default so casual/shared-site visitors never see $ estimates
      // unless they opt in. Explicit true/false both stick; missing key = off.
      showCosts: saved.showCosts === true,
    };
  } catch {
    return { nightHideEnabled: false, nightWords: "", showCosts: false };
  }
}

const uiSettings = loadUiSettings();

function saveUiSettings() {
  localStorage.setItem(UiSettingsKey, JSON.stringify(uiSettings));
}

const settingsToggle = el("settings-toggle");
const settingsPanel = el("settings-panel");
const nightToggle = el("night-toggle");
const nightHideEnabledBox = el("night-hide-enabled");
const showCostsBox = el("show-costs");
const nightWordsEditor = el("night-words-editor");
const nightWordsBox = el("night-words");

let nightMatchers = null; // compiled lazily, invalidated when the list changes

function getNightMatchers() {
  if (nightMatchers) return nightMatchers;
  nightMatchers = [];
  for (const raw of uiSettings.nightWords.split("\n")) {
    const term = raw.trim();
    if (!term) continue;
    const escaped = term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    // A single word matches at a word start so "cat" also hides "cats"
    // (better to over-hide than leak); multi-word phrases match anywhere.
    nightMatchers.push(/^[\w']+$/.test(term)
      ? new RegExp(`\\b${escaped}`, "i")
      : new RegExp(escaped, "i"));
  }
  return nightMatchers;
}

function promptIsNightHidden(prompt) {
  if (!uiSettings.nightHideEnabled || !prompt) return false;
  return getNightMatchers().some((re) => re.test(prompt));
}

function applyNightModeToCard(card) {
  const prompt = card.querySelector(".job-prompt").textContent;
  card.classList.toggle("night-hidden", promptIsNightHidden(prompt));
}

function applyNightMode() {
  nightToggle.classList.toggle("on", uiSettings.nightHideEnabled);
  nightToggle.setAttribute("aria-pressed", String(uiSettings.nightHideEnabled));
  nightHideEnabledBox.checked = uiSettings.nightHideEnabled;
  for (const card of document.querySelectorAll("#jobs .job, #archive .job")) applyNightModeToCard(card);
  // If the viewer is on an image from a now-hidden job, re-rendering makes it
  // report "no longer available" instead of displaying hidden content.
  if (!imageViewer.hidden) renderImageViewer();
}

function setNightHideEnabled(enabled) {
  uiSettings.nightHideEnabled = enabled;
  saveUiSettings();
  applyNightMode();
}

nightToggle.addEventListener("click", () => setNightHideEnabled(!uiSettings.nightHideEnabled));
nightHideEnabledBox.addEventListener("change", () => setNightHideEnabled(nightHideEnabledBox.checked));

function applyShowCosts() {
  showCostsBox.checked = uiSettings.showCosts;
  document.body.classList.toggle("show-costs", uiSettings.showCosts);
}

function setShowCosts(enabled) {
  uiSettings.showCosts = enabled;
  saveUiSettings();
  applyShowCosts();
  // User toggled after page init — restore or clear the session bar now.
  if (enabled) renderCostSummary();
  else el("cost-summary").hidden = true;
}

showCostsBox.addEventListener("change", () => setShowCosts(showCostsBox.checked));

// Image-viewer close handback (see imageViewerReturnSync above). The settings
// checkbox and the viewer's `s` shortcut drive the same persisted state.
const viewerReturnSyncBox = el("viewer-return-sync");

function setViewerReturnSync(enabled) {
  imageViewerReturnSync = enabled;
  localStorage.setItem("imageViewerReturnSync", String(enabled));
  viewerReturnSyncBox.checked = enabled;
}

viewerReturnSyncBox.addEventListener("change", () => setViewerReturnSync(viewerReturnSyncBox.checked));
viewerReturnSyncBox.checked = imageViewerReturnSync;

nightWordsBox.addEventListener("input", () => {
  uiSettings.nightWords = nightWordsBox.value;
  nightMatchers = null;
  saveUiSettings();
  applyNightMode();
});

function setSettingsOpen(open) {
  settingsPanel.hidden = !open;
  settingsToggle.setAttribute("aria-expanded", String(open));
  settingsToggle.classList.toggle("open", open);
  if (!open) {
    // Reopening always requires the explicit "modify list" click again, and
    // the closed panel keeps no readable copy of the list in the DOM.
    nightWordsEditor.hidden = true;
    nightWordsBox.value = "";
  }
}

settingsToggle.addEventListener("click", () => setSettingsOpen(settingsPanel.hidden));
el("settings-close").addEventListener("click", () => setSettingsOpen(false));
el("night-words-toggle").addEventListener("click", () => {
  nightWordsEditor.hidden = !nightWordsEditor.hidden;
  if (!nightWordsEditor.hidden) {
    nightWordsBox.value = uiSettings.nightWords;
    nightWordsBox.focus();
  } else {
    nightWordsBox.value = "";
  }
});
document.addEventListener("pointerdown", (event) => {
  if (!settingsPanel.hidden &&
      !settingsPanel.contains(event.target) &&
      !settingsToggle.contains(event.target)) {
    setSettingsOpen(false);
  }
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && !settingsPanel.hidden) setSettingsOpen(false);
});

applyNightMode();
applyShowCosts();

function setAllGenerators(mode) {
  for (const cb of gensRow.querySelectorAll("input:not(:disabled)")) {
    cb.checked = mode === "enable" ? true : mode === "disable" ? false : !cb.checked;
    cb.closest(".gen-toggle").classList.toggle("checked", cb.checked);
  }
  updateGeneratorCount();
}

el("gens-enable-all").addEventListener("click", () => setAllGenerators("enable"));
el("gens-disable-all").addEventListener("click", () => setAllGenerators("disable"));
el("gens-toggle-all").addEventListener("click", () => setAllGenerators("toggle"));
// Attachment-aware bulk actions, visible only while an image is attached.
function setGeneratorsByImageCapability(wantCapable, checked) {
  for (const cb of gensRow.querySelectorAll("input:not(:disabled)")) {
    if ((cb.dataset.imageCapable === "true") !== wantCapable) continue;
    cb.checked = checked;
    cb.closest(".gen-toggle").classList.toggle("checked", cb.checked);
  }
  updateGeneratorCount();
}
el("gens-enable-image-capable").addEventListener("click", () => setGeneratorsByImageCapability(true, true));
el("gens-disable-text-only").addEventListener("click", () => setGeneratorsByImageCapability(false, false));
// The main bulk buttons act on the media-generator section only; the describe
// section has its own all/none so a "Enable all" can't silently fan an image
// out to every paid describe endpoint too.
function setAllDescribers(checked) {
  for (const cb of describeRow.querySelectorAll("input:not(:disabled)")) {
    cb.checked = checked;
    cb.closest(".gen-toggle").classList.toggle("checked", cb.checked);
  }
  updateGeneratorCount();
}
el("describe-enable-all").addEventListener("click", () => setAllDescribers(true));
el("describe-disable-all").addEventListener("click", () => setAllDescribers(false));
el("opt-shape").addEventListener("change", updateGeneratorCompatibility);

// ---------- image attach: paste / drop / browse (up to maxInputImages) ----------

const inputThumbs = el("input-thumbs");
const multiInputHint = el("multi-input-hint");

function renderInputThumbs() {
  inputThumbs.replaceChildren();
  inputImageItems.forEach((item, index) => {
    const wrap = document.createElement("div");
    wrap.className = "input-thumb";
    const img = document.createElement("img");
    img.src = item.url;
    img.alt = `Input image ${index + 1}`;
    const badge = document.createElement("span");
    badge.className = "input-thumb-index";
    badge.textContent = String(index + 1);
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "input-thumb-remove";
    remove.title = `Remove image ${index + 1}`;
    remove.setAttribute("aria-label", `Remove image ${index + 1}`);
    remove.textContent = "×";
    remove.addEventListener("click", (e) => {
      e.stopPropagation();
      removeInputAt(index);
    });
    wrap.appendChild(img);
    wrap.appendChild(badge);
    wrap.appendChild(remove);
    inputThumbs.appendChild(wrap);
  });
  if (inputImageItems.length > 0 && inputImageItems.length < maxInputImages) {
    const add = document.createElement("button");
    add.type = "button";
    add.id = "input-add-slot";
    add.title = `Add another image (${inputImageItems.length}/${maxInputImages})`;
    add.textContent = `+ add (${inputImageItems.length}/${maxInputImages})`;
    add.addEventListener("click", (e) => {
      e.stopPropagation();
      fileInput.click();
    });
    inputThumbs.appendChild(add);
  }

  const hasImage = hasInputImages();
  inputThumbs.hidden = !hasImage;
  pasteHint.hidden = hasImage;
  clearBtn.hidden = !hasImage;
  multiInputHint.hidden = inputImageItems.length < 2;
  pasteZone.classList.toggle("has-image", hasImage);
  updateShapeOptionLabel();
  updateGeneratorCompatibility();
}

function appendImage(fileOrBlob) {
  if (!fileOrBlob || !fileOrBlob.type.startsWith("image/")) return false;
  if (inputImageItems.length >= maxInputImages) {
    sendError.textContent = `At most ${maxInputImages} input images. Remove one to add another.`;
    return false;
  }
  sendError.textContent = "";
  inputImageItems.push({
    file: fileOrBlob,
    url: URL.createObjectURL(fileOrBlob),
  });
  renderInputThumbs();
  return true;
}

function removeInputAt(index) {
  if (index < 0 || index >= inputImageItems.length) return;
  const [removed] = inputImageItems.splice(index, 1);
  if (removed?.url) URL.revokeObjectURL(removed.url);
  renderInputThumbs();
}

function clearImage() {
  for (const item of inputImageItems) {
    if (item.url) URL.revokeObjectURL(item.url);
  }
  inputImageItems = [];
  renderInputThumbs();
}

async function setImagesFromBlobs(blobs) {
  clearImage();
  for (const blob of blobs) {
    if (!appendImage(blob)) break;
  }
}

// Paste works anywhere on the page: grabbing the clipboard image is the
// core gesture, so don't make the user click the zone first. Additional
// pastes append until the cap.
document.addEventListener("paste", (e) => {
  for (const item of e.clipboardData.items) {
    if (item.type.startsWith("image/")) {
      appendImage(item.getAsFile());
      e.preventDefault();
      return;
    }
  }
});

pasteZone.addEventListener("click", (e) => {
  if (e.target.closest("#clear-image, .input-thumb-remove, #input-add-slot")) return;
  if (inputImageItems.length >= maxInputImages) {
    sendError.textContent = `At most ${maxInputImages} input images. Remove one to add another.`;
    return;
  }
  fileInput.click();
});
clearBtn.addEventListener("click", (e) => {
  e.stopPropagation();
  clearImage();
});
fileInput.addEventListener("change", () => {
  if (fileInput.files.length > 0) appendImage(fileInput.files[0]);
  fileInput.value = "";
});

pasteZone.addEventListener("dragover", (e) => {
  e.preventDefault();
  pasteZone.classList.add("dragover");
});
pasteZone.addEventListener("dragleave", () => pasteZone.classList.remove("dragover"));
pasteZone.addEventListener("drop", (e) => {
  e.preventDefault();
  pasteZone.classList.remove("dragover");
  const files = [...e.dataTransfer.files].filter((f) => f.type.startsWith("image/"));
  for (const file of files) {
    if (!appendImage(file)) break;
  }
});

// ---------- past-input-image picker ("load a previous image") ----------

// Every user-uploaded input image ever archived, deduplicated server-side by
// content hash, newest first. Fetched fresh on each open.
const inputLibraryPanel = el("input-library-panel");
const inputLibraryToggle = el("input-library-toggle");

function closeInputLibrary() {
  inputLibraryPanel.hidden = true;
  inputLibraryToggle.setAttribute("aria-expanded", "false");
}

async function attachLibraryImage(item) {
  const resp = await fetch(apiUrl(item.url));
  if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
  const blob = await resp.blob();
  if (!blob.type.startsWith("image/")) {
    throw new Error(`unexpected content type ${blob.type || "unknown"}`);
  }
  appendImage(blob);
}

async function openInputLibrary() {
  inputLibraryPanel.hidden = false;
  inputLibraryToggle.setAttribute("aria-expanded", "true");
  const results = el("input-library-results");
  const showMessage = (text) => {
    const p = document.createElement("p");
    p.className = "input-library-empty";
    p.textContent = text;
    results.replaceChildren(p);
  };
  showMessage("loading…");

  let images;
  try {
    const resp = await fetch(apiUrl("api/input-images"));
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    images = (await resp.json()).images;
  } catch (err) {
    showMessage(`could not list past images: ${err}`);
    return;
  }
  if (inputLibraryPanel.hidden) return;
  // Night mode also filters the past-input picker, whose tiles carry the
  // original prompt as their tooltip.
  images = images.filter((item) => !promptIsNightHidden(item.prompt));
  if (images.length === 0) {
    showMessage("No uploaded input images yet — paste or drop one to start the collection.");
    return;
  }

  results.replaceChildren(...images.map((item) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "input-library-item";
    button.setAttribute("role", "option");
    const when = item.createdAtUnixMs ? new Date(item.createdAtUnixMs).toLocaleString() : "";
    button.title = `${item.width}×${item.height} · first used ${when}\n${item.prompt}`;
    const img = document.createElement("img");
    img.src = apiUrl(`${item.url}?thumb=1`);
    img.loading = "lazy";
    img.alt = "Previously uploaded input image";
    button.appendChild(img);
    button.addEventListener("click", async () => {
      button.disabled = true;
      try {
        await attachLibraryImage(item);
        closeInputLibrary();
      } catch (err) {
        sendError.textContent = `could not load that image: ${err}`;
      } finally {
        button.disabled = false;
      }
    });
    return button;
  }));
}

inputLibraryToggle.addEventListener("click", () => {
  if (inputLibraryPanel.hidden) openInputLibrary();
  else closeInputLibrary();
});
el("input-library-close").addEventListener("click", closeInputLibrary);
document.addEventListener("pointerdown", (event) => {
  if (!inputLibraryPanel.hidden && !el("input-library-control").contains(event.target)) {
    closeInputLibrary();
  }
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && !inputLibraryPanel.hidden) closeInputLibrary();
});

// ---------- prompt inspiration library ----------

const InspirationStorageKey = "multi-image-client.inspiration-library.v1";
const inspirationCore = [
  ["style", "Japanese woodblock print", "art direction: Japanese woodblock print, crisp ink contours, flat balanced color planes"],
  ["style", "Gouache editorial illustration", "art direction: layered gouache editorial illustration, tactile brushwork, clear silhouettes"],
  ["style", "Risograph poster", "art direction: risograph poster print, limited bright ink palette, slight registration texture"],
  ["style", "Cut-paper collage", "art direction: dimensional cut-paper collage, clean layered shapes and cast-paper depth"],
  ["style", "Ink wash", "art direction: expressive ink wash with deliberate brush marks and spacious composition"],
  ["style", "Art Nouveau poster", "art direction: Art Nouveau poster design, flowing botanical linework and decorative framing"],
  ["style", "Bauhaus graphic design", "art direction: Bauhaus graphic design, geometric forms, bold primary color relationships"],
  ["style", "Memphis design", "art direction: playful Memphis design, bright geometric pattern and confident negative space"],
  ["style", "Isometric miniature", "art direction: precise isometric miniature, readable architectural details and clean perspective"],
  ["style", "Architectural watercolor", "art direction: architectural watercolor, precise perspective, luminous washes, fine pen detail"],
  ["style", "Scientific illustration", "art direction: meticulous scientific illustration, clear labeled-diagram sensibility without text"],
  ["style", "Screenprint", "art direction: hand-pulled screenprint, limited saturated palette and tactile ink texture"],
  ["style", "Ceramic sculpture", "art direction: handcrafted glazed ceramic sculpture, visible material texture and studio daylight"],
  ["style", "Textile tapestry", "art direction: woven textile tapestry, rich fiber texture and an organized ornamental pattern"],
  ["style", "Retro-futurist travel poster", "art direction: retro-futurist travel poster, optimistic graphic forms and clean daylight color"],
  ["style", "Surrealist collage", "art direction: precise surrealist collage, unexpected but coherent scale relationships"],
  ["style", "Botanical field guide", "art direction: botanical field-guide illustration, careful observation and a bright clean background"],
  ["style", "Stop-motion miniature", "art direction: handcrafted stop-motion miniature set, visible tactile materials and bright studio lighting"],
  ["world", "Sunlit floating archipelago", "setting: a sunlit floating archipelago connected by rope bridges, clear air and lush planted terraces"],
  ["world", "Coastal cliff observatory", "setting: a bright coastal cliff observatory above the sea, wind-shaped grasses and precise instruments"],
  ["world", "Overgrown library conservatory", "setting: an overgrown library conservatory, daylight through glass ceilings and orderly shelves of curiosities"],
  ["world", "Desert research station", "setting: a clear daytime desert research station with geometric shade structures and distant mesas"],
  ["world", "Canal city workshop", "setting: a lively canal city workshop, reflective water, painted facades, and open doors"],
  ["world", "Mountain rail terminal", "setting: a bright mountain rail terminal among alpine meadows and clean modern wayfinding"],
  ["world", "Orbital greenhouse", "setting: an orbital greenhouse with sunlit planting bays, curved windows, and a visible planet below"],
  ["world", "Forest village canopy", "setting: a forest village built through the canopy, suspended walkways and warm morning sunlight"],
  ["world", "Iceberg research harbor", "setting: a polar research harbor beside clear blue icebergs under full daylight"],
  ["world", "Ancient hilltop market", "setting: an ancient hilltop market with terraced stone, colorful awnings, and an expansive daylight view"],
  ["world", "Underwater museum", "setting: an underwater museum with bright filtered water, glass corridors, and clearly visible exhibits"],
  ["world", "Rainy neon street", "setting: a rain-polished city street with saturated signage, crisp reflections, and readable storefronts"],
  ["world", "Solarpunk neighborhood", "setting: a bright solarpunk neighborhood with rooftop gardens, public transit, and abundant daylight"],
  ["world", "Giant botanical laboratory", "setting: a giant botanical laboratory with oversized specimens, skylights, and orderly worktables"],
  ["world", "Clifftop wind farm", "setting: a clean clifftop wind farm above the ocean, dramatic scale and full clear daylight"],
  ["world", "Moon base commons", "setting: a sunlit lunar base commons with modular habitats, crisp shadows, and Earth in the sky"],
  ["composition", "Low horizon", "composition: low horizon, expansive sky, strong foreground anchor, and a clear focal subject"],
  ["composition", "Leading lines", "composition: strong leading lines guiding directly to the main subject"],
  ["composition", "Symmetrical frontal", "composition: calm symmetrical frontal view with precise visual hierarchy"],
  ["composition", "Rule of thirds", "composition: deliberate rule-of-thirds placement with generous readable negative space"],
  ["composition", "Bird's-eye map", "composition: bird's-eye view with coherent miniature detail and easy-to-read spatial organization"],
  ["composition", "Intimate close-up", "composition: intimate close-up with a clear primary detail and softly simplified surroundings"],
  ["composition", "Layered depth", "composition: layered foreground, middle distance, and background with clean separation"],
  ["composition", "Bold diagonal", "composition: bold diagonal movement, balanced by a stable counterweight"],
  ["composition", "Centered icon", "composition: a centered iconic subject on a simple high-contrast background"],
  ["composition", "Panoramic story", "composition: a wide panoramic scene with several clearly separated story moments"],
  ["atmosphere", "Clear spring morning", "clear spring-morning daylight, fresh balanced color, and highly readable details"],
  ["atmosphere", "Warm studio daylight", "warm studio daylight, soft natural shadows, and accurate material color"],
  ["atmosphere", "Crisp winter sun", "crisp winter sunlight, clean air, high visibility, and restrained cool color"],
  ["atmosphere", "Festival color", "joyful festival color, bright decorations, clear daylight, and organized visual energy"],
  ["atmosphere", "Calm contemplative", "calm contemplative mood, open breathing room, gentle daylight, and intentional simplicity"],
  ["atmosphere", "Playfully oversized", "playfully oversized scale, clear visual logic, and a bright inviting atmosphere"],
  ["atmosphere", "Luxurious craft", "luxurious handmade craft, rich material detail, and bright gallery-quality lighting"],
  ["atmosphere", "Whimsical precision", "whimsical precision, surprising details, and a coherent carefully organized scene"],
  ["atmosphere", "Bright retro optimism", "bright retro optimism, confident shapes, clean color separation, and full daytime clarity"],
  ["atmosphere", "Natural material study", "natural material study, tactile wood, paper, stone, or fiber texture in soft clear daylight"],
].map(([category, label, text], index) => ({ id: `core-${index}`, category, label, text }));

const inspirationState = {
  activeTab: "all",
  selectedIndex: 0,
  custom: [],
  favorites: [],
  recent: [],
};
let inspirationRendered = [];

function loadInspirationState() {
  try {
    const saved = JSON.parse(localStorage.getItem(InspirationStorageKey) || "{}");
    inspirationState.custom = Array.isArray(saved.custom) ? saved.custom.filter(validInspirationItem) : [];
    inspirationState.favorites = Array.isArray(saved.favorites) ? saved.favorites : [];
    inspirationState.recent = Array.isArray(saved.recent) ? saved.recent : [];
  } catch {
    // A malformed local value should never make the composer unusable.
  }
}

function validInspirationItem(item) {
  return item && typeof item.id === "string" && typeof item.label === "string"
    && typeof item.text === "string" && typeof item.category === "string";
}

function saveInspirationState() {
  localStorage.setItem(InspirationStorageKey, JSON.stringify({
    custom: inspirationState.custom,
    favorites: inspirationState.favorites,
    recent: inspirationState.recent,
  }));
}

function inspirationItems() {
  return [...inspirationCore, ...inspirationState.custom];
}

function isFavorite(item) {
  return inspirationState.favorites.includes(item.id);
}

function categoryLabel(category) {
  return ({ style: "Style", world: "World", composition: "Composition", atmosphere: "Mood & material", custom: "Mine" })[category] || category;
}

function openInspiration() {
  el("inspiration-panel").hidden = false;
  el("inspiration-toggle").setAttribute("aria-expanded", "true");
  renderInspiration();
  el("inspiration-search").focus();
}

function closeInspiration() {
  el("inspiration-panel").hidden = true;
  el("inspiration-toggle").setAttribute("aria-expanded", "false");
  el("inspiration-toggle").focus();
}

function renderInspiration() {
  const search = el("inspiration-search").value.trim().toLocaleLowerCase();
  const itemsById = new Map(inspirationItems().map((item) => [item.id, item]));
  const tabs = [
    ["all", "All"], ["recent", "Recent"], ["favorites", "Favorites"],
    ["style", "Style"], ["world", "World"], ["composition", "Composition"], ["atmosphere", "Mood"],
  ];
  el("inspiration-tabs").replaceChildren(...tabs.map(([key, label]) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "inspiration-tab";
    button.textContent = label;
    button.setAttribute("role", "tab");
    button.setAttribute("aria-selected", String(inspirationState.activeTab === key));
    button.addEventListener("click", () => {
      inspirationState.activeTab = key;
      inspirationState.selectedIndex = 0;
      renderInspiration();
    });
    return button;
  }));

  let source = inspirationItems();
  if (inspirationState.activeTab === "recent") {
    source = inspirationState.recent.map((id) => itemsById.get(id)).filter(Boolean);
  } else if (inspirationState.activeTab === "favorites") {
    source = inspirationState.favorites.map((id) => itemsById.get(id)).filter(Boolean);
  } else if (inspirationState.activeTab !== "all") {
    source = source.filter((item) => item.category === inspirationState.activeTab);
  }
  if (search) {
    source = source.filter((item) => `${item.label} ${item.text} ${item.category}`.toLocaleLowerCase().includes(search));
  } else if (!["recent", "favorites"].includes(inspirationState.activeTab)) {
    source = source.sort((a, b) => Number(isFavorite(b)) - Number(isFavorite(a)) || a.label.localeCompare(b.label));
  }
  inspirationRendered = source;
  inspirationState.selectedIndex = Math.min(inspirationState.selectedIndex, Math.max(0, source.length - 1));

  const results = el("inspiration-results");
  if (source.length === 0) {
    const empty = document.createElement("p");
    empty.className = "inspiration-empty";
    empty.textContent = search ? "No matching direction yet." : "Nothing here yet — star directions or add your own.";
    results.replaceChildren(empty);
  } else {
    results.replaceChildren(...source.map((item, index) => inspirationItemElement(item, index)));
  }
  el("inspiration-custom").hidden = !search || source.some((item) => item.text.toLocaleLowerCase() === search);
}

function inspirationItemElement(item, index) {
  const row = document.createElement("div");
  row.className = `inspiration-item${index === inspirationState.selectedIndex ? " is-active" : ""}`;
  row.setAttribute("role", "option");
  row.setAttribute("aria-selected", String(index === inspirationState.selectedIndex));

  const use = document.createElement("button");
  use.type = "button";
  use.className = "inspiration-use";
  use.addEventListener("click", () => useInspiration(item));
  const label = document.createElement("span");
  label.className = "inspiration-label";
  label.textContent = item.label;
  const preview = document.createElement("span");
  preview.className = "inspiration-preview";
  preview.textContent = `${categoryLabel(item.category)} · ${item.text}`;
  use.append(label, preview);

  const actions = document.createElement("div");
  actions.className = "inspiration-actions";
  const favorite = document.createElement("button");
  favorite.type = "button";
  favorite.className = `inspiration-action${isFavorite(item) ? " is-favorite" : ""}`;
  favorite.textContent = isFavorite(item) ? "favorited" : "favorite";
  favorite.title = isFavorite(item) ? "Remove from favorites" : "Add to favorites";
  favorite.setAttribute("aria-label", favorite.title);
  favorite.addEventListener("click", () => toggleInspirationFavorite(item.id));
  actions.appendChild(favorite);
  if (item.id.startsWith("custom-")) {
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "inspiration-action";
    remove.textContent = "×";
    remove.title = "Delete this personal direction";
    remove.setAttribute("aria-label", remove.title);
    remove.addEventListener("click", () => deleteCustomInspiration(item.id));
    actions.appendChild(remove);
  }
  row.append(use, actions);
  return row;
}

function insertPromptText(text) {
  const start = promptBox.selectionStart ?? promptBox.value.length;
  const end = promptBox.selectionEnd ?? start;
  const before = promptBox.value.slice(0, start);
  const after = promptBox.value.slice(end);
  const separator = before.trim() && !/[\s,.;:]$/.test(before) ? ", " : before.trim() ? " " : "";
  const trailing = after && !/^\s/.test(after) ? " " : "";
  promptBox.value = `${before}${separator}${text}${trailing}${after}`;
  const caret = before.length + separator.length + text.length;
  promptBox.focus();
  promptBox.setSelectionRange(caret, caret);
  if (mcpheeCtl) mcpheeCtl.refresh();
  if (mcpheePanel && !mcpheePanelContainer.hidden) mcpheePanel.refresh();
  updatePromptLimitNotice();
}

function useInspiration(item) {
  insertPromptText(item.text);
  inspirationState.recent = [item.id, ...inspirationState.recent.filter((id) => id !== item.id)].slice(0, 12);
  saveInspirationState();
  renderInspiration();
}

function toggleInspirationFavorite(id) {
  inspirationState.favorites = isFavorite({ id })
    ? inspirationState.favorites.filter((favoriteId) => favoriteId !== id)
    : [id, ...inspirationState.favorites.filter((favoriteId) => favoriteId !== id)];
  saveInspirationState();
  renderInspiration();
}

function addCustomInspiration() {
  const text = el("inspiration-search").value.trim();
  if (!text) return;
  const existing = inspirationItems().find((item) => item.text.toLocaleLowerCase() === text.toLocaleLowerCase());
  const item = existing || {
    id: `custom-${crypto.randomUUID()}`,
    category: "custom",
    label: text.length > 58 ? `${text.slice(0, 55)}…` : text,
    text,
  };
  if (!existing) inspirationState.custom.unshift(item);
  if (!isFavorite(item)) inspirationState.favorites.unshift(item.id);
  useInspiration(item);
  el("inspiration-search").value = "";
  saveInspirationState();
  renderInspiration();
}

function deleteCustomInspiration(id) {
  inspirationState.custom = inspirationState.custom.filter((item) => item.id !== id);
  inspirationState.favorites = inspirationState.favorites.filter((favoriteId) => favoriteId !== id);
  inspirationState.recent = inspirationState.recent.filter((recentId) => recentId !== id);
  saveInspirationState();
  renderInspiration();
}

el("inspiration-toggle").addEventListener("click", () => el("inspiration-panel").hidden ? openInspiration() : closeInspiration());
el("inspiration-close").addEventListener("click", closeInspiration);
el("inspiration-search").addEventListener("input", () => {
  inspirationState.selectedIndex = 0;
  renderInspiration();
});
el("inspiration-add-custom").addEventListener("click", addCustomInspiration);
el("inspiration-search").addEventListener("keydown", (event) => {
  if (event.key === "Escape") { event.preventDefault(); closeInspiration(); }
  if (event.key === "ArrowDown" || event.key === "ArrowUp") {
    event.preventDefault();
    if (inspirationRendered.length) {
      const delta = event.key === "ArrowDown" ? 1 : -1;
      inspirationState.selectedIndex = (inspirationState.selectedIndex + delta + inspirationRendered.length) % inspirationRendered.length;
      renderInspiration();
    }
  }
  if (event.key === "Enter") {
    event.preventDefault();
    if (inspirationRendered[inspirationState.selectedIndex]) useInspiration(inspirationRendered[inspirationState.selectedIndex]);
    else addCustomInspiration();
  }
});
document.addEventListener("pointerdown", (event) => {
  if (!el("inspiration-panel").hidden && !el("inspiration-control").contains(event.target)) closeInspiration();
});
loadInspirationState();

// ---------- shared-site identity: username + person filters ----------

// Every job is created under a display name (attribution, not privacy —
// everyone sees everyone's work by design). The name lives in this browser's
// localStorage; when the server's access gate is on, the login name seeds it.
const usernameInput = el("username-input");
const logoutBtn = el("logout");
usernameInput.value = localStorage.getItem(UsernameKey) || "";

function currentUsername() {
  return usernameInput.value.trim().replace(/\s+/g, " ");
}

usernameInput.addEventListener("change", () => {
  localStorage.setItem(UsernameKey, currentUsername());
  favoriteMutationError = null;
  refreshFavoritePresentation();
});

function applyAuthState() {
  logoutBtn.hidden = !authInfo.enabled;
  if (authInfo.enabled && authInfo.user && !currentUsername()) {
    usernameInput.value = authInfo.user;
    localStorage.setItem(UsernameKey, authInfo.user);
  }
}

logoutBtn.addEventListener("click", async () => {
  try {
    await fetch(apiUrl("api/auth/logout"), { method: "POST" });
  } finally {
    location.reload();
  }
});

// Person filter chips: "everyone" plus one chip per creator name seen in
// history (seeded from /api/users, then live-accumulated from job-known
// envelopes). Chips multi-select; an empty selection means show everyone.
const userFiltersBar = el("user-filters");

function userFilterAllows(user) {
  return selectedUserFilter.size === 0 || selectedUserFilter.has(user || "");
}

function applyUserFilterToCard(card) {
  card.classList.toggle("user-filter-hidden", !userFilterAllows(card.dataset.user || ""));
}

function applyUserFilterAll() {
  for (const card of document.querySelectorAll("#jobs .job, #archive .job")) {
    applyUserFilterToCard(card);
  }
  if (!imageViewer.hidden) renderImageViewer();
}

function persistUserFilter() {
  localStorage.setItem(UserFilterKey, JSON.stringify([...selectedUserFilter]));
}

function renderUserChips() {
  // Keep the static "everyone" chip; rebuild the per-person chips after it.
  for (const chip of userFiltersBar.querySelectorAll(".user-chip:not([data-user='*'])")) {
    chip.remove();
  }
  const everyone = userFiltersBar.querySelector("[data-user='*']");
  everyone.classList.toggle("selected", selectedUserFilter.size === 0);
  const names = [...knownUsers.keys()].sort((a, b) =>
    (a || "~").localeCompare(b || "~", undefined, { sensitivity: "base" }));
  for (const name of names) {
    const chip = document.createElement("button");
    chip.type = "button";
    chip.className = "user-chip";
    chip.dataset.user = name;
    chip.textContent = name === "" ? "(unnamed)" : name;
    if (name !== "" && name === currentUsername()) chip.classList.add("self");
    chip.title = `${knownUsers.get(name)} job${knownUsers.get(name) === 1 ? "" : "s"}`;
    chip.classList.toggle("selected", selectedUserFilter.has(name));
    chip.addEventListener("click", () => {
      if (selectedUserFilter.has(name)) selectedUserFilter.delete(name);
      else selectedUserFilter.add(name);
      persistUserFilter();
      renderUserChips();
      applyUserFilterAll();
    });
    userFiltersBar.appendChild(chip);
  }
}

userFiltersBar.querySelector("[data-user='*']").addEventListener("click", () => {
  selectedUserFilter.clear();
  persistUserFilter();
  renderUserChips();
  applyUserFilterAll();
});

// Ensure a creator seen in the live feed has a chip. Counts come from
// /api/users (tooltip only); envelope replays must not inflate them.
function registerUser(name) {
  const key = name || "";
  if (knownUsers.has(key)) return;
  knownUsers.set(key, 1);
  renderUserChips();
}

async function loadKnownUsers() {
  try {
    const resp = await fetch(apiUrl("api/users"));
    if (!resp.ok) return;
    const body = await resp.json();
    knownUsers = new Map(body.users.map((u) => [u.user || "", u.count]));
    renderUserChips();
  } catch { /* the filter bar fills in from live events regardless */ }
}

// ---------- creator-only persistent stream hiding ----------

function hiddenImageIdentity(jobId, generator, imageIndex) {
  return `${jobId}|${generator}|${Number(imageIndex)}`;
}

function isPromptHidden(jobId) {
  return hiddenPromptJobIds.has(String(jobId || ""));
}

function isImageHidden(jobId, generator, imageIndex) {
  return hiddenImageKeys.has(hiddenImageIdentity(jobId, generator, imageIndex));
}

function applyVisibilitySnapshot(raw) {
  if (!raw || raw.unchanged === true) return;
  if (!raw.version || !Array.isArray(raw.prompts) || !Array.isArray(raw.images)) {
    throw new Error("visibility response is malformed");
  }

  const prompts = new Set(raw.prompts.map(String));
  const images = new Set();
  for (const item of raw.images) {
    if (!item || !item.jobId || !item.generator ||
        !Number.isInteger(item.imageIndex) || item.imageIndex < 0) {
      throw new Error("visibility response contains an invalid image identity");
    }
    images.add(hiddenImageIdentity(item.jobId, item.generator, item.imageIndex));
  }

  visibilityServerVersion = String(raw.version);
  hiddenPromptJobIds = prompts;
  hiddenImageKeys = images;

  for (const card of document.querySelectorAll(
    "#jobs .job, #archive .job, #favorites-grid .favorite-gallery-card")) {
    const jobId = card.dataset.jobId || card.id.replace(/^job-/, "");
    if (isPromptHidden(jobId)) {
      for (const link of card.querySelectorAll('a[data-viewer-image="true"]')) {
        const cached = imageViewerCache.get(link.href);
        if (cached) discardImageViewerCacheEntry(link.href, cached);
      }
      const inputUrl = imageViewerInputUrl(jobId);
      const inputCached = imageViewerCache.get(inputUrl);
      if (inputCached) discardImageViewerCacheEntry(inputUrl, inputCached);
      card.remove();
      continue;
    }
    let hasHiddenImage = false;
    for (const link of [...card.querySelectorAll('a[data-viewer-image="true"]')]) {
      if (link.dataset.resultKind === "text") continue;
      if (!isImageHidden(
        link.dataset.jobId,
        link.dataset.generator,
        Number(link.dataset.imageIndex))) {
        continue;
      }
      const cached = imageViewerCache.get(link.href);
      if (cached) discardImageViewerCacheEntry(link.href, cached);
      (link.closest(".media-result") || link).remove();
      hasHiddenImage = true;
    }
    if (hasHiddenImage) card.querySelector(".grid-link")?.remove();
  }
  for (const row of document.querySelectorAll("#logs-lines .log-row[data-job-id]")) {
    if (isPromptHidden(row.dataset.jobId)) row.remove();
  }

  if (imageViewerState &&
      (isPromptHidden(imageViewerState.jobId) ||
       isImageHidden(
         imageViewerState.jobId,
         imageViewerState.generator,
         imageViewerState.imageIndex))) {
    closeImageViewer();
  } else if (!imageViewer.hidden) {
    renderImageViewer();
  }
  updateCostTotals();
  loadFavorites();
}

async function persistHiddenResource(kind, jobId, generator = "", imageIndex = -1) {
  if (visibilityMutation) return false;
  const identity = kind === "prompt"
    ? `prompt|${jobId}`
    : `image|${hiddenImageIdentity(jobId, generator, imageIndex)}`;
  visibilityMutation = identity;
  const form = new FormData();
  form.append("kind", kind);
  form.append("jobId", jobId);
  if (kind === "image") {
    form.append("generator", generator);
    form.append("imageIndex", String(imageIndex));
  }
  try {
    const response = await fetch(apiUrl("api/visibility"), { method: "POST", body: form });
    if (response.status === 401) { location.reload(); return false; }
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
    applyVisibilitySnapshot(body);
    return true;
  } finally {
    visibilityMutation = null;
  }
}

function createHidePromptButton(jobId) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "hide-prompt";
  button.textContent = "hide prompt";
  button.title = "Permanently hide this entire prompt and all of its results from everyone";
  button.addEventListener("click", async () => {
    if (!confirm(
      "Hide this entire prompt and all its results from everyone?\n\n" +
      "This cannot be undone in the UI.")) return;
    button.disabled = true;
    button.textContent = "hiding…";
    try {
      await persistHiddenResource("prompt", jobId);
    } catch (error) {
      button.disabled = false;
      button.textContent = "hide failed";
      button.title = String(error);
    }
  });
  return button;
}

// ---------- shared persistent image + prompt favorites ----------

function favoriteIdentity(jobId, generator, imageIndex) {
  return `${jobId}|${generator}|${Number(imageIndex)}`;
}

function favoriteIdentityFor(ref) {
  return ref ? favoriteIdentity(ref.jobId, ref.generator, ref.imageIndex) : "";
}

function promptFavoriteIdentity(jobId) {
  return String(jobId || "");
}

function favoriteMutationIdentity(kind, identity) {
  return `${kind}|${identity}`;
}

function normalizeFavoriteItem(raw) {
  if (!raw || typeof raw !== "object" ||
      !raw.jobId || !raw.generator ||
      !Number.isInteger(raw.imageIndex) || raw.imageIndex < 0 ||
      !Number.isInteger(raw.generatorImageCount) ||
      raw.generatorImageCount <= raw.imageIndex ||
      typeof raw.prompt !== "string" ||
      !(raw.jobCreatedAtUnixMs > 0) ||
      !raw.imageUrl || !raw.thumbUrl ||
      !Array.isArray(raw.users) || raw.users.some((user) => !user)) {
    throw new Error("favorites response contains an invalid exact image identity");
  }
  return {
    jobId: String(raw.jobId),
    generator: String(raw.generator),
    imageIndex: Number(raw.imageIndex),
    generatorImageCount: Number(raw.generatorImageCount),
    prompt: String(raw.prompt),
    createdBy: String(raw.createdBy || ""),
    jobCreatedAtUnixMs: Number(raw.jobCreatedAtUnixMs),
    hasInputImage: !!raw.hasInputImage,
    imageUrl: String(raw.imageUrl),
    thumbUrl: String(raw.thumbUrl),
    size: String(raw.size || ""),
    canHide: !!raw.canHide,
    users: [...new Set(raw.users.map(String))],
  };
}

function normalizePromptFavoriteItem(raw) {
  if (!raw || typeof raw !== "object" ||
      !raw.jobId || typeof raw.prompt !== "string" || !raw.prompt.trim() ||
      !(raw.jobCreatedAtUnixMs > 0) ||
      !Array.isArray(raw.users) || raw.users.some((user) => !user)) {
    throw new Error("favorites response contains an invalid exact prompt identity");
  }
  return {
    jobId: String(raw.jobId),
    prompt: String(raw.prompt),
    createdBy: String(raw.createdBy || ""),
    jobCreatedAtUnixMs: Number(raw.jobCreatedAtUnixMs),
    hasInputImage: !!raw.hasInputImage,
    canHide: !!raw.canHide,
    users: [...new Set(raw.users.map(String))],
  };
}

function favoriteItemFor(ref) {
  return favoriteItems.get(favoriteIdentityFor(ref)) || null;
}

function favoriteSnapshotSignature(imageItems, promptItems) {
  const ordered = (items) => [...items.entries()]
    .sort((a, b) => a[0].localeCompare(b[0]))
    .map(([key, item]) => [key, item]);
  return JSON.stringify({
    images: ordered(imageItems),
    prompts: ordered(promptItems),
  });
}

function rebuildFavoriteUsers() {
  favoriteUsers = new Map();
  for (const item of [...favoriteItems.values(), ...promptFavoriteItems.values()]) {
    for (const name of item.users) {
      favoriteUsers.set(name, (favoriteUsers.get(name) || 0) + 1);
    }
  }
}

function applyFavoriteMarkerToAnchor(link) {
  if (!link || link.dataset.resultKind === "text") return;
  const item = favoriteItems.get(favoriteIdentity(
    link.dataset.jobId,
    link.dataset.generator,
    Number(link.dataset.imageIndex)));
  const oldBadge = link.querySelector(":scope > .favorite-badge");
  if (!item || item.users.length === 0) {
    if (oldBadge) oldBadge.remove();
    link.classList.remove("is-favorited", "favorite-mine");
    if (link.dataset.favoriteTitle === "true") {
      link.removeAttribute("title");
      delete link.dataset.favoriteTitle;
    }
    return;
  }

  const mine = item.users.includes(currentUsername());
  link.classList.add("is-favorited");
  link.classList.toggle("favorite-mine", mine);
  const badge = oldBadge || document.createElement("span");
  badge.className = "favorite-badge";
  badge.textContent = item.users.length === 1 ? "★" : `★ ${item.users.length}`;
  badge.setAttribute("aria-hidden", "true");
  if (!oldBadge) link.appendChild(badge);
  link.title = `Favorited by ${item.users.join(", ")}`;
  link.dataset.favoriteTitle = "true";
}

function applyFavoriteMarkers() {
  for (const link of document.querySelectorAll('a[data-viewer-image="true"]')) {
    applyFavoriteMarkerToAnchor(link);
  }
}

function createPromptFavoriteButton(jobId) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "prompt-favorite";
  button.dataset.jobId = jobId;
  button.addEventListener("click", () => togglePromptFavorite(jobId));
  renderPromptFavoriteButton(button);
  return button;
}

function renderPromptFavoriteButton(button) {
  if (!button) return;
  const jobId = promptFavoriteIdentity(button.dataset.jobId);
  const key = favoriteMutationIdentity("prompt", jobId);
  const item = promptFavoriteItems.get(jobId);
  const users = item ? item.users : [];
  const mine = users.includes(currentUsername());

  button.classList.remove("pending", "error");
  button.disabled = false;
  button.setAttribute("aria-pressed", String(mine));
  if (favoriteMutation === key) {
    button.classList.add("pending");
    button.disabled = true;
    button.textContent = "saving…";
    button.title = "Persisting prompt favorite";
    return;
  }
  if (favoriteMutationError && favoriteMutationError.key === key) {
    button.classList.add("error");
    button.textContent = "favorite failed";
    button.title = favoriteMutationError.message;
    return;
  }

  button.textContent = mine
    ? `★ prompt${users.length > 1 ? ` · ${users.length}` : ""}`
    : users.length > 0
      ? `☆ prompt · ★ ${users.length}`
      : "☆ prompt";
  button.title = users.length > 0
    ? `Prompt favorited by ${users.join(", ")}. Click to ${mine ? "remove your favorite" : "add yours"}.`
    : "Favorite this entire prompt";
}

function applyPromptFavoriteMarkers() {
  for (const button of document.querySelectorAll(".prompt-favorite")) {
    renderPromptFavoriteButton(button);
  }
}

function renderFavoriteControls() {
  const filters = el("favorite-user-filters");
  filters.replaceChildren();
  const allImages = el("favorites-all-images");
  const everyone = el("favorites-everyone");
  allImages.classList.toggle("selected", favoriteBrowseUser === null);
  everyone.classList.toggle("selected", favoriteBrowseUser === "*");
  const total = favoriteItems.size + promptFavoriteItems.size;
  everyone.textContent = total === 1
    ? "everyone's favorites · 1"
    : `everyone's favorites · ${total}`;

  for (const [user, count] of [...favoriteUsers.entries()].sort((a, b) =>
    a[0].localeCompare(b[0], undefined, { sensitivity: "base" }))) {
    const chip = document.createElement("button");
    chip.type = "button";
    chip.className = "favorite-user-chip";
    chip.classList.toggle("selected", favoriteBrowseUser === user);
    chip.classList.toggle("self", user === currentUsername());
    chip.textContent = `${user} · ${count}`;
    chip.title = `Show ${user}'s ${count} favorite item${count === 1 ? "" : "s"}`;
    chip.addEventListener("click", () => setFavoriteBrowseUser(user));
    filters.appendChild(chip);
  }
}

function setFavoriteBrowseUser(user) {
  favoriteBrowseUser = user;
  renderFavoriteControls();
  const active = favoriteBrowseUser !== null;
  jobsSection.hidden = active;
  el("archive").hidden = active || el("archive-days").childElementCount === 0;
  favoritesGallery.hidden = !active;
  if (active) renderFavoritesGallery();
}

el("favorites-all-images").addEventListener("click", () => setFavoriteBrowseUser(null));
el("favorites-everyone").addEventListener("click", () => setFavoriteBrowseUser("*"));

function renderFavoritesGallery() {
  if (favoriteBrowseUser === null) return;
  const items = [
    ...[...favoriteItems.values()].map((item) => ({ ...item, kind: "image" })),
    ...[...promptFavoriteItems.values()].map((item) => ({ ...item, kind: "prompt" })),
  ]
    .filter((item) =>
      favoriteBrowseUser === "*" || item.users.includes(favoriteBrowseUser))
    .sort((a, b) => b.jobCreatedAtUnixMs - a.jobCreatedAtUnixMs);
  const title = favoriteBrowseUser === "*"
    ? "everyone's favorites"
    : `${favoriteBrowseUser}'s favorites`;
  el("favorites-gallery-title").textContent =
    `${title} — ${items.length} item${items.length === 1 ? "" : "s"}`;
  favoritesGrid.replaceChildren();

  if (items.length === 0) {
    const empty = document.createElement("p");
    empty.textContent = "No favorites in this view.";
    favoritesGrid.appendChild(empty);
    favoritesGalleryRenderPending = false;
    return;
  }

  for (const item of items) {
    const card = document.createElement("article");
    card.className = "job favorite-gallery-card";
    card.dataset.jobId = item.jobId;
    card.dataset.user = item.createdBy;
    card.dataset.hasInputImage = String(item.hasInputImage);
    card.dataset.canHide = String(item.canHide);

    let link = null;
    if (item.kind === "image") {
      link = document.createElement("a");
      link.href = apiUrl(item.imageUrl);
      link.target = "_blank";
      link.dataset.viewerImage = "true";
      link.dataset.jobId = item.jobId;
      link.dataset.generator = item.generator;
      link.dataset.imageIndex = String(item.imageIndex);
      link.dataset.generatorCount = String(item.generatorImageCount);

      const img = document.createElement("img");
      img.src = apiUrl(item.thumbUrl);
      img.loading = "lazy";
      img.alt = `${genLabel(item.generator)} image favorited by ${item.users.join(", ")}`;
      const dims = /^(\d+)x(\d+)$/.exec(item.size);
      if (dims) img.style.aspectRatio = `${dims[1]} / ${dims[2]}`;
      link.appendChild(img);
      card.appendChild(link);
    } else {
      card.classList.add("favorite-prompt-card");
      const kind = document.createElement("strong");
      kind.className = "favorite-gallery-kind";
      kind.textContent = "prompt";
      card.appendChild(kind);
    }

    const meta = document.createElement("div");
    meta.className = "favorite-gallery-meta";
    const madeBy = item.createdBy ? `made by ${item.createdBy}` : "creator not recorded";
    const date = new Date(item.jobCreatedAtUnixMs).toLocaleDateString();
    const resource = item.kind === "image" ? `${genLabel(item.generator)} image` : "whole prompt";
    const summary = document.createElement("span");
    summary.textContent =
      `${resource} · ${madeBy} · ${date} · favorited by ${item.users.join(", ")}`;
    meta.append(summary, createPromptFavoriteButton(item.jobId));
    if (item.canHide) meta.appendChild(createHidePromptButton(item.jobId));
    card.appendChild(meta);

    const prompt = document.createElement("div");
    prompt.className = "job-prompt";
    prompt.textContent = item.prompt;
    card.appendChild(prompt);
    favoritesGrid.appendChild(card);
    if (link) applyFavoriteMarkerToAnchor(link);
  }
  favoritesGalleryRenderPending = false;
}

function refreshFavoritePresentation() {
  renderFavoriteControls();
  applyFavoriteMarkers();
  applyPromptFavoriteMarkers();
  if (!imageViewer.hidden) {
    const current = locateImageViewerState(getImageViewerPrompts());
    renderImageViewerFavorite(current ? current.item : null);
    renderImageViewerHide(current ? current.item : null);
  }
  if (favoriteBrowseUser !== null) {
    if (imageViewer.hidden) renderFavoritesGallery();
    else favoritesGalleryRenderPending = true;
  }
}

async function loadFavorites() {
  if (favoritesRefreshInFlight) return;
  favoritesRefreshInFlight = true;
  try {
    const versionQuery = favoritesServerVersion
      ? `?version=${encodeURIComponent(favoritesServerVersion)}`
      : "";
    const response = await fetch(apiUrl(`api/favorites${versionQuery}`));
    if (response.status === 401) { location.reload(); return; }
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
    if (body.unchanged === true) {
      if (!body.version || body.version !== favoritesServerVersion) {
        throw new Error("favorites unchanged response has the wrong version");
      }
      favoritesLastRefreshAt = Date.now();
      el("favorites-error").textContent = "";
      return;
    }
    if (!Array.isArray(body.favorites) ||
        !Array.isArray(body.promptFavorites) ||
        !Array.isArray(body.users)) {
      throw new Error("favorites response is malformed");
    }
    if (!body.version) throw new Error("favorites response has no version");

    const nextItems = new Map();
    for (const raw of body.favorites) {
      const item = normalizeFavoriteItem(raw);
      const key = favoriteIdentityFor(item);
      if (nextItems.has(key)) {
        throw new Error(`favorites response repeats ${key}`);
      }
      nextItems.set(key, item);
    }
    const nextPromptItems = new Map();
    for (const raw of body.promptFavorites) {
      const item = normalizePromptFavoriteItem(raw);
      const key = promptFavoriteIdentity(item.jobId);
      if (nextPromptItems.has(key)) {
        throw new Error(`favorites response repeats prompt ${key}`);
      }
      nextPromptItems.set(key, item);
    }
    const nextUsers = new Map();
    for (const entry of body.users) {
      if (!entry.user || !Number.isInteger(entry.count) || entry.count < 1) {
        throw new Error("favorites response contains an invalid user count");
      }
      nextUsers.set(String(entry.user), Number(entry.count));
    }
    const nextSignature = favoriteSnapshotSignature(nextItems, nextPromptItems);
    const changed = nextSignature !== favoritesSnapshotSignature;
    favoriteItems = nextItems;
    promptFavoriteItems = nextPromptItems;
    favoriteUsers = nextUsers;
    favoritesSnapshotSignature = nextSignature;
    favoritesServerVersion = String(body.version);
    favoritesLastRefreshAt = Date.now();
    el("favorites-error").textContent = "";
    if (favoriteBrowseUser !== null &&
        favoriteBrowseUser !== "*" &&
        !favoriteUsers.has(favoriteBrowseUser) &&
        imageViewer.hidden) {
      setFavoriteBrowseUser(null);
    }
    if (changed) refreshFavoritePresentation();
  } catch (error) {
    el("favorites-error").textContent = `favorites refresh failed: ${error}`;
  } finally {
    favoritesRefreshInFlight = false;
  }
}

function pollFavorites() {
  if (document.hidden && Date.now() - favoritesLastRefreshAt < 30000) return;
  loadFavorites();
}

function renderImageViewerFavorite(item) {
  imageViewerFavorite.className = "";
  imageViewerFavorite.disabled = !item || item.kind === "text";
  imageViewerFavorite.setAttribute("aria-pressed", "false");
  if (!item || item.kind === "text") {
    imageViewerFavorite.textContent = "☆ favorite";
    imageViewerFavorite.title = item && item.kind === "text"
      ? "Describe-result views cannot be favorited."
      : "Favorite this image (v)";
    return;
  }

  const identity = favoriteIdentityFor(item);
  const mutationKey = favoriteMutationIdentity("image", identity);
  if (favoriteMutation === mutationKey) {
    imageViewerFavorite.classList.add("pending");
    imageViewerFavorite.textContent = "saving…";
    imageViewerFavorite.title = "Persisting favorite";
    return;
  }
  if (favoriteMutationError && favoriteMutationError.key === mutationKey) {
    imageViewerFavorite.classList.add("error");
    imageViewerFavorite.textContent = "favorite failed";
    imageViewerFavorite.title = favoriteMutationError.message;
    return;
  }

  const favorite = favoriteItems.get(identity);
  const users = favorite ? favorite.users : [];
  const mine = users.includes(currentUsername());
  imageViewerFavorite.setAttribute("aria-pressed", String(mine));
  imageViewerFavorite.textContent = mine
    ? `★ favorited${users.length > 1 ? ` · ${users.length}` : ""}`
    : users.length > 0
      ? `☆ favorite · ★ ${users.length}`
      : "☆ favorite";
  imageViewerFavorite.title = users.length > 0
    ? `Favorited by ${users.join(", ")}. Press v to ${mine ? "remove your favorite" : "add yours"}.`
    : "Favorite this image (v)";
}

function renderImageViewerVideo(item) {
  const allowed = !!item && item.kind !== "text" && videoGeneration.available;
  imageViewerVideo.hidden = !allowed;
  imageViewerVideo.disabled = !allowed;
}

function renderImageViewerHide(item) {
  const card = item ? findViewerAnchor(item)?.closest(".job") : null;
  const allowed = !!item && item.kind !== "text" && card?.dataset.canHide === "true";
  imageViewerHide.hidden = !allowed;
  imageViewerHide.disabled = !allowed || visibilityMutation !== null;
  imageViewerHide.textContent = visibilityMutation ? "hiding…" : "hide image";
}

async function hideCurrentViewerImage() {
  const current = locateImageViewerState(getImageViewerPrompts());
  if (!current || current.item.kind === "text") return;
  if (findViewerAnchor(current.item)?.closest(".job")?.dataset.canHide !== "true") return;
  if (!confirm("Hide only this image from everyone?\n\nThis cannot be undone in the UI.")) return;
  renderImageViewerHide(current.item);
  try {
    await persistHiddenResource(
      "image",
      current.item.jobId,
      current.item.generator,
      current.item.imageIndex);
  } catch (error) {
    imageViewerHide.disabled = false;
    imageViewerHide.textContent = "hide failed";
    imageViewerHide.title = String(error);
  }
}

async function toggleImageViewerFavorite() {
  const current = locateImageViewerState(getImageViewerPrompts());
  if (!current || current.item.kind === "text") return;
  const user = currentUsername();
  if (!user) {
    favoriteMutationError = {
      key: favoriteMutationIdentity("image", favoriteIdentityFor(current.item)),
      message: "Choose a 'creating as' username before favoriting.",
    };
    renderImageViewerFavorite(current.item);
    return;
  }

  const key = favoriteIdentityFor(current.item);
  const mutationKey = favoriteMutationIdentity("image", key);
  if (favoriteMutation) return;
  const existing = favoriteItems.get(key);
  const desired = !(existing && existing.users.includes(user));
  favoriteMutation = mutationKey;
  favoriteMutationError = null;
  renderImageViewerFavorite(current.item);

  const form = new FormData();
  form.append("kind", "image");
  form.append("user", user);
  form.append("jobId", current.item.jobId);
  form.append("generator", current.item.generator);
  form.append("imageIndex", String(current.item.imageIndex));
  form.append("favorite", String(desired));

  try {
    const response = await fetch(apiUrl("api/favorites"), { method: "POST", body: form });
    if (response.status === 401) { location.reload(); return; }
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
    if (body.kind !== "image") {
      throw new Error("favorite response kind did not match the requested image");
    }
    if (body.item) {
      const item = normalizeFavoriteItem(body.item);
      if (favoriteIdentityFor(item) !== key) {
        throw new Error("favorite response identity did not match the requested image");
      }
      favoriteItems.set(key, item);
    } else {
      favoriteItems.delete(key);
    }
    // Counts are cheap to recompute and avoid trusting a mutation response
    // about unrelated users.
    rebuildFavoriteUsers();
    favoritesSnapshotSignature =
      favoriteSnapshotSignature(favoriteItems, promptFavoriteItems);
    favoriteMutation = null;
    refreshFavoritePresentation();
  } catch (error) {
    favoriteMutation = null;
    favoriteMutationError = { key: mutationKey, message: String(error) };
    renderImageViewerFavorite(current.item);
  }
}

imageViewerFavorite.addEventListener("click", () => toggleImageViewerFavorite());
imageViewerVideo.addEventListener("click", () => {
  const current = locateImageViewerState(getImageViewerPrompts());
  if (!current || current.item.kind === "text" || !videoGeneration.available) return;
  openVideoDialog(
    current.item.jobId,
    current.item.generator,
    current.item.imageIndex,
    current.item.url,
    current.prompt.prompt);
});
imageViewerHide.addEventListener("click", () => hideCurrentViewerImage());

async function togglePromptFavorite(jobId) {
  const identity = promptFavoriteIdentity(jobId);
  if (!identity || favoriteMutation) return;
  const user = currentUsername();
  const mutationKey = favoriteMutationIdentity("prompt", identity);
  if (!user) {
    favoriteMutationError = {
      key: mutationKey,
      message: "Choose a 'creating as' username before favoriting.",
    };
    applyPromptFavoriteMarkers();
    return;
  }

  const existing = promptFavoriteItems.get(identity);
  const desired = !(existing && existing.users.includes(user));
  favoriteMutation = mutationKey;
  favoriteMutationError = null;
  applyPromptFavoriteMarkers();

  const form = new FormData();
  form.append("kind", "prompt");
  form.append("user", user);
  form.append("jobId", identity);
  form.append("favorite", String(desired));

  try {
    const response = await fetch(apiUrl("api/favorites"), { method: "POST", body: form });
    if (response.status === 401) { location.reload(); return; }
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
    if (body.kind !== "prompt") {
      throw new Error("favorite response kind did not match the requested prompt");
    }
    if (body.item) {
      const item = normalizePromptFavoriteItem(body.item);
      if (promptFavoriteIdentity(item.jobId) !== identity) {
        throw new Error("favorite response identity did not match the requested prompt");
      }
      promptFavoriteItems.set(identity, item);
    } else {
      promptFavoriteItems.delete(identity);
    }
    rebuildFavoriteUsers();
    favoritesSnapshotSignature =
      favoriteSnapshotSignature(favoriteItems, promptFavoriteItems);
    favoriteMutation = null;
    refreshFavoritePresentation();
  } catch (error) {
    favoriteMutation = null;
    favoriteMutationError = { key: mutationKey, message: String(error) };
    applyPromptFavoriteMarkers();
  }
}

// ---------- submit ----------

function checkedGeneratorKeys() {
  return allGeneratorInputs().filter((cb) => cb.checked).map((cb) => cb.value);
}

function isDescribeGenKey(key) {
  return (generators.find((g) => g.key === key) || {}).kind === "describe";
}

async function submit() {
  sendError.textContent = "";
  const prompt = promptBox.value.trim();
  const gens = checkedGeneratorKeys();
  if (gens.length === 0) { sendError.textContent = "pick at least one generator"; return; }
  const describeOnly = gens.every(isDescribeGenKey);
  // A describe-only job may omit the prompt: the standard describe instruction
  // is used instead (the server enforces the same substitution), so the card
  // shows exactly the instruction that went out.
  let effectivePrompt = prompt;
  if (!prompt) {
    if (!describeOnly) { sendError.textContent = "prompt is empty"; return; }
    effectivePrompt = describeConfig.defaultInstruction;
    if (!effectivePrompt) { sendError.textContent = "prompt is empty"; return; }
  }
  if (gens.some(isDescribeGenKey) && !hasInputImages()) {
    sendError.textContent = "describe endpoints need an attached image";
    return;
  }
  const user = currentUsername();
  if (!user) {
    sendError.textContent = "choose a username first (top of the page) — everything here is created under a name";
    usernameInput.focus();
    return;
  }

  const form = new FormData();
  form.append("prompt", effectivePrompt);
  form.append("user", user);
  form.append("generators", gens.join(","));
  form.append("shape", el("opt-shape").value);
  form.append("detail", el("opt-detail").value);
  form.append("quality", el("opt-quality").value);
  form.append("moderation", el("opt-moderation").value);
  form.append("n", el("opt-n").value);
  form.append("gpt2GuidanceEnabled", String(gpt2GuidanceEnabledBox.checked));
  form.append("gpt2GuidanceText", gpt2GuidanceTextBox.value);
  inputImageItems.forEach((item, index) => {
    const name = item.file.name && item.file.name.includes(".")
      ? item.file.name
      : `input${index}.png`;
    form.append("images", item.file, name);
  });

  sendBtn.disabled = true;
  try {
    const resp = await fetch(apiUrl("api/jobs"), { method: "POST", body: form });
    const body = await resp.json();
    if (!resp.ok) { sendError.textContent = body.error || `HTTP ${resp.status}`; return; }
    addJobCard(
      body.id,
      effectivePrompt,
      gens,
      inputImageItems.length > 0,
      Date.now(),
      inputImageItems.length,
      { user, canHide: !!authInfo.user });
  } catch (err) {
    sendError.textContent = String(err);
  } finally {
    sendBtn.disabled = false;
  }
}

sendBtn.addEventListener("click", submit);
promptBox.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
    e.preventDefault();
    submit();
  }
});

// ---------- fix spelling (Claude, spelling only) + undo ----------

// Browser spellcheck suggestions aren't scriptable, so "accept all" runs the
// prompt through a server-side Claude call constrained to spelling-only
// corrections. Undo restores the exact pre-fix text.
const spellfixBtn = el("spellfix");
const spellfixUndoBtn = el("spellfix-undo");

function applySpellfixAvailability() {
  spellfixBtn.disabled = !spellfix.available;
  if (!spellfix.available) {
    spellfixBtn.title = `Fix spelling is unavailable: ${spellfix.availabilityProblem || "not configured"}`;
  }
}

async function fixSpelling() {
  const original = promptBox.value;
  if (!original.trim()) {
    sendError.textContent = "prompt is empty";
    return;
  }
  sendError.textContent = "";
  spellfixBtn.disabled = true;
  const idleLabel = "fix spelling (Claude)";
  spellfixBtn.textContent = "fixing…";
  try {
    const form = new FormData();
    form.append("prompt", original);
    const resp = await fetch(apiUrl("api/prompt/spellfix"), { method: "POST", body: form });
    const body = await resp.json();
    if (!resp.ok) {
      sendError.textContent = body.error || `HTTP ${resp.status}`;
      return;
    }
    if (body.corrected === original) {
      spellfixBtn.textContent = "no changes";
      setTimeout(() => { spellfixBtn.textContent = idleLabel; }, 1600);
      return;
    }
    spellfixPrevious = original;
    promptBox.value = body.corrected;
    spellfixUndoBtn.hidden = false;
    if (mcpheeCtl) mcpheeCtl.refresh();
    updatePromptLimitNotice();
  } catch (err) {
    sendError.textContent = String(err);
  } finally {
    spellfixBtn.disabled = !spellfix.available;
    if (spellfixBtn.textContent === "fixing…") spellfixBtn.textContent = idleLabel;
  }
}

spellfixBtn.addEventListener("click", fixSpelling);
spellfixUndoBtn.addEventListener("click", () => {
  if (spellfixPrevious === null) return;
  promptBox.value = spellfixPrevious;
  spellfixPrevious = null;
  spellfixUndoBtn.hidden = true;
  // The report describes changes that no longer exist once undone.
  const report = el("spellfix-report");
  if (report) report.hidden = true;
  if (mcpheeCtl) mcpheeCtl.refresh();
  updatePromptLimitNotice();
  promptBox.focus();
});

// ---------- McPhee: local writing checks, corrections panel, and fixes ----------

// McPhee stays entirely in the browser: its verified overlay marks spelling,
// spacing, capitalization, and repetition issues, while its optional panel
// explains each finding without crowding the prompt field.
let mcphee = null;
let mcpheeCtl = null;
let mcpheePanel = null;
const spellfixLocalBtn = el("spellfix-local");
const mcpheeEnabledToggle = el("mcphee-enabled-toggle");
const mcpheePanelToggle = el("mcphee-panel-toggle");
const spellingPanelContainer = el("spelling-panel");
const mcpheePanelContainer = el("mcphee-panel");
const mcpheeEnabledStorageKey = "mic_mcphee_enabled";

// Provider/model jargon that appears in prompts constantly and must never
// light up as a misspelling.
const mcpheeJargon = [
  "grok", "xai", "recraft", "ideogram", "bfl", "gpt", "openai", "midjourney",
  "dalle", "webp", "png", "jpeg", "screenshot", "screenshots", "hyperrealistic",
  "photoreal", "photorealistic", "cinematic", "bokeh", "vaporwave", "cyberpunk",
];

async function initMcphee() {
  try {
    mcphee = await McPhee.create({
      affUrl: "mcphee/vendor/typo/en_US.aff",
      dicUrl: "mcphee/vendor/typo/en_US.dic",
      freqUrl: "mcphee/vendor/wordfreq/en-30k.txt",
      extraWords: mcpheeJargon,
      // Keep the established key so existing personal dictionaries carry
      // forward exactly through the SpellWell -> McPhee rename.
      customDictStorageKey: "mic_spellwell_custom_dict",
      profile: "standard",
    });
    if (!(mcphee.freqRank instanceof Map) || mcphee.freqRank.size < 10000) {
      throw new Error("McPhee frequency data is missing or malformed");
    }
    mcpheeCtl = mcphee.attach(promptBox);
    mcpheePanel = mcphee.attachPanel({
      textarea: promptBox,
      container: mcpheePanelContainer,
      controller: mcpheeCtl,
      formalityStorageKey: "mic_mcphee_formality",
      ruleOverridesStorageKey: "mic_mcphee_rule_overrides",
    });
    mcpheeEnabledToggle.disabled = false;
    mcpheePanelToggle.disabled = false;
    const storedEnabled = localStorage.getItem(mcpheeEnabledStorageKey);
    setMcpheeEnabled(storedEnabled !== "false", false);
  } catch (err) {
    // Missing or malformed McPhee data is a hard failure for the local tools;
    // the independent Claude spelling action remains available.
    console.error(err);
    spellfixLocalBtn.title = `Local fix unavailable: ${err.message || err}`;
    mcpheeEnabledToggle.title = `Spellchecker unavailable: ${err.message || err}`;
    mcpheePanelToggle.title = `Spellchecker unavailable: ${err.message || err}`;
  }
}

function setMcpheeEnabled(enabled, persist = true) {
  if (!mcpheeCtl) return;
  mcpheeCtl.setEnabled(enabled);
  mcpheeEnabledToggle.checked = enabled;
  mcpheePanelContainer.hidden = !enabled;
  spellfixLocalBtn.disabled = !enabled;
  if (enabled && !spellingPanelContainer.hidden) {
    mcpheeCtl.refresh(true);
    mcpheePanel.refresh();
  }
  if (persist) localStorage.setItem(mcpheeEnabledStorageKey, String(enabled));
}

mcpheeEnabledToggle.addEventListener("change", () => {
  if (!mcpheeCtl) return;
  setMcpheeEnabled(mcpheeEnabledToggle.checked);
});

mcpheePanelToggle.addEventListener("click", () => {
  if (!mcpheePanel) return;
  const willOpen = spellingPanelContainer.hidden;
  spellingPanelContainer.hidden = !willOpen;
  mcpheePanelToggle.setAttribute("aria-expanded", String(willOpen));
  if (willOpen && mcpheeEnabledToggle.checked) {
    mcpheeCtl.refresh(true);
    mcpheePanel.refresh();
  }
});

spellfixLocalBtn.addEventListener("click", () => {
  if (!mcphee) return;
  const original = promptBox.value;
  if (!original.trim()) {
    sendError.textContent = "prompt is empty";
    return;
  }
  sendError.textContent = "";
  const fix = mcphee.applyFixes(promptBox);
  const idleLabel = "auto-fix typos";
  if (!fix.applied) {
    spellfixLocalBtn.textContent = "no changes";
    setTimeout(() => { spellfixLocalBtn.textContent = idleLabel; }, 1600);
    return;
  }
  spellfixPrevious = original;
  spellfixUndoBtn.hidden = false;
  mcpheeCtl.refresh();
  if (!mcpheePanelContainer.hidden) mcpheePanel.refresh();
  updatePromptLimitNotice();
  // Persistent report, one change per line, visible until dismissed — so a
  // bad correction can't flash past unnoticed (undo fix restores everything).
  const lines = fix.wordChanges.map((c) => `${c.from} → ${c.to}`);
  if (fix.spaceRuns) lines.push(`${fix.spaceRuns} double space${fix.spaceRuns === 1 ? "" : "s"} collapsed`);
  const report = el("spellfix-report-lines");
  report.innerHTML = "";
  for (const line of lines) {
    const div = document.createElement("div");
    div.textContent = line;
    report.appendChild(div);
  }
  el("spellfix-report").hidden = false;
  promptBox.focus();
});

el("spellfix-report-close").addEventListener("click", () => {
  el("spellfix-report").hidden = true;
});

initMcphee();

// ---------- options help popover ----------

const optsHelpPanel = el("opts-help-panel");
const optsHelpToggle = el("opts-help-toggle");

function setOptsHelpOpen(open) {
  optsHelpPanel.hidden = !open;
  optsHelpToggle.setAttribute("aria-expanded", String(open));
}

optsHelpToggle.addEventListener("click", () => setOptsHelpOpen(optsHelpPanel.hidden));
el("opts-help-close").addEventListener("click", () => setOptsHelpOpen(false));
document.addEventListener("pointerdown", (event) => {
  if (!optsHelpPanel.hidden && !el("opts-help-control").contains(event.target)) setOptsHelpOpen(false);
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && !optsHelpPanel.hidden) setOptsHelpOpen(false);
});

// ---------- job cards + live events ----------

const genLabel = (key) => key === "grok-web-video"
  ? "grok-web video"
  : (generators.find((g) => g.key === key) || { label: key }).label;

function openVideoDialog(jobId, generator, index, url, sourcePrompt, videoOptions = {}) {
  videoSource = { jobId, generator, index, url };
  el("video-source-preview").src = url;
  el("video-prompt").value = sourcePrompt || "";
  el("video-mode").value = videoOptions.mode || "normal";
  el("video-duration").value = String(videoOptions.durationSeconds || 10);
  el("video-resolution").value = videoOptions.resolution || "480p";
  el("video-aspect").value = videoOptions.aspectRatio || "source";
  el("video-error").textContent = "";
  videoDialog.showModal();
  el("video-prompt").focus();
}

el("video-cancel").addEventListener("click", () => videoDialog.close());
videoDialog.addEventListener("click", (e) => {
  if (e.target === videoDialog) videoDialog.close();
});
el("video-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  if (!videoSource) return;

  const submitButton = el("video-submit");
  const error = el("video-error");
  const prompt = el("video-prompt").value.trim();
  const form = new FormData();
  form.append("sourceJobId", videoSource.jobId);
  form.append("sourceGenerator", videoSource.generator);
  form.append("sourceIndex", String(videoSource.index));
  form.append("prompt", prompt);
  form.append("user", currentUsername());
  form.append("mode", el("video-mode").value);
  form.append("duration", el("video-duration").value);
  form.append("resolution", el("video-resolution").value);
  form.append("aspectRatio", el("video-aspect").value);

  submitButton.disabled = true;
  error.textContent = "";
  try {
    const resp = await fetch(apiUrl("api/video-jobs"), { method: "POST", body: form });
    const body = await resp.json();
    if (!resp.ok) {
      error.textContent = body.error || `HTTP ${resp.status}`;
      return;
    }
    videoDialog.close();
    addJobCard(
      body.id,
      prompt,
      ["grok-web-video"],
      true,
      Date.now(),
      1,
      { user: currentUsername(), canHide: !!authInfo.user });
  } catch (err) {
    error.textContent = String(err);
  } finally {
    submitButton.disabled = false;
  }
});

// ---------- custom video player ----------

const VideoAudioStorageKey = "mic_video_audio_v1";
let sharedVideoVolume = 0.5;
let sharedVideoMuted = false;
try {
  const savedVideoAudio = JSON.parse(localStorage.getItem(VideoAudioStorageKey) || "{}");
  if (Number.isFinite(savedVideoAudio.volume)
    && savedVideoAudio.volume >= 0
    && savedVideoAudio.volume <= 1) {
    sharedVideoVolume = savedVideoAudio.volume;
  }
  if (typeof savedVideoAudio.muted === "boolean") {
    sharedVideoMuted = savedVideoAudio.muted;
  }
} catch {
  // A malformed browser-local value is ignored; the declared 50% unmuted
  // initial setting remains in force.
}

function applySharedVideoAudio() {
  for (const player of document.querySelectorAll(".custom-video-player")) {
    player.video.volume = sharedVideoVolume;
    player.video.muted = sharedVideoMuted;
    const volume = player.querySelector(".video-volume");
    const mute = player.querySelector(".video-mute");
    volume.value = String(sharedVideoVolume);
    mute.textContent = sharedVideoMuted || sharedVideoVolume === 0 ? "Unmute" : "Mute";
    mute.setAttribute("aria-label", mute.textContent + " video");
  }
}

function setSharedVideoAudio(volume, muted) {
  sharedVideoVolume = Math.max(0, Math.min(1, volume));
  sharedVideoMuted = muted;
  localStorage.setItem(VideoAudioStorageKey, JSON.stringify({
    volume: sharedVideoVolume,
    muted: sharedVideoMuted,
  }));
  applySharedVideoAudio();
}

function formatMediaTime(value) {
  if (!Number.isFinite(value)) return "0:00";
  const seconds = Math.max(0, Math.floor(value));
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(seconds % 60).padStart(2, "0")}`;
}

function setExactPixelMode(
  player,
  enabled = !player.classList.contains("exact-pixels"),
  shouldScrollIntoView = false) {
  const video = player.video;
  if (enabled && (!video.videoWidth || !video.videoHeight)) {
    video.addEventListener("loadedmetadata", () => setExactPixelMode(player, true), { once: true });
    video.load();
    return;
  }

  // CSS pixels are logical pixels. Dividing by devicePixelRatio maps each
  // source video pixel to one physical monitor pixel.
  const pixelRatio = Math.max(1, window.devicePixelRatio || 1);
  player.classList.toggle("exact-pixels", enabled);
  if (enabled) {
    player.style.width = `${video.videoWidth / pixelRatio}px`;
    video.style.width = `${video.videoWidth / pixelRatio}px`;
    video.style.height = `${video.videoHeight / pixelRatio}px`;
  } else {
    player.style.removeProperty("width");
    video.style.removeProperty("width");
    video.style.removeProperty("height");
  }

  const exactButton = player.querySelector(".video-exact");
  exactButton.textContent = enabled ? "Fit cell" : "1:1 pixels";
  exactButton.setAttribute(
    "aria-label",
    enabled ? "Fit video back into its result cell" : "Expand video to exact physical pixel size");

  const cell = player.closest(".cell");
  if (cell) {
    cell.classList.toggle("exact-video-cell", !!cell.querySelector(".custom-video-player.exact-pixels"));
  }
  if (enabled && shouldScrollIntoView) {
    player.scrollIntoView({ block: "nearest", inline: "nearest" });
  }
}

function createVideoPlayer(url, downloadFilename = "grok-video.mp4") {
  const player = document.createElement("div");
  player.className = "custom-video-player";
  player.tabIndex = 0;

  const video = document.createElement("video");
  video.src = url;
  video.preload = "metadata";
  video.playsInline = true;
  video.volume = sharedVideoVolume;
  video.muted = sharedVideoMuted;
  video.setAttribute("aria-label", "Generated video");
  player.video = video;

  const controls = document.createElement("div");
  controls.className = "video-player-controls";

  const play = document.createElement("button");
  play.type = "button";
  play.className = "video-play";
  play.textContent = "Play";
  play.setAttribute("aria-label", "Play video");

  const seek = document.createElement("input");
  seek.type = "range";
  seek.className = "video-seek";
  seek.min = "0";
  seek.max = "1000";
  seek.step = "1";
  seek.value = "0";
  seek.setAttribute("aria-label", "Video position");

  const time = document.createElement("span");
  time.className = "video-time";
  time.textContent = "0:00 / 0:00";

  const mute = document.createElement("button");
  mute.type = "button";
  mute.className = "video-mute";
  mute.textContent = "Mute";
  mute.setAttribute("aria-label", "Mute video");

  const volume = document.createElement("input");
  volume.type = "range";
  volume.className = "video-volume";
  volume.min = "0";
  volume.max = "1";
  volume.step = "0.05";
  volume.value = String(sharedVideoVolume);
  volume.setAttribute("aria-label", "Video volume");

  const resolution = document.createElement("span");
  resolution.className = "video-resolution";
  resolution.textContent = "loading…";

  controls.append(play, seek, time, mute, volume, resolution);

  const exact = document.createElement("button");
  exact.type = "button";
  exact.className = "video-exact";
  exact.textContent = "1:1 pixels";
  exact.setAttribute("aria-label", "Expand video to exact physical pixel size");
  exact.addEventListener("click", () => setExactPixelMode(player, undefined, true));
  controls.appendChild(exact);

  const fullscreen = document.createElement("button");
  fullscreen.type = "button";
  fullscreen.className = "video-fullscreen";
  fullscreen.textContent = "Fullscreen";
  fullscreen.setAttribute("aria-label", "Toggle fullscreen video");
  fullscreen.addEventListener("click", async () => {
    if (document.fullscreenElement === player) await document.exitFullscreen();
    else await player.requestFullscreen();
  });
  controls.appendChild(fullscreen);

  const save = document.createElement("a");
  save.className = "video-save";
  save.href = url;
  save.download = downloadFilename;
  save.textContent = "Save video";
  save.setAttribute("aria-label", "Save generated video");
  controls.appendChild(save);

  const syncPlayState = () => {
    const paused = video.paused;
    play.textContent = paused ? "Play" : "Pause";
    play.setAttribute("aria-label", paused ? "Play video" : "Pause video");
  };
  const togglePlay = () => {
    if (video.paused) video.play().catch(() => {});
    else video.pause();
  };
  const syncPosition = () => {
    seek.value = video.duration > 0
      ? String(Math.round(video.currentTime / video.duration * 1000))
      : "0";
    time.textContent = `${formatMediaTime(video.currentTime)} / ${formatMediaTime(video.duration)}`;
  };
  const syncMute = () => {
    mute.textContent = sharedVideoMuted || sharedVideoVolume === 0 ? "Unmute" : "Mute";
    mute.setAttribute("aria-label", mute.textContent + " video");
  };

  play.addEventListener("click", togglePlay);
  video.addEventListener("click", togglePlay);
  video.addEventListener("dblclick", () => fullscreen.click());
  video.addEventListener("play", syncPlayState);
  video.addEventListener("pause", syncPlayState);
  video.addEventListener("ended", syncPlayState);
  video.addEventListener("timeupdate", syncPosition);
  video.addEventListener("durationchange", syncPosition);
  video.addEventListener("loadedmetadata", () => {
    resolution.textContent = `${video.videoWidth}×${video.videoHeight}`;
    syncPosition();
    if (player.classList.contains("exact-pixels")) setExactPixelMode(player, true);
  });
  seek.addEventListener("input", () => {
    if (video.duration > 0) video.currentTime = Number(seek.value) / 1000 * video.duration;
  });
  mute.addEventListener("click", () => {
    setSharedVideoAudio(sharedVideoVolume, !sharedVideoMuted);
  });
  volume.addEventListener("input", () => {
    const nextVolume = Number(volume.value);
    setSharedVideoAudio(nextVolume, nextVolume === 0);
  });
  player.addEventListener("keydown", (event) => {
    if (event.target.matches("input, button, a")) return;
    if (event.key === " " || event.key.toLowerCase() === "k") {
      event.preventDefault();
      togglePlay();
    } else if (event.key === "ArrowLeft") {
      video.currentTime = Math.max(0, video.currentTime - 5);
    } else if (event.key === "ArrowRight") {
      video.currentTime = Math.min(video.duration || 0, video.currentTime + 5);
    } else if (event.key.toLowerCase() === "m") {
      mute.click();
    } else if (event.key.toLowerCase() === "f") {
      fullscreen.click();
    } else if (event.key === "1") {
      setExactPixelMode(player);
    }
  });

  player.append(video, controls);
  syncMute();
  setExactPixelMode(player, true);
  return player;
}

window.addEventListener("resize", () => {
  for (const player of document.querySelectorAll(".custom-video-player.exact-pixels")) {
    setExactPixelMode(player, true);
  }
});

// ---------- generated-image viewer ----------

function getImageViewerPrompts() {
  const prompts = [];
  // Normal mode walks live jobs then expanded archive days. Favorites mode
  // walks only the dedicated filtered favorites gallery, so the same image
  // cannot appear twice when its original job card is also loaded.
  const cardSelector = favoriteBrowseUser === null
    ? "#jobs .job, #archive .job"
    : "#favorites-grid .favorite-gallery-card";
  for (const card of document.querySelectorAll(cardSelector)) {
    // Night-hidden and person-filtered jobs are invisible to the viewer's
    // keyboard walk too, as are jobs inside a collapsed archive day.
    if (card.classList.contains("night-hidden")) continue;
    if (card.classList.contains("user-filter-hidden")) continue;
    const dayContainer = card.closest(".archive-day-jobs");
    if (dayContainer && dayContainer.hidden) continue;
    const jobId = card.dataset.jobId || card.id.substring("job-".length);
    const items = [...card.querySelectorAll('a[data-viewer-image="true"]')].map((link) => ({
      jobId,
      generator: link.dataset.generator,
      imageIndex: Number(link.dataset.imageIndex),
      generatorCount: Number(link.dataset.generatorCount),
      url: link.href,
      // Describe results ride the same walk: kind "text" items point their
      // url at the described INPUT image and carry the description text plus
      // the model's separated meta comments.
      kind: link.dataset.resultKind || "image",
      describeText: link.dataset.describeText || "",
      describeComments: link.dataset.describeComments || "",
    }));
    if (items.length === 0) continue;
    prompts.push({
      jobId,
      prompt: card.querySelector(".job-prompt").textContent,
      hasInput: card.dataset.hasInputImage === "true",
      gpt2Guidance: card.dataset.gpt2Guidance || "",         // "sent" | "off" | "" (unknown)
      gpt2GuidanceText: card.dataset.gpt2GuidanceText || "",
      items,
    });
  }
  return prompts;
}

function getImageViewerFlatItems() {
  return getImageViewerPrompts().flatMap((prompt) => prompt.items);
}

function locateImageViewerState(prompts) {
  if (!imageViewerState) return null;
  const promptIndex = prompts.findIndex((prompt) => prompt.jobId === imageViewerState.jobId);
  if (promptIndex < 0) return null;
  const itemIndex = prompts[promptIndex].items.findIndex((item) =>
    item.generator === imageViewerState.generator &&
    item.imageIndex === imageViewerState.imageIndex);
  if (itemIndex < 0) return null;
  return { promptIndex, itemIndex, prompt: prompts[promptIndex], item: prompts[promptIndex].items[itemIndex] };
}

function loadViewerSeen() {
  try {
    const saved = JSON.parse(localStorage.getItem(ViewerSeenKey) || "[]");
    return new Set(Array.isArray(saved) ? saved.filter((k) => typeof k === "string") : []);
  } catch {
    return new Set();
  }
}

function viewerSeenKeyFor(jobId, generator, imageIndex) {
  return `${jobId}|${generator}|${imageIndex}`;
}

// The outer-page thumbnail anchor for a viewer item/state, matched by exact
// identity (job id + generator + image index) — never by position.
function findViewerAnchor(ref) {
  if (!ref) return null;
  const scope = favoriteBrowseUser === null
    ? document.querySelectorAll("#jobs a[data-viewer-image='true'], #archive a[data-viewer-image='true']")
    : document.querySelectorAll("#favorites-grid a[data-viewer-image='true']");
  for (const link of scope) {
    if (link.dataset.jobId !== ref.jobId) continue;
    if (link.dataset.generator === ref.generator &&
        Number(link.dataset.imageIndex) === Number(ref.imageIndex)) {
      return link;
    }
  }
  return null;
}

// Called whenever a frame actually paints in the viewer: records the image as
// seen (persisted per-browser) and marks its card thumbnail immediately.
function markImageViewed(item) {
  const key = viewerSeenKeyFor(item.jobId, item.generator, item.imageIndex);
  if (!viewerSeenSet.has(key)) {
    viewerSeenSet.add(key);
    // Set preserves insertion order, so the front is the oldest mark.
    while (viewerSeenSet.size > ViewerSeenCap) {
      viewerSeenSet.delete(viewerSeenSet.values().next().value);
    }
    try {
      localStorage.setItem(ViewerSeenKey, JSON.stringify([...viewerSeenSet]));
    } catch (error) {
      console.warn("could not persist viewer-seen marks", error);
    }
  }
  const link = findViewerAnchor(item);
  if (link) link.classList.add("viewer-seen");
}

// Departure pulse: the thumbnail of the image the user just closed out of
// glows briefly so their eye lands on it — in both handback modes.
function pulseViewerAnchor(link) {
  link.classList.remove("viewer-return-pulse");
  void link.offsetWidth; // restart the animation when re-closing onto the same image
  link.classList.add("viewer-return-pulse");
  link.addEventListener("animationend",
    () => link.classList.remove("viewer-return-pulse"), { once: true });
}

function sortImageViewerPreloadWaiters() {
  imageViewerPreloadWaiters.sort((a, b) => a.priority - b.priority);
}

function enqueueImageViewerPreloadWaiter(priority, entry) {
  return new Promise((resolve, reject) => {
    const waiter = { priority, resolve, reject, entry };
    entry.waiter = waiter;
    imageViewerPreloadWaiters.push(waiter);
    sortImageViewerPreloadWaiters();
  });
}

async function takeImageViewerPreloadSlot(priority, entry) {
  if (imageViewerPreloadActive < ImageViewerPreloadConcurrency) {
    imageViewerPreloadActive++;
    entry.waiter = null;
    return;
  }
  await enqueueImageViewerPreloadWaiter(priority, entry);
  entry.waiter = null;
}

function releaseImageViewerPreloadSlot() {
  const next = imageViewerPreloadWaiters.shift();
  if (next) {
    if (next.entry) next.entry.waiter = null;
    next.resolve();
    return;
  }
  imageViewerPreloadActive = Math.max(0, imageViewerPreloadActive - 1);
}

function bumpImageViewerEntryPriority(entry, priority) {
  if (!(priority < entry.priority)) return;
  entry.priority = priority;
  if (!entry.waiter) return;
  entry.waiter.priority = priority;
  sortImageViewerPreloadWaiters();
}

function discardImageViewerCacheEntry(url, entry) {
  if (imageViewerCache.get(url) !== entry) return;
  imageViewerCache.delete(url);
  if (entry.waiter) {
    const idx = imageViewerPreloadWaiters.indexOf(entry.waiter);
    if (idx >= 0) imageViewerPreloadWaiters.splice(idx, 1);
    const waiter = entry.waiter;
    entry.waiter = null;
    // Never acquired a slot — reject so the async body does not pretend it
    // holds concurrency and call release.
    waiter.reject(new DOMException("Image preload aborted", "AbortError"));
  }
  if (entry.controller) entry.controller.abort();
  if (entry.blobUrl) {
    URL.revokeObjectURL(entry.blobUrl);
    return;
  }
  entry.promise
    .then(() => {
      if (entry.blobUrl) URL.revokeObjectURL(entry.blobUrl);
    })
    .catch(() => {});
}

function imageViewerInputUrl(jobId) {
  return apiUrl(`api/jobs/${encodeURIComponent(jobId)}/images/input/0`);
}

function loadImageViewerEntry(url, priority) {
  const existing = imageViewerCache.get(url);
  if (existing) {
    bumpImageViewerEntryPriority(existing, priority);
    return existing;
  }

  const controller = new AbortController();
  const entry = {
    promise: null,
    blobUrl: null,
    image: null,
    controller,
    priority,
    waiter: null,
    acquired: false,
  };
  entry.promise = (async () => {
    await takeImageViewerPreloadSlot(entry.priority, entry);
    entry.acquired = true;
    try {
      if (controller.signal.aborted) {
        throw new DOMException("Image preload aborted", "AbortError");
      }
      const response = await fetch(url, { signal: controller.signal });
      if (!response.ok) throw new Error(`image preload returned HTTP ${response.status}`);
      const blob = await response.blob();
      if (!blob.type.startsWith("image/")) {
        throw new Error(`image preload returned ${blob.type || "an unknown content type"}`);
      }

      // Free the network slot before decode so neighbors can fetch while
      // this frame's pixels land in the decoder.
      entry.acquired = false;
      releaseImageViewerPreloadSlot();

      entry.blobUrl = URL.createObjectURL(blob);
      entry.image = new Image();
      entry.image.src = entry.blobUrl;
      await entry.image.decode();
      return entry;
    } finally {
      if (entry.acquired) {
        entry.acquired = false;
        releaseImageViewerPreloadSlot();
      }
    }
  })().catch((error) => {
    if (entry.blobUrl) URL.revokeObjectURL(entry.blobUrl);
    if (imageViewerCache.get(url) === entry) imageViewerCache.delete(url);
    throw error;
  });
  imageViewerCache.set(url, entry);
  return entry;
}

// Build the ±10 preload window. Priority bands (lower = sooner):
//   0     current output
//   1     current input (compare mode)
//   2+d   ahead-of-travel neighbor at distance d
//   1000+d behind-travel neighbor at distance d
// Travel bias (imageViewerNavDelta) only reorders inside the window — both
// sides stay warm so a reverse turn does not hitch. The render path never
// clears the previous decoded frame until the next blob is ready.
function prepareImageViewerWindow(prompts, current) {
  const allItems = prompts.flatMap((prompt) => prompt.items);
  const currentIndex = allItems.findIndex((item) =>
    item.jobId === current.item.jobId &&
    item.generator === current.item.generator &&
    item.imageIndex === current.item.imageIndex);
  const aheadSign = imageViewerNavDelta < 0 ? -1 : 1;
  const promptByJobId = new Map(prompts.map((p) => [p.jobId, p]));
  const wantedUrls = new Set();
  const schedule = [];

  const want = (url, priority) => {
    wantedUrls.add(url);
    schedule.push({ url, priority });
  };

  const consider = (index, priority) => {
    if (index < 0 || index >= allItems.length) return;
    const item = allItems[index];
    want(item.url, priority);
    if (!imageViewerCompareInput) return;
    const prompt = promptByJobId.get(item.jobId);
    if (prompt && prompt.hasInput) want(imageViewerInputUrl(item.jobId), priority + 0.5);
  };

  want(current.item.url, 0);
  if (imageViewerCompareInput && current.prompt.hasInput) {
    want(imageViewerInputUrl(current.item.jobId), 1);
  }
  // Everything before this index is the visible presentation; everything
  // after is speculative runway.
  const immediateCount = schedule.length;

  for (let distance = 1; distance <= ImageViewerPreloadAhead; distance++) {
    consider(currentIndex + aheadSign * distance, 2 + distance);
  }
  for (let distance = 1; distance <= ImageViewerPreloadBehind; distance++) {
    consider(currentIndex - aheadSign * distance, 1000 + distance);
  }

  for (const [url, entry] of imageViewerCache) {
    if (!wantedUrls.has(url)) discardImageViewerCacheEntry(url, entry);
  }

  // Stable order: first occurrence of each URL keeps the best (lowest) priority.
  const seen = new Set();
  const startSlice = (from, to) => {
    let currentEntry = null;
    for (const { url, priority } of schedule.slice(from, to)) {
      if (seen.has(url)) continue;
      seen.add(url);
      const entry = loadImageViewerEntry(url, priority);
      if (url === current.item.url) currentEntry = entry;
      else entry.promise.catch(() => {});
    }
    return currentEntry;
  };

  // The visible image (and its compare input) fetches ALONE. On a cold open,
  // starting the runway at the same time splits the ~6-socket HTTP/1.1 pool
  // and the server uplink across up to ImageViewerPreloadConcurrency multi-MB
  // originals, multiplying the wait for the one image the user is staring at
  // (observed 2026-08-05 as "first image very slow, rest instant"). Neighbors
  // start only once the current frame has fetched+decoded — during warm
  // navigation the current entry is already decoded, so the runway still
  // schedules immediately and scrubbing stays hitch-free.
  const currentEntry = startSlice(0, immediateCount);
  const startNeighbors = () => startSlice(immediateCount, schedule.length);
  if (currentEntry.blobUrl && currentEntry.image) {
    startNeighbors();
  } else {
    const version = imageViewerRenderVersion;
    const later = () => {
      // A newer render's own prepare owns the window now; a closed viewer
      // wants no fetches at all.
      if (version !== imageViewerRenderVersion || imageViewer.hidden) return;
      startNeighbors();
    };
    currentEntry.promise.then(later, later);
  }
  return currentEntry;
}

function showImageViewerEntry(current, entry) {
  imageViewerImage.src = entry.blobUrl;
  imageViewerImage.alt = current.item.kind === "text"
    ? `input image ${current.item.imageIndex + 1} of ${current.item.generatorCount} described by ${current.item.generator}`
    : `${current.item.generator} image ${current.item.imageIndex + 1} of ${current.item.generatorCount}`;
  imageViewerDimensions.textContent = `${entry.image.naturalWidth}×${entry.image.naturalHeight}`;
  imageViewerContentAr = entry.image.naturalWidth / entry.image.naturalHeight;
  fitImageViewerWindow();
}

function setImageViewerIdentity(item) {
  imageViewerState = {
    jobId: item.jobId,
    generator: item.generator,
    imageIndex: item.imageIndex,
  };
  renderImageViewer();
}

// Left pane of the `c` comparison: only swap onto a decoded preload blob.
// Never point at a raw network URL (that would paint an unloading/partial
// frame). Until the blob lands, keep the previous input pixels up.
function applyImageViewerCompare(current) {
  // Describe items already show the input image as the stage; comparing it
  // with itself is meaningless, so the mode stays armed but paints nothing.
  const active = !!current && imageViewerCompareInput && current.prompt.hasInput
    && current.item.kind !== "text";
  imageViewerStage.classList.toggle("compare", active);
  imageViewerInputImage.hidden = !active;
  imageViewerInputLabel.hidden = !active;
  imageViewerOutputLabel.hidden = !active;
  if (!active) {
    imageViewerInputImage.removeAttribute("src");
    return;
  }
  const inputUrl = imageViewerInputUrl(current.item.jobId);
  const cached = imageViewerCache.get(inputUrl);
  if (cached && cached.blobUrl) {
    if (imageViewerInputImage.getAttribute("src") !== cached.blobUrl) {
      imageViewerInputImage.src = cached.blobUrl;
    }
  }
}

function toggleImageViewerCompare() {
  imageViewerCompareInput = !imageViewerCompareInput;
  localStorage.setItem("imageViewerCompareInput", String(imageViewerCompareInput));
  renderImageViewer();
}

// Below the prompt, state plainly whether the anti-murk guidance rode along
// with the viewed gpt2 image. Green = the exact appended text; red = a gpt2
// image that went out WITHOUT it. Silence here once hid a two-day stretch of
// guidance-free (ultra-dark) generations, so absence is now a visible state.
function renderImageViewerGuidance(current) {
  const state = current && current.item.generator === "gpt2"
    ? current.prompt.gpt2Guidance
    : "";
  if (state === "sent") {
    imageViewerGuidance.hidden = false;
    imageViewerGuidance.className = "sent";
    imageViewerGuidance.textContent = `+ gpt-image-2 guidance: ${current.prompt.gpt2GuidanceText}`;
  } else if (state === "off") {
    imageViewerGuidance.hidden = false;
    imageViewerGuidance.className = "off";
    imageViewerGuidance.textContent = "anti-murk guidance was NOT sent with this image";
  } else {
    imageViewerGuidance.hidden = true;
    imageViewerGuidance.className = "";
    imageViewerGuidance.textContent = "";
  }
}

function imageViewerIdentityMatches(item) {
  return !!item && !!imageViewerState &&
    imageViewerState.jobId === item.jobId &&
    imageViewerState.generator === item.generator &&
    Number(imageViewerState.imageIndex) === Number(item.imageIndex);
}

function renderImageViewerActiveActions(item) {
  imageViewerActiveActions.hidden = !item;
  for (const button of [imageViewerSetImage, imageViewerSetImagePrompt]) {
    button.disabled = false;
    button.classList.remove("pending", "error", "success");
  }
  imageViewerSetImage.textContent = "set image active";
  imageViewerSetImage.title =
    "Replace the composer input with this image; keep the current composer prompt";
  imageViewerSetImagePrompt.textContent = "set image + prompt active";
  imageViewerSetImagePrompt.title =
    "Replace the composer input with this image and replace the composer prompt with this image's prompt";
}

// These controls intentionally set only the fields they name. Both replace
// every current composer image with the exact viewed original; the second also
// copies this item's prompt. Generator selection and output options remain
// untouched. A later click supersedes an earlier in-flight fetch by identity.
async function setViewedImageActive(includePrompt) {
  const current = locateImageViewerState(getImageViewerPrompts());
  if (!current) return;

  const item = { ...current.item };
  const prompt = current.prompt.prompt;
  const selectedButton = includePrompt ? imageViewerSetImagePrompt : imageViewerSetImage;
  const operationVersion = ++imageViewerActivationVersion;
  if (imageViewerActivationController) imageViewerActivationController.abort();
  const controller = new AbortController();
  imageViewerActivationController = controller;

  for (const button of [imageViewerSetImage, imageViewerSetImagePrompt]) {
    button.disabled = true;
    button.classList.remove("error", "success");
    button.classList.add("pending");
  }
  selectedButton.textContent = "setting…";

  try {
    const response = await fetch(apiUrl(item.url), { signal: controller.signal });
    if (!response.ok) throw new Error(`image fetch returned HTTP ${response.status}`);
    const blob = await response.blob();
    if (!blob.type.startsWith("image/")) {
      throw new Error(`image fetch returned ${blob.type || "an unknown content type"}`);
    }
    if (operationVersion !== imageViewerActivationVersion) return;

    await setImagesFromBlobs([blob]);
    if (includePrompt) {
      promptBox.value = prompt;
      if (mcpheeCtl) mcpheeCtl.refresh();
      if (mcpheePanel && !mcpheePanelContainer.hidden) mcpheePanel.refresh();
    }
    sendError.textContent = "";

    if (imageViewerIdentityMatches(item)) {
      selectedButton.classList.remove("pending");
      selectedButton.classList.add("success");
      selectedButton.textContent = includePrompt ? "image + prompt active" : "image active";
      selectedButton.title = includePrompt
        ? "This image and prompt are now active in the composer"
        : "This image is now active in the composer; the composer prompt was left unchanged";
    }
  } catch (error) {
    if (error && error.name === "AbortError") return;
    if (operationVersion !== imageViewerActivationVersion) return;
    if (imageViewerIdentityMatches(item)) {
      selectedButton.classList.remove("pending");
      selectedButton.classList.add("error");
      selectedButton.textContent = "set active failed";
      selectedButton.title = String(error);
    }
  } finally {
    if (operationVersion !== imageViewerActivationVersion) return;
    imageViewerActivationController = null;
    if (!imageViewerIdentityMatches(item)) return;
    for (const button of [imageViewerSetImage, imageViewerSetImagePrompt]) {
      button.disabled = false;
      button.classList.remove("pending");
    }
    setTimeout(() => {
      if (operationVersion === imageViewerActivationVersion &&
          imageViewerIdentityMatches(item)) {
        renderImageViewerActiveActions(item);
      }
    }, 1800);
  }
}

imageViewerSetImage.addEventListener("click", () => setViewedImageActive(false));
imageViewerSetImagePrompt.addEventListener("click", () => setViewedImageActive(true));

// Item-specific chrome (prompt, guidance, describe panel, generator name).
// Callers pair this with same-item stage pixels only — never with another
// item's media.
function paintImageViewerChrome(target) {
  imageViewerPrompt.textContent = target.prompt.prompt;
  renderImageViewerGuidance(target);
  renderImageViewerActiveActions(target.item);
  renderImageViewerFavorite(target.item);
  renderImageViewerVideo(target.item);
  renderImageViewerHide(target.item);
  renderImageViewerPosition(target.item);
  // Describe items: the stage IS the submitted image; the panel above the
  // prompt carries the returned description (plus the model's separated
  // comments, when any), and the header says so.
  const isText = target.item.kind === "text";
  imageViewerDescribe.hidden = !isText;
  imageViewerDescribe.replaceChildren();
  if (isText) {
    const descriptionDiv = document.createElement("div");
    descriptionDiv.textContent = target.item.describeText;
    imageViewerDescribe.appendChild(descriptionDiv);
    if (target.item.describeComments) {
      const commentsDiv = document.createElement("div");
      commentsDiv.className = "viewer-describe-comments";
      commentsDiv.textContent = `model comments: ${target.item.describeComments}`;
      imageViewerDescribe.appendChild(commentsDiv);
    }
  }
  imageViewerGenerator.textContent = isText
    ? `${genLabel(target.item.generator)} — description of the submitted image`
    : genLabel(target.item.generator);
}

// Cold-open loading state: the pending target's OWN card thumbnail at stage
// size, with its own chrome — it explicitly identifies the image being
// fetched, so nothing stale is implied. The dimensions slot reports that the
// full-resolution bytes are still on their way; the compare split and the
// "seen" mark wait for the real frame. The thumbnail is only used when the
// clicked card already has it decoded (it always does — the user clicked it).
function paintImageViewerColdOpenPreview(current) {
  const anchor = findViewerAnchor(current.item);
  const thumb = anchor ? anchor.querySelector("img") : null;
  if (!thumb || !thumb.complete || !(thumb.naturalWidth > 0)) return;
  imageViewerImage.src = thumb.currentSrc || thumb.src;
  imageViewerImage.alt = `loading full resolution of ${current.item.generator} image ${current.item.imageIndex + 1}`;
  paintImageViewerChrome(current);
  imageViewerDimensions.textContent = "loading full resolution…";
  // The thumb keeps the original's aspect ratio, so the window shrink-wraps
  // to its final geometry immediately instead of jumping after the swap.
  imageViewerContentAr = thumb.naturalWidth / thumb.naturalHeight;
  fitImageViewerWindow();
}

// A hidden viewer is not allowed to retain presentable content. A later open
// may wait on the network/decode path, and exposing the old image's chrome
// during that wait falsely associates it with the newly selected image.
function clearImageViewerPresentation() {
  imageViewerImage.removeAttribute("src");
  imageViewerImage.alt = "";
  imageViewerPrompt.textContent = "";
  imageViewerDescribe.hidden = true;
  imageViewerDescribe.textContent = "";
  renderImageViewerGuidance(null);
  imageViewerGenerator.textContent = "";
  imageViewerDimensions.textContent = "";
  renderImageViewerPosition(null);
  renderImageViewerActiveActions(null);
  renderImageViewerFavorite(null);
  renderImageViewerVideo(null);
  renderImageViewerHide(null);
  imageViewerContentAr = null;
  applyImageViewerCompare(null);
}

async function renderImageViewer() {
  if (imageViewer.hidden || !imageViewerState) return;
  const prompts = getImageViewerPrompts();
  const current = locateImageViewerState(prompts);
  if (!current) {
    imageViewerImage.removeAttribute("src");
    imageViewerPrompt.textContent = "";
    imageViewerDescribe.hidden = true;
    imageViewerDescribe.textContent = "";
    renderImageViewerGuidance(null);
    renderImageViewerActiveActions(null);
    renderImageViewerFavorite(null);
    renderImageViewerVideo(null);
    renderImageViewerHide(null);
    imageViewerGenerator.textContent = "selected image is no longer available";
    imageViewerDimensions.textContent = "";
    renderImageViewerPosition(null);
    applyImageViewerCompare(null);
    return;
  }

  const version = ++imageViewerRenderVersion;
  // Kick the deep ahead runway immediately. Pixels + chrome only advance
  // together onto a fully decoded frame — never blank, never "loading…",
  // never a new prompt sitting on the previous image.
  const currentEntry = prepareImageViewerWindow(prompts, current);

  const paint = (entry) => {
    if (version !== imageViewerRenderVersion || imageViewer.hidden) return false;
    const latest = locateImageViewerState(getImageViewerPrompts());
    if (!latest ||
        latest.item.jobId !== current.item.jobId ||
        latest.item.generator !== current.item.generator ||
        latest.item.imageIndex !== current.item.imageIndex) return false;
    paintImageViewerChrome(latest);
    showImageViewerEntry(latest, entry);
    applyImageViewerCompare(latest);
    markImageViewed(latest.item);
    if (imageViewerCompareInput && latest.prompt.hasInput) {
      const inputEntry = imageViewerCache.get(imageViewerInputUrl(latest.item.jobId));
      if (inputEntry && !inputEntry.blobUrl) {
        inputEntry.promise.then(() => {
          if (version !== imageViewerRenderVersion || imageViewer.hidden) return;
          const still = locateImageViewerState(getImageViewerPrompts());
          if (still) {
            applyImageViewerCompare(still);
            fitImageViewerWindow();
          }
        }).catch(() => {});
      }
    }
    return true;
  };

  if (currentEntry.blobUrl && currentEntry.image) {
    paint(currentEntry);
    return;
  }

  // Cold open: the stage was cleared while the viewer was hidden, so there is
  // no previous coherent frame to keep up — the user would stare at an empty
  // window for the whole full-resolution download (multi-MB originals take
  // seconds on a remote deployment). Paint the SAME item's card thumbnail
  // (already in the browser cache — it is the picture that was just clicked)
  // together with its chrome, then swap to the full-resolution frame when it
  // decodes. Warm navigation (stage occupied) keeps the previous frame.
  if (!imageViewerImage.getAttribute("src")) {
    paintImageViewerColdOpenPreview(current);
  }

  try {
    const entry = await currentEntry.promise;
    paint(entry);
  } catch (error) {
    if (version !== imageViewerRenderVersion || imageViewer.hidden) return;
    if (error && error.name === "AbortError") return;
    // Keep the previous fully-loaded frame on screen; only the status line
    // reports the miss.
    imageViewerDimensions.textContent = `load failed: ${error}`;
  }
}

function navigateImageViewerImage(delta) {
  const allItems = getImageViewerFlatItems();
  const prompts = getImageViewerPrompts();
  const current = locateImageViewerState(prompts);
  if (!current) return;
  const currentIndex = allItems.findIndex((item) =>
    item.jobId === current.item.jobId &&
    item.generator === current.item.generator &&
    item.imageIndex === current.item.imageIndex);
  if (currentIndex < 0) return;
  const targetIndex = currentIndex + delta;
  if (targetIndex < 0 || targetIndex >= allItems.length) return;
  imageViewerNavDelta = Math.sign(delta);
  setImageViewerIdentity(allItems[targetIndex]);
}

function navigateImageViewerAbsolute(index) {
  const allItems = getImageViewerFlatItems();
  if (allItems.length === 0) return;
  const targetIndex = index < 0 ? allItems.length - 1 : index;
  if (targetIndex < 0 || targetIndex >= allItems.length) return;
  imageViewerNavDelta = 0;
  setImageViewerIdentity(allItems[targetIndex]);
}

function findImageViewerFlatIndex(allItems, item) {
  if (!item) return -1;
  return allItems.findIndex((candidate) =>
    candidate.jobId === item.jobId &&
    candidate.generator === item.generator &&
    candidate.imageIndex === item.imageIndex);
}

// Place-in-list indicator: a stable, 1-based current / total reading for the
// exact visible walk. It is painted with the same item-specific chrome as the
// decoded media, so the number can never advance ahead of the pixels.
function renderImageViewerPosition(item) {
  const allItems = item ? getImageViewerFlatItems() : [];
  const index = findImageViewerFlatIndex(allItems, item);
  if (index < 0) {
    imageViewerPosition.textContent = "";
    imageViewerPosition.removeAttribute("aria-label");
    return;
  }
  imageViewerPosition.textContent = `${index + 1} / ${allItems.length}`;
  imageViewerPosition.setAttribute(
    "aria-label",
    `item ${index + 1} of ${allItems.length} in the visible gallery`);
}

// Halfway jump: move halfway through the remaining distance to an endpoint.
// Repetition converges exponentially (1/2, 3/4, 7/8...) while floor/ceil make
// the final press reach the exact first/last item instead of stalling beside it.
function navigateImageViewerHalfway(direction) {
  const allItems = getImageViewerFlatItems();
  const prompts = getImageViewerPrompts();
  const current = locateImageViewerState(prompts);
  if (!current || allItems.length === 0) return;
  const currentIndex = findImageViewerFlatIndex(allItems, current.item);
  if (currentIndex < 0) return;
  const lastIndex = allItems.length - 1;
  const targetIndex = direction < 0
    ? Math.floor(currentIndex / 2)
    : Math.ceil((currentIndex + lastIndex) / 2);
  if (targetIndex === currentIndex) return;
  imageViewerNavDelta = Math.sign(direction);
  setImageViewerIdentity(allItems[targetIndex]);
}

function navigateImageViewerPrompt(delta) {
  const prompts = getImageViewerPrompts();
  const current = locateImageViewerState(prompts);
  if (!current) return;
  const targetPrompt = prompts[current.promptIndex + delta];
  // At either boundary, keep the current prompt and return to its first image.
  // Prompt navigation never guesses a different destination.
  imageViewerNavDelta = Math.sign(delta);
  setImageViewerIdentity((targetPrompt || current.prompt).items[0]);
}

function hideImageViewerHelp() {
  imageViewerHelpOpen = false;
  imageViewerHelp.hidden = true;
}

function showImageViewerHelp() {
  imageViewerHelpList.textContent = "";
  for (const command of ImageViewerCommands) {
    // help may be a function so entries can report live state (e.g. the
    // close-handback toggle shows its current ON/OFF).
    const helpText = typeof command.help === "function" ? command.help() : command.help;
    if (!helpText) continue;
    const item = document.createElement("li");
    const keys = document.createElement("kbd");
    keys.textContent = command.keys.join(" / ");
    const label = document.createElement("span");
    label.className = "image-viewer-help-command";
    const name = document.createElement("strong");
    name.textContent = command.name;
    const explanation = document.createElement("span");
    explanation.textContent = helpText;
    label.append(name, explanation);
    item.appendChild(keys);
    item.appendChild(label);
    imageViewerHelpList.appendChild(item);
  }
  imageViewerHelpOpen = true;
  imageViewerHelp.hidden = false;
}

function toggleImageViewerHelp() {
  if (imageViewerHelpOpen) hideImageViewerHelp();
  else showImageViewerHelp();
}

const ImageViewerCommands = [
  {
    id: "positionIndicator",
    keys: ["Always visible"],
    name: "Place-in-list indicator",
    match: () => false,
    help: "The 1-based current / total count identifies your exact place in the visible gallery.",
    run: () => {},
  },
  {
    id: "wheelStep",
    keys: ["Wheel"],
    name: "Intent-filtered wheel stepping",
    match: () => false,
    help: "Vertical wheel movement advances one image only after a threshold; trackpad noise and horizontal gestures do not skip through the list.",
    run: () => {},
  },
  {
    id: "mouseSideStep",
    keys: ["Mouse Back", "Mouse Forward"],
    name: "Mouse side-button stepping",
    match: () => false,
    help: "A mouse's Back or Forward thumb button selects the previous or next image without changing browser history.",
    run: () => {},
  },
  {
    id: "previous",
    keys: ["Left", "Up"],
    name: "Single-step traversal",
    match: (event) =>
      !event.ctrlKey && !event.metaKey &&
      (event.key === "ArrowLeft" || event.key === "ArrowUp"),
    help: "Select the previous image in the visible gallery, crossing prompt boundaries.",
    run: () => navigateImageViewerImage(-1),
  },
  {
    id: "next",
    keys: ["Right", "Down"],
    name: "Single-step traversal",
    match: (event) =>
      !event.ctrlKey && !event.metaKey &&
      (event.key === "ArrowRight" || event.key === "ArrowDown"),
    help: "Select the next image in the visible gallery, crossing prompt boundaries.",
    run: () => navigateImageViewerImage(1),
  },
  {
    id: "newerPrompt",
    keys: ["Ctrl+Left"],
    name: "Prompt-boundary jump",
    match: (event) =>
      (event.ctrlKey || event.metaKey) && event.key === "ArrowLeft",
    help: "Select the first image belonging to the newer prompt.",
    run: () => navigateImageViewerPrompt(-1),
  },
  {
    id: "olderPrompt",
    keys: ["Ctrl+Right"],
    name: "Prompt-boundary jump",
    match: (event) =>
      (event.ctrlKey || event.metaKey) && event.key === "ArrowRight",
    help: "Select the first image belonging to the older prompt.",
    run: () => navigateImageViewerPrompt(1),
  },
  {
    id: "pageBack",
    keys: ["PageUp"],
    name: "Fixed-distance jump",
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "PageUp",
    help: `Move ${ImageViewerPageJumpSize} images toward the beginning of the visible gallery.`,
    run: () => navigateImageViewerImage(-ImageViewerPageJumpSize),
  },
  {
    id: "pageForward",
    keys: ["PageDown"],
    name: "Fixed-distance jump",
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "PageDown",
    help: `Move ${ImageViewerPageJumpSize} images toward the end of the visible gallery.`,
    run: () => navigateImageViewerImage(ImageViewerPageJumpSize),
  },
  {
    id: "halfwayBack",
    keys: ["Ctrl+PageUp"],
    name: "Halfway jump",
    match: (event) =>
      (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey &&
      event.key === "PageUp",
    help: "Move halfway through the remaining distance to the first item; repeated presses rapidly narrow a long gallery.",
    run: () => navigateImageViewerHalfway(-1),
  },
  {
    id: "halfwayForward",
    keys: ["Ctrl+PageDown"],
    name: "Halfway jump",
    match: (event) =>
      (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey &&
      event.key === "PageDown",
    help: "Move halfway through the remaining distance to the last item; repeated presses rapidly narrow a long gallery.",
    run: () => navigateImageViewerHalfway(1),
  },
  {
    id: "first",
    keys: ["Home"],
    name: "Endpoint jump",
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "Home",
    help: "Select the first item in the visible gallery.",
    run: () => navigateImageViewerAbsolute(0),
  },
  {
    id: "last",
    keys: ["End"],
    name: "Endpoint jump",
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "End",
    help: "Select the last item in the visible gallery.",
    run: () => navigateImageViewerAbsolute(-1),
  },
  {
    id: "compareInput",
    keys: ["c"],
    name: "Sticky input comparison",
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey &&
      (event.key === "c" || event.key === "C"),
    help: "Keep input/output comparison armed while traversing; jobs without an input remain single-image.",
    run: () => toggleImageViewerCompare(),
  },
  {
    id: "favorite",
    keys: ["v"],
    name: "Shared exact-image favorite",
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey &&
      event.key === "v",
    help: "Add or remove your persistent favorite for this exact job, generator, and image index.",
    run: () => toggleImageViewerFavorite(),
  },
  {
    id: "fullscreen",
    keys: ["f"],
    name: "Fullscreen inspection",
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey &&
      event.key.toLowerCase() === "f",
    help: "Use the full monitor while preserving the viewer's adaptive image and information layout.",
    run: () => toggleImageViewerFullscreen(),
  },
  {
    id: "returnSync",
    keys: ["s"],
    name: "Close handback",
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey &&
      (event.key === "s" || event.key === "S"),
    help: () =>
      `Currently ${imageViewerReturnSync ? "ON" : "OFF"}. When ON, ` +
      "closing the viewer scrolls the page to the image you were viewing and focuses it; " +
      "when OFF, the page stays where it was",
    run: () => {
      setViewerReturnSync(!imageViewerReturnSync);
      // Refresh the ON/OFF readout in place when toggled from the help list.
      if (imageViewerHelpOpen) showImageViewerHelp();
    },
  },
  {
    id: "help",
    keys: ["?", "/"],
    name: "Control glossary",
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey &&
      (event.key === "?" || event.key === "/"),
    help: "Show or hide these named viewer interactions and their exact behavior.",
    run: () => toggleImageViewerHelp(),
  },
  {
    id: "close",
    keys: ["Escape"],
    name: "Layered exit",
    match: (event) => event.key === "Escape",
    help: "Close the glossary first, then fullscreen, then the viewer on successive presses.",
    run: () => {
      if (imageViewerHelpOpen) hideImageViewerHelp();
      // While fullscreen, Esc only exits fullscreen (the browser does this
      // natively too); the viewer stays open. A second Esc closes it.
      else if (document.fullscreenElement === imageViewer) document.exitFullscreen();
      else closeImageViewer();
    },
  },
];

// Fullscreen covers the whole monitor with the viewer overlay; entering and
// exiting both fire a window resize, so fitImageViewerWindow re-shrink-wraps
// the window to the new viewport automatically.
async function toggleImageViewerFullscreen() {
  if (document.fullscreenElement) {
    await document.exitFullscreen();
    return;
  }
  // Entering fullscreen means "draw this as large as the screen allows", so
  // a prior manual drag-resize stops pinning the window size.
  delete imageViewerWindow.dataset.userSized;
  await imageViewer.requestFullscreen();
}

// Pre-content size: fill the viewport (any monitor aspect) minus a thin
// margin that keeps the click-outside-to-close backdrop reachable. Once the
// image decodes, fitImageViewerWindow shrink-wraps the window to the content.
function sizeImageViewerWindow() {
  const margin = 16;
  const width = Math.max(300, window.innerWidth - margin * 2);
  const height = Math.max(260, window.innerHeight - margin * 2);
  imageViewerWindow.style.width = `${width}px`;
  imageViewerWindow.style.height = `${height}px`;
  imageViewerWindow.style.left = `${Math.max(margin, (window.innerWidth - width) / 2)}px`;
  imageViewerWindow.style.top = `${Math.max(margin, (window.innerHeight - height) / 2)}px`;
}

// Shrink-wrap the window to the displayed content: the image (or the two
// compare panes) draws at the largest undistorted size the screen allows,
// and the window hugs that — full width only when the content actually
// needs it (wide image, ultrawide compare), never just to fill the monitor.
// The info panel (generator, resolution, prompt, guidance) goes wherever it
// costs the image the least: below the stage, or as a fixed-width right
// column when that draws the image larger — typical for wide images and for
// compare mode, where the doubled stage makes height the binding constraint.
const ImageViewerSideStatusWidth = 280; // matches the .side-status grid column in style.css

function fitImageViewerWindow() {
  if (imageViewer.hidden || imageViewerWindow.dataset.userSized || !imageViewerContentAr) return;
  const margin = 16;
  const gap = 4; // matches #image-viewer-stage.compare gap
  const availWidth = Math.max(440, window.innerWidth - margin * 2);
  const availHeight = Math.max(320, window.innerHeight - margin * 2);
  const stageSizeWithin = (maxWidth, maxHeight) => {
    if (imageViewerStage.classList.contains("compare")) {
      const inputAr = imageViewerInputImage.naturalWidth > 0
        ? imageViewerInputImage.naturalWidth / imageViewerInputImage.naturalHeight
        : imageViewerContentAr;
      // Equal-width panes: the wider aspect dictates the pane width needed
      // for both images to reach the shared stage height.
      const paneAr = Math.max(inputAr, imageViewerContentAr);
      const paneMaxWidth = (maxWidth - gap) / 2;
      const height = Math.min(maxHeight, paneMaxWidth / paneAr);
      return { width: height * paneAr * 2 + gap, height };
    }
    const height = Math.min(maxHeight, maxWidth / imageViewerContentAr);
    return { width: height * imageViewerContentAr, height };
  };
  const placeWindow = (width, height) => {
    imageViewerWindow.style.width = `${width}px`;
    imageViewerWindow.style.height = `${height}px`;
    imageViewerWindow.style.left = `${Math.max(margin, (window.innerWidth - width) / 2)}px`;
    imageViewerWindow.style.top = `${Math.max(margin, (window.innerHeight - height) / 2)}px`;
  };

  // Bottom-bar candidate: the bar's height depends on the window width (the
  // prompt wraps differently as the window narrows), so measure it in bottom
  // mode over two passes. This leaves the bottom layout applied.
  imageViewerWindow.classList.remove("side-status");
  let bottomStageHeight = 0;
  for (let pass = 0; pass < 2; pass++) {
    const statusHeight = imageViewerStatus.offsetHeight;
    const stage = stageSizeWithin(availWidth, Math.max(120, availHeight - statusHeight));
    bottomStageHeight = stage.height;
    placeWindow(
      Math.max(440, Math.min(availWidth, Math.round(stage.width))),
      Math.max(320, Math.min(availHeight, Math.round(stage.height + statusHeight))));
  }

  // Side-column candidate: fixed-width info column, full-height stage. The
  // displayed image height equals the stage height in both layouts (the stage
  // hugs the content's aspect ratio), so comparing stage heights compares
  // image scale directly. Ties keep the bottom bar.
  const sideStage = stageSizeWithin(availWidth - ImageViewerSideStatusWidth, availHeight);
  if (sideStage.height > bottomStageHeight + 1) {
    imageViewerWindow.classList.add("side-status");
    placeWindow(
      Math.max(440, Math.min(availWidth, Math.round(sideStage.width + ImageViewerSideStatusWidth))),
      Math.max(320, Math.min(availHeight, Math.round(sideStage.height))));
  }
}

function clampImageViewerWindow() {
  if (imageViewer.hidden) return;
  const margin = 8;
  const rect = imageViewerWindow.getBoundingClientRect();
  const width = Math.min(rect.width, window.innerWidth - margin * 2);
  const height = Math.min(rect.height, window.innerHeight - margin * 2);
  const left = Math.max(margin, Math.min(rect.left, window.innerWidth - width - margin));
  const top = Math.max(margin, Math.min(rect.top, window.innerHeight - height - margin));
  imageViewerWindow.style.width = `${width}px`;
  imageViewerWindow.style.height = `${height}px`;
  imageViewerWindow.style.left = `${left}px`;
  imageViewerWindow.style.top = `${top}px`;
}

function getImageViewerFocusables() {
  return [...imageViewerWindow.querySelectorAll(
    'button:not([disabled]), [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'
  )].filter((node) => !node.closest("[hidden]") && node.offsetParent !== null);
}

function openImageViewer(link) {
  imageViewerFocusBeforeOpen =
    document.activeElement instanceof HTMLElement ? document.activeElement : null;
  // Clear while still hidden. The selected frame and all of its chrome will
  // paint together only after that frame's preload has decoded.
  clearImageViewerPresentation();
  imageViewerState = {
    jobId: link.dataset.jobId,
    generator: link.dataset.generator,
    imageIndex: Number(link.dataset.imageIndex),
  };
  imageViewerNavDelta = 0;
  hideImageViewerHelp();
  imageViewerWheelAccumulator = 0;
  imageViewer.hidden = false;
  document.body.classList.add("image-viewer-open");
  // A manual drag-resize takes over until reload. Otherwise the very first
  // open pre-sizes to the viewport (content unknown while loading); later
  // opens keep the previous shrink-wrapped size until the new image decodes
  // and fitImageViewerWindow refits, avoiding a full-screen flash.
  if (imageViewerWindow.dataset.userSized) clampImageViewerWindow();
  else if (!imageViewerWindow.style.width) sizeImageViewerWindow();
  else clampImageViewerWindow();
  renderImageViewer();
  imageViewerWindow.focus({ preventScroll: true });
}

function closeImageViewer() {
  hideImageViewerHelp();
  if (document.fullscreenElement === imageViewer) document.exitFullscreen();
  // Resolve the departed image's outer-page anchor BEFORE clearing state: the
  // departure pulse (always) and the close handback (opt-in, `s` / settings)
  // both target it.
  const departedAnchor = findViewerAnchor(imageViewerState);
  imageViewer.hidden = true;
  document.body.classList.remove("image-viewer-open");
  imageViewerState = null;
  imageViewerRenderVersion++;
  imageViewerWheelAccumulator = 0;
  imageViewerNavDelta = 0;
  clearImageViewerPresentation();
  for (const [url, entry] of imageViewerCache) discardImageViewerCacheEntry(url, entry);
  const restore = imageViewerFocusBeforeOpen;
  imageViewerFocusBeforeOpen = null;
  if (departedAnchor) pulseViewerAnchor(departedAnchor);
  if (imageViewerReturnSync && departedAnchor) {
    // Handback: inner movement implies outer movement. Scroll only when the
    // thumbnail isn't already fully on screen, and move focus to it so the
    // keyboard continues from where the viewer walk ended.
    const rect = departedAnchor.getBoundingClientRect();
    if (rect.top < 0 || rect.bottom > window.innerHeight) {
      departedAnchor.scrollIntoView({ block: "center" });
    }
    departedAnchor.focus({ preventScroll: true });
  } else if (restore && document.contains(restore) && typeof restore.focus === "function") {
    restore.focus({ preventScroll: true });
  }
  if (favoriteBrowseUser !== null &&
      favoriteBrowseUser !== "*" &&
      !favoriteUsers.has(favoriteBrowseUser)) {
    setFavoriteBrowseUser(null);
  } else if (favoritesGalleryRenderPending && favoriteBrowseUser !== null) {
    renderFavoritesGallery();
  }
}

// Document-level so result images inside archived day sections open the
// viewer exactly like live ones.
document.addEventListener("click", (event) => {
  const link = event.target.closest('a[data-viewer-image="true"]');
  if (!link || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
  event.preventDefault();
  openImageViewer(link);
});

// ---------- hover prefetch of full-resolution originals ----------
// Resting the pointer on a result thumbnail for a beat fetches the exact
// bytes the viewer will need on click, so the click paints from the browser
// HTTP cache instead of hanging on a multi-MB transfer over the server's
// limited uplink (final images are served immutable, so a warmed URL is
// never re-downloaded). Single-flight, latest hover wins: sweeping across a
// grid replaces the in-flight prefetch instead of stacking downloads.
// data-viewer-image anchors are images only (video results never get one).
// Prefetch failure is silent by design: this is speculative cache warming,
// and the click path performs its own fetch whose errors surface normally.
const HoverPrefetchIntentMs = 150;
const hoverPrefetchWarmed = new Set();
let hoverPrefetchTimer = null;
let hoverPrefetchController = null;

function startHoverPrefetch(url) {
  if (hoverPrefetchWarmed.has(url)) return;
  if (hoverPrefetchController) hoverPrefetchController.abort();
  const controller = new AbortController();
  hoverPrefetchController = controller;
  fetch(url, { signal: controller.signal, priority: "low" })
    .then(async (response) => {
      if (!response.ok) return;
      await response.blob(); // drain fully so the cache entry completes
      hoverPrefetchWarmed.add(url);
    })
    .catch(() => {})
    .finally(() => {
      if (hoverPrefetchController === controller) hoverPrefetchController = null;
    });
}

document.addEventListener("pointerover", (event) => {
  // The open viewer runs its own prioritized preload window; stay out of
  // its way. Warmed-set hits skip the timer entirely.
  if (!imageViewer.hidden) return;
  const link = event.target.closest('a[data-viewer-image="true"]');
  if (!link || hoverPrefetchWarmed.has(link.href)) return;
  if (hoverPrefetchTimer) clearTimeout(hoverPrefetchTimer);
  const url = link.href;
  hoverPrefetchTimer = setTimeout(() => {
    hoverPrefetchTimer = null;
    startHoverPrefetch(url);
  }, HoverPrefetchIntentMs);
});

document.addEventListener("pointerout", (event) => {
  // Leaving the anchor cancels only the intent timer; an already-started
  // transfer runs to completion (the pointer usually comes back, and the
  // bytes cache either way).
  const link = event.target.closest('a[data-viewer-image="true"]');
  if (!link) return;
  const to = event.relatedTarget instanceof Element
    ? event.relatedTarget.closest('a[data-viewer-image="true"]')
    : null;
  if (to === link) return; // moved between children of the same anchor
  if (hoverPrefetchTimer) {
    clearTimeout(hoverPrefetchTimer);
    hoverPrefetchTimer = null;
  }
});

// Navigation uses keys, normalized wheel intent, and optional mouse thumb
// buttons (see ImageViewerCommands; ? shows the named interaction glossary).
// Closing is Escape or a click outside the window.
el("image-viewer-help-toggle").addEventListener("click", () => toggleImageViewerHelp());

imageViewer.addEventListener("click", (event) => {
  if (event.target === imageViewer) {
    closeImageViewer();
    return;
  }
  if (imageViewerHelpOpen && event.target === imageViewerHelp) {
    hideImageViewerHelp();
  }
});

function normalizedImageViewerWheelDelta(event) {
  if (event.deltaY === 0 || Math.abs(event.deltaX) > Math.abs(event.deltaY)) return 0;
  // Normalize Firefox line/page wheel units into approximate CSS pixels so
  // one physical gesture has comparable semantics across mice and trackpads.
  const unit = event.deltaMode === 1
    ? 16
    : event.deltaMode === 2
      ? Math.max(1, imageViewerStage.clientHeight || window.innerHeight)
      : 1;
  return event.deltaY * unit;
}

imageViewer.addEventListener("wheel", (event) => {
  if (imageViewer.hidden) return;
  event.preventDefault();
  const delta = normalizedImageViewerWheelDelta(event);
  if (delta === 0) return;
  if (imageViewerHelpOpen) hideImageViewerHelp();
  imageViewerWheelAccumulator += delta;
  if (imageViewerWheelResetTimer) clearTimeout(imageViewerWheelResetTimer);
  imageViewerWheelResetTimer = setTimeout(() => {
    imageViewerWheelAccumulator = 0;
    imageViewerWheelResetTimer = null;
  }, 280);
  if (Math.abs(imageViewerWheelAccumulator) < ImageViewerWheelThreshold) return;
  const direction = imageViewerWheelAccumulator > 0 ? 1 : -1;
  imageViewerWheelAccumulator = 0;
  navigateImageViewerImage(direction);
}, { passive: false });

function isImageViewerSideButton(event) {
  return event.button === 3 || event.button === 4;
}

// Prevent Back/Forward thumb buttons from changing browser history while the
// modal viewer owns the interaction, then map them to exact adjacent items.
imageViewer.addEventListener("mousedown", (event) => {
  if (!imageViewer.hidden && isImageViewerSideButton(event)) event.preventDefault();
});
imageViewer.addEventListener("mouseup", (event) => {
  if (imageViewer.hidden || !isImageViewerSideButton(event)) return;
  event.preventDefault();
  event.stopPropagation();
  if (imageViewerHelpOpen) hideImageViewerHelp();
  navigateImageViewerImage(event.button === 3 ? -1 : 1);
});
imageViewer.addEventListener("auxclick", (event) => {
  if (!imageViewer.hidden && isImageViewerSideButton(event)) event.preventDefault();
});

document.addEventListener("keydown", (event) => {
  if (imageViewer.hidden) return;
  // A modal/form layered over the viewer owns its keyboard completely. This
  // is especially important for Make Video: letters such as c/f/s/v and
  // navigation keys must edit/select normally, never operate the viewer
  // behind the dialog.
  const formOwnsKey = event.target instanceof Element &&
    event.target.closest("input, textarea, select, [contenteditable]");
  if (videoDialog.open || formOwnsKey) {
    return;
  }

  if (event.key === "Tab") {
    const focusables = getImageViewerFocusables();
    if (focusables.length === 0) {
      event.preventDefault();
      return;
    }
    const active = document.activeElement;
    const index = focusables.indexOf(active);
    event.preventDefault();
    if (event.shiftKey) {
      const prev = index <= 0 ? focusables[focusables.length - 1] : focusables[index - 1];
      prev.focus({ preventScroll: true });
    } else {
      const next = index < 0 || index >= focusables.length - 1 ? focusables[0] : focusables[index + 1];
      next.focus({ preventScroll: true });
    }
    return;
  }

  const command = ImageViewerCommands.find((entry) => entry.match(event));
  if (!command) {
    if (imageViewerHelpOpen) {
      event.preventDefault();
      hideImageViewerHelp();
    }
    return;
  }

  event.preventDefault();
  // returnSync keeps the help list up so its ON/OFF readout refreshes in place.
  if (imageViewerHelpOpen && !["help", "close", "returnSync"].includes(command.id)) {
    hideImageViewerHelp();
  }
  command.run();
});

const imageViewerResize = el("image-viewer-resize");
let imageViewerResizeStart = null;
imageViewerResize.addEventListener("pointerdown", (event) => {
  event.preventDefault();
  const rect = imageViewerWindow.getBoundingClientRect();
  imageViewerResizeStart = {
    pointerX: event.clientX,
    pointerY: event.clientY,
    width: rect.width,
    height: rect.height,
    left: rect.left,
    top: rect.top,
  };
  imageViewerResize.setPointerCapture(event.pointerId);
});
imageViewerResize.addEventListener("pointermove", (event) => {
  if (!imageViewerResizeStart || !imageViewerResize.hasPointerCapture(event.pointerId)) return;
  const minimumWidth = window.innerWidth <= 700 ? 300 : 440;
  const minimumHeight = window.innerWidth <= 700 ? 260 : 320;
  const maximumWidth = window.innerWidth - imageViewerResizeStart.left - 8;
  const maximumHeight = window.innerHeight - imageViewerResizeStart.top - 8;
  const width = Math.max(
    Math.min(minimumWidth, maximumWidth),
    Math.min(maximumWidth, imageViewerResizeStart.width + event.clientX - imageViewerResizeStart.pointerX));
  const height = Math.max(
    Math.min(minimumHeight, maximumHeight),
    Math.min(maximumHeight, imageViewerResizeStart.height + event.clientY - imageViewerResizeStart.pointerY));
  imageViewerWindow.style.width = `${width}px`;
  imageViewerWindow.style.height = `${height}px`;
  imageViewerWindow.dataset.userSized = "true";
});
imageViewerResize.addEventListener("pointerup", (event) => {
  imageViewerResizeStart = null;
  if (imageViewerResize.hasPointerCapture(event.pointerId)) {
    imageViewerResize.releasePointerCapture(event.pointerId);
  }
});
imageViewerResize.addEventListener("pointercancel", () => {
  imageViewerResizeStart = null;
});
window.addEventListener("resize", () => {
  if (imageViewer.hidden) return;
  if (imageViewerWindow.dataset.userSized || !imageViewerContentAr) clampImageViewerWindow();
  else fitImageViewerWindow();
});
// The compare pane's input image loads independently of the output; its
// aspect ratio can widen the shrink-wrapped window once known.
imageViewerInputImage.addEventListener("load", fitImageViewerWindow);

// One describe result block: the returned description text at full card
// width, the model's separated "comments" (meta remarks the JSON reply
// contract diverts out of the description), a copy button, a "view with
// image" link, and a collapsed sent/returned exchange viewer showing the
// exact wire prompt and the raw pre-parse reply. No input thumbnail here —
// the job head right above already shows the input image; the view link is
// the viewer-walk anchor (data-viewer-image, resultKind "text"), so the
// viewer shows the submitted image beside the full description.
function buildDescribeResult(jobId, gen, entry, totalInputs, sentPrompt) {
  const result = document.createElement("div");
  result.className = "media-result describe-result";

  const inputUrl = apiUrl(`api/jobs/${encodeURIComponent(jobId)}/images/input/${entry.inputIndex}`);
  const a = document.createElement("a");
  a.className = "describe-view";
  a.href = inputUrl;
  a.target = "_blank";
  a.textContent = "view with image";
  a.title = totalInputs > 1
    ? `open the viewer: input image ${entry.inputIndex + 1} of ${totalInputs} beside this description`
    : "open the viewer: the described input image beside this description";
  a.dataset.viewerImage = "true";
  a.dataset.jobId = jobId;
  a.dataset.generator = gen;
  a.dataset.imageIndex = String(entry.inputIndex);
  a.dataset.generatorCount = String(totalInputs);
  a.dataset.resultKind = "text";
  a.dataset.describeText = entry.text;
  a.dataset.describeComments = entry.comments || "";
  if (viewerSeenSet.has(viewerSeenKeyFor(jobId, gen, entry.inputIndex))) {
    a.classList.add("viewer-seen");
  }

  const body = document.createElement("div");
  body.className = "describe-body";
  if (totalInputs > 1) {
    const which = document.createElement("div");
    which.className = "describe-which";
    which.textContent = `input ${entry.inputIndex + 1} of ${totalInputs}`;
    body.appendChild(which);
  }
  const text = document.createElement("div");
  text.className = "describe-text";
  text.textContent = entry.text;
  body.appendChild(text);

  // The model's non-description remarks (the JSON contract diverts caveats,
  // offers, and meta-chatter into "comments" so the description stays clean).
  if (entry.comments) {
    const comments = document.createElement("div");
    comments.className = "describe-comments";
    const commentsLabel = document.createElement("span");
    commentsLabel.className = "describe-comments-label";
    commentsLabel.textContent = "model comments: ";
    comments.appendChild(commentsLabel);
    comments.appendChild(document.createTextNode(entry.comments));
    body.appendChild(comments);
  }

  const tools = document.createElement("div");
  tools.className = "describe-tools";
  const copyBtn = document.createElement("button");
  copyBtn.type = "button";
  copyBtn.className = "describe-copy";
  copyBtn.textContent = "copy text";
  copyBtn.title = "Copy this description";
  let copyTimer = null;
  copyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(entry.text);
      copyBtn.textContent = "copied";
    } catch {
      copyBtn.textContent = "copy failed";
    }
    if (copyTimer) clearTimeout(copyTimer);
    copyTimer = setTimeout(() => { copyBtn.textContent = "copy text"; }, 1600);
  });
  tools.appendChild(copyBtn);
  tools.appendChild(a);
  body.appendChild(tools);

  // Exact wire exchange, collapsed by default: the full prompt actually sent
  // to this endpoint (instruction + JSON reply contract) and the raw reply
  // before parsing. Ideogram sends no prompt at all; events persisted before
  // this feature lack `raw` and show the parsed description instead.
  const exchange = document.createElement("details");
  exchange.className = "describe-exchange";
  const summary = document.createElement("summary");
  summary.textContent = "sent / returned";
  summary.title = "Show the exact full prompt sent to this endpoint and its raw reply";
  exchange.appendChild(summary);
  const sentHead = document.createElement("div");
  sentHead.className = "describe-exchange-head";
  sentHead.textContent = "sent prompt";
  exchange.appendChild(sentHead);
  const sentBody = document.createElement("div");
  sentBody.className = "describe-exchange-body";
  // "" means Ideogram (which sends no prompt by design); events persisted
  // before the exchange recorder existed have no claim to make either way.
  sentBody.textContent = sentPrompt
    || (gen === "describe-ideogram"
      ? "(nothing — Ideogram /describe uses its fixed built-in instruction; the prompt is not sent)"
      : "(not recorded — this result predates the exchange recorder)");
  exchange.appendChild(sentBody);
  const returnedHead = document.createElement("div");
  returnedHead.className = "describe-exchange-head";
  returnedHead.textContent = "raw reply";
  exchange.appendChild(returnedHead);
  const returnedBody = document.createElement("div");
  returnedBody.className = "describe-exchange-body";
  returnedBody.textContent = entry.raw || entry.text;
  exchange.appendChild(returnedBody);
  body.appendChild(exchange);

  result.appendChild(body);
  return result;
}

// "~$0.25", "~$0.02", "~$1.5" — trailing zeros trimmed. Empty for 0/absent.
function formatCost(v) {
  if (!(v > 0)) return "";
  return "~$" + v.toFixed(v < 0.01 ? 4 : 3).replace(/0+$/, "").replace(/\.$/, "");
}

// The session-spend bar collapses to just the headline total; the collapsed
// preference sticks per browser.
const CostSummaryCollapsedKey = "multi-image-client.cost-summary-collapsed";
let costSummaryCollapsed = localStorage.getItem(CostSummaryCollapsedKey) === "true";
let costHeadline = "";
let costBreakdown = "";

function renderCostSummary() {
  const bar = el("cost-summary");
  if (!uiSettings.showCosts || !costHeadline) {
    bar.hidden = true;
    return;
  }
  bar.hidden = false;
  el("cost-text").textContent = costSummaryCollapsed
    ? costHeadline
    : `${costHeadline}: ${costBreakdown}`;
  const toggle = el("cost-toggle");
  toggle.textContent = costSummaryCollapsed ? "show" : "hide";
  toggle.title = costSummaryCollapsed
    ? "Show the per-generator spend breakdown"
    : "Collapse the per-generator spend breakdown";
  toggle.setAttribute("aria-expanded", String(!costSummaryCollapsed));
}

el("cost-toggle").addEventListener("click", () => {
  costSummaryCollapsed = !costSummaryCollapsed;
  localStorage.setItem(CostSummaryCollapsedKey, String(costSummaryCollapsed));
  renderCostSummary();
});

// Recompute per-job and session cost totals from the DOM (each cell stores
// its own cost in data attributes), so SSE replays / reconnects can never
// double-count. Estimates from each generator's GetCost(), not bills.
function updateCostTotals() {
  const perGen = new Map(); // gen key -> { cost, images }
  let grand = 0;
  let grandImages = 0;
  // Archived cards get their per-job cost label too, but only live-feed
  // jobs count toward the session spend summary.
  for (const card of document.querySelectorAll("#jobs .job, #archive .job")) {
    const inLiveFeed = card.parentElement === jobsSection;
    let jobTotal = 0;
    for (const cell of card.querySelectorAll(".cell")) {
      const images = Number(cell.dataset.imgCount || 0);
      if (images === 0) continue;
      const cost = Number(cell.dataset.cost || 0);
      jobTotal += cost;
      if (inLiveFeed) {
        grand += cost;
        grandImages += images;
        const agg = perGen.get(cell.dataset.gen) || { cost: 0, images: 0 };
        agg.cost += cost;
        agg.images += images;
        perGen.set(cell.dataset.gen, agg);
      }
    }
    card.querySelector(".job-cost").textContent = jobTotal > 0 ? `est. ${formatCost(jobTotal)}` : "";
  }

  if (grandImages === 0) {
    costHeadline = "";
    costBreakdown = "";
    renderCostSummary();
    return;
  }
  // "results" not "images": describe cells contribute text descriptions.
  costHeadline = `Session est. spend ${grand > 0 ? formatCost(grand) : "$0"} for ${grandImages} result${grandImages === 1 ? "" : "s"}`;
  costBreakdown = [...perGen.entries()]
    .sort((a, b) => b[1].cost - a[1].cost)
    .map(([key, v]) => `${genLabel(key)} ${v.cost > 0 ? formatCost(v.cost) : "free"} (${v.images})`)
    .join(" \u00b7 ");
  renderCostSummary();
}

function formatElapsed(ms) {
  const totalSeconds = Math.max(0, Math.floor(ms / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return minutes > 0 ? `${minutes}m ${String(seconds).padStart(2, "0")}s` : `${seconds}s`;
}

function setCellStatus(cell, text, spinning = false) {
  const status = cell.querySelector(".cell-status");
  status.className = "cell-status";
  status.textContent = "";
  if (spinning) {
    const spinner = document.createElement("div");
    spinner.className = "spinner";
    status.appendChild(spinner);
  }
  const label = document.createElement("span");
  label.textContent = text;
  status.appendChild(label);
}

function updateJobProgress(card) {
  const cells = [...card.querySelectorAll(".cell")];
  const finished = cells.filter((cell) => ["done", "error", "no-result"].includes(cell.dataset.state)).length;
  const failed = cells.filter((cell) => ["error", "no-result"].includes(cell.dataset.state)).length;
  const progress = card.querySelector(".job-progress");
  progress.textContent = `${finished}/${cells.length} finished${failed ? `, ${failed} failed` : ""}`;
}

// "Set active": copy a past job's entire setup (input image, prompt,
// generator selection, and the options recorded on its accepted event) back
// into the composer as the new working state. Options are only applied when
// the job's events actually recorded them (jobs persisted before the
// accepted event carried options restore prompt/image/generators only).
async function setActiveFromJob(id, card) {
  const inputCount = Math.max(
    0,
    parseInt(card.dataset.inputCount || (card.querySelector(".job-input-thumb") ? "1" : "0"), 10) || 0);
  if (inputCount > 0) {
    const blobs = [];
    for (let i = 0; i < inputCount; i++) {
      const resp = await fetch(apiUrl(`api/jobs/${encodeURIComponent(id)}/images/input/${i}`));
      if (!resp.ok) {
        if (i === 0) throw new Error(`input image fetch returned HTTP ${resp.status}`);
        break;
      }
      const blob = await resp.blob();
      if (!blob.type.startsWith("image/")) {
        throw new Error(`input image ${i} fetch returned ${blob.type || "an unknown content type"}`);
      }
      blobs.push(blob);
    }
    await setImagesFromBlobs(blobs);
  } else {
    clearImage();
  }

  promptBox.value = card.querySelector(".job-prompt").textContent;
  if (mcpheeCtl) mcpheeCtl.refresh();
  if (mcpheePanel && !mcpheePanelContainer.hidden) mcpheePanel.refresh();
  const recorded = card.dataset;
  if (recorded.optShape) el("opt-shape").value = recorded.optShape;
  if (recorded.optDetail) el("opt-detail").value = recorded.optDetail;
  if (recorded.optQuality) el("opt-quality").value = recorded.optQuality;
  if (recorded.optModeration) el("opt-moderation").value = recorded.optModeration;
  if (recorded.optN) el("opt-n").value = recorded.optN;
  // gpt-image-2 guidance is deliberately NOT restored here: it's a global
  // browser setting (settings modal, localStorage), not per-job composer state.
  updateShapeOptionLabel();

  const wanted = new Set([...card.querySelectorAll(".cell")].map((cell) => cell.dataset.gen));
  // Both sections restore: describe selections come back too (the input image
  // is already re-attached above, so their image requirement is satisfied).
  for (const cb of allGeneratorInputs()) {
    if (cb.dataset.available !== "true") continue;
    cb.checked = wanted.has(cb.value);
    cb.closest(".gen-toggle").classList.toggle("checked", cb.checked);
  }
  updateGeneratorCompatibility();

  window.scrollTo({ top: 0, behavior: "smooth" });
  promptBox.focus({ preventScroll: true });
}

// opts: { user, container }. user is the creator display name (shared-site
// attribution + filter target); container is where the card renders — the
// live feed by default, an archive day section otherwise.
function addJobCard(id, prompt, gens, hasImage, createdAtUnixMs, inputCount, opts = {}) {
  if (isPromptHidden(id)) return null;
  const existing = el(`job-${id}`);
  if (existing) {
    if (opts.canHide && existing.dataset.canHide !== "true") {
      existing.dataset.canHide = "true";
      const copyWrap = existing.querySelector(".copy-prompt-wrap");
      if (copyWrap && !copyWrap.querySelector(".hide-prompt")) {
        copyWrap.prepend(createHidePromptButton(id));
      }
    }
    return existing;
  }

  const resolvedInputCount = Math.max(
    0,
    Number.isFinite(inputCount) ? inputCount : (hasImage ? 1 : 0));

  // The server sends only the UTC instant (unix ms); every displayed time is
  // formatted here in the viewer's own timezone and locale.
  const createdMs = Number(createdAtUnixMs) || Date.now();

  const card = document.createElement("div");
  card.className = "job";
  card.id = `job-${id}`;
  card.dataset.jobId = id;
  card.dataset.state = "queued";
  card.dataset.createdAt = String(createdMs);
  card.dataset.user = opts.user || "";
  card.dataset.canHide = String(!!opts.canHide);
  // Read by the image viewer's input-comparison mode (`c`).
  card.dataset.hasInputImage = String(!!hasImage || resolvedInputCount > 0);
  card.dataset.inputCount = String(resolvedInputCount);

  const head = document.createElement("div");
  head.className = "job-head";
  if (hasImage || resolvedInputCount > 0) {
    // Served through the job store; completed jobs also survive server restarts.
    // Primary (index 0) is the card thumb; a badge shows when more were attached.
    const thumbLink = document.createElement("a");
    thumbLink.href = apiUrl(`api/jobs/${id}/images/input/0`);
    thumbLink.target = "_blank";
    const thumb = document.createElement("img");
    thumb.className = "job-input-thumb";
    thumb.src = `${thumbLink.href}?thumb=1`;
    thumb.loading = "lazy";
    thumbLink.appendChild(thumb);
    if (resolvedInputCount > 1) {
      const countBadge = document.createElement("span");
      countBadge.className = "job-input-count";
      countBadge.textContent = `×${resolvedInputCount}`;
      countBadge.title = `${resolvedInputCount} input images (gpt-image-2 received all; others received the first)`;
      thumbLink.appendChild(countBadge);
    }
    head.appendChild(thumbLink);
  }
  const promptDiv = document.createElement("div");
  promptDiv.className = "job-prompt";
  promptDiv.textContent = prompt;
  head.appendChild(promptDiv);
  if (prompt) {
    // Copy-prompt affordance (the familiar two-overlapping-squares icon):
    // copies the exact prompt text and flashes "prompt copied". Deliberately
    // OUTSIDE .job-prompt, whose textContent is read verbatim elsewhere
    // (video dialog source prompt, image viewer caption).
    const copyWrap = document.createElement("div");
    copyWrap.className = "copy-prompt-wrap";
    const copyBtn = document.createElement("button");
    copyBtn.type = "button";
    copyBtn.className = "copy-prompt";
    copyBtn.title = "Copy this prompt";
    copyBtn.setAttribute("aria-label", "Copy this prompt");
    copyBtn.innerHTML =
      '<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true">' +
      '<rect x="1.5" y="1.5" width="9.5" height="9.5" rx="1.5"/>' +
      '<rect x="5" y="5" width="9.5" height="9.5" rx="1.5"/>' +
      '</svg>';
    const copyNote = document.createElement("span");
    copyNote.className = "copy-prompt-note";
    copyNote.hidden = true;
    let copyNoteTimer = null;
    copyBtn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(prompt);
        copyNote.textContent = "prompt copied";
        copyNote.classList.remove("err");
      } catch {
        copyNote.textContent = "copy failed";
        copyNote.classList.add("err");
      }
      copyNote.hidden = false;
      if (copyNoteTimer) clearTimeout(copyNoteTimer);
      copyNoteTimer = setTimeout(() => { copyNote.hidden = true; }, 1600);
    });
    if (opts.canHide) copyWrap.appendChild(createHidePromptButton(id));
    copyWrap.append(createPromptFavoriteButton(id), copyBtn, copyNote);
    head.appendChild(copyWrap);
  }
  const meta = document.createElement("div");
  meta.className = "job-meta";
  meta.innerHTML = `
    <span class="job-user"></span>
    <span class="job-created"></span>
    <span class="job-progress">0/${gens.length} finished</span>
    <span class="job-elapsed">elapsed 0s</span>
    <span class="job-cost"></span>
    <span class="job-connection">connecting…</span>`;
  meta.querySelector(".job-user").textContent = opts.user || "";
  // Same-local-day jobs show just the time; older ones (archive cards, or a
  // viewer whose local date differs from the server's day bucket) include
  // the local date so the time can't be misread as today's.
  const created = new Date(createdMs);
  meta.querySelector(".job-created").textContent =
    created.toDateString() === new Date().toDateString()
      ? created.toLocaleTimeString()
      : created.toLocaleString();
  // Video jobs aren't composer setups, so they get no set-active button.
  if (!gens.includes("grok-web-video")) {
    const setActive = document.createElement("button");
    setActive.type = "button";
    setActive.className = "job-set-active";
    setActive.textContent = "set active";
    setActive.title = "Copy this job's image, prompt, generators, and options into the composer";
    setActive.addEventListener("click", async () => {
      setActive.disabled = true;
      try {
        await setActiveFromJob(id, card);
      } catch (err) {
        sendError.textContent = `set active failed: ${err}`;
        window.scrollTo({ top: 0, behavior: "smooth" });
      } finally {
        setActive.disabled = false;
      }
    });
    meta.appendChild(setActive);
  }
  head.appendChild(meta);
  card.appendChild(head);

  const cells = document.createElement("div");
  cells.className = "job-cells";
  for (const key of gens) {
    const cell = document.createElement("div");
    cell.className = "cell";
    cell.dataset.gen = key;
    cell.dataset.state = "queued";
    cell.innerHTML = `
      <div class="cell-head">
        <span class="cell-name"></span>
        <span class="cell-size"></span>
        <span class="cell-cost"></span>
        <span class="cell-time"></span>
      </div>
      <div class="cell-status"><div class="spinner"></div><span>queued</span></div>
      <div class="cell-images"></div>`;
    cell.querySelector(".cell-name").textContent = genLabel(key);
    const genCfg = generators.find((g) => g.key === key);
    if (hasImage && genCfg && !genCfg.imageCapable) {
      // Text-only marker: this target ran from the prompt alone; the job's
      // attached image was never sent to it.
      const noImg = document.createElement("span");
      noImg.className = "cell-noimg";
      noImg.textContent = "text-only";
      noImg.title = `${genLabel(key)} doesn't accept input images; the attached image was not sent to it — this result is from the prompt text alone`;
      cell.querySelector(".cell-name").after(noImg);
    }
    cells.appendChild(cell);
  }
  card.appendChild(cells);

  applyNightModeToCard(card);
  applyUserFilterToCard(card);
  registerUser(opts.user || "");
  (opts.container || jobsSection).prepend(card);
  return card;
}

// All job events arrive by short cursor-based polling, deliberately NOT by
// EventSource/WebSocket: on plain-HTTP localhost the browser has no HTTP/2,
// so every persistent connection permanently occupies one of ~6 HTTP/1.1
// sockets shared across ALL tabs of the whole browser. A few open windows
// each holding a stream starved every <img> load and the page went blind
// (observed twice, 2026-07-27). Each poll answers immediately and releases
// its socket. cursor=0 replays the full envelope log (job-known metadata
// announcements followed by each job's events), which is also how a fresh
// window hydrates; replays are idempotent.
let jobsPollCursor = 0;
let jobsPollTimer = null;
let jobsPollInFlight = false;
let jobsPollFailing = false;

function setAllJobConnections(text, isError) {
  for (const card of jobsSection.querySelectorAll(".job")) {
    if (card.dataset.state === "done") continue;
    const connection = card.querySelector(".job-connection");
    connection.textContent = text;
    connection.classList.toggle("err", isError);
  }
}

async function pollJobEvents() {
  if (jobsPollInFlight) return;
  jobsPollInFlight = true;
  if (jobsPollTimer) {
    clearTimeout(jobsPollTimer);
    jobsPollTimer = null;
  }
  try {
    const visibilityQuery = visibilityServerVersion
      ? `&visibilityVersion=${encodeURIComponent(visibilityServerVersion)}`
      : "";
    const resp = await fetch(apiUrl(
      `api/events/poll?cursor=${jobsPollCursor}${visibilityQuery}`));
    if (resp.status === 401) { location.reload(); return; }
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    const body = await resp.json();
    // A cursor lower than ours means the server restarted and resynced us
    // from 0; the replayed history is applied idempotently.
    jobsPollCursor = body.cursor;
    applyVisibilitySnapshot(body.visibility);
    for (const envelope of body.envelopes) {
      if (envelope.kind === "job-known") {
        const j = envelope.job;
        addJobCard(
          j.id, j.prompt, j.gens, j.hasImage, j.createdAtUnixMs, j.inputCount,
          { user: j.user, canHide: !!j.canHide });
        continue;
      }
      const card = el(`job-${envelope.jobId}`);
      if (card) applyJobEvent(envelope.jobId, card, envelope.event);
    }
    if (jobsPollFailing) {
      jobsPollFailing = false;
      setAllJobConnections("live", false);
    }
  } catch {
    if (!jobsPollFailing) {
      jobsPollFailing = true;
      setAllJobConnections("server disconnected — retrying", true);
    }
  } finally {
    jobsPollInFlight = false;
    // Back off in hidden tabs; the visibilitychange handler polls
    // immediately when the tab comes back.
    jobsPollTimer = setTimeout(pollJobEvents, document.hidden ? 5000 : 1000);
  }
}

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) pollJobEvents();
});

function applyJobEvent(id, card, evt) {
  if (card.dataset.state !== "done") {
    const connection = card.querySelector(".job-connection");
    connection.textContent = "live";
    connection.classList.remove("err");
  }

  if (evt.type === "accepted" || evt.type === "job-queued") {
    card.dataset.state = "queued";
    // Recorded composer options, consumed by setActiveFromJob. Older
    // persisted events may lack them; only what was recorded is stored.
    if (evt.shape) card.dataset.optShape = evt.shape;
    if (evt.detail) card.dataset.optDetail = evt.detail;
    if (evt.quality) card.dataset.optQuality = evt.quality;
    if (evt.moderation) card.dataset.optModeration = evt.moderation;
    if (evt.n) card.dataset.optN = String(evt.n);
    // What the gpt2 target actually received: "sent" (guidance text rode
    // along) or "off" (toggle disabled). Absent on events recorded before
    // this control existed — the viewer then makes no claim either way.
    if (typeof evt.gpt2GuidanceEnabled === "boolean") {
      const guidanceText = (evt.gpt2GuidanceText || "").trim();
      const guidanceSent = evt.gpt2GuidanceEnabled && guidanceText.length > 0;
      card.dataset.gpt2Guidance = guidanceSent ? "sent" : "off";
      card.dataset.gpt2GuidanceText = guidanceSent ? guidanceText : "";
    }
    if (Number.isInteger(evt.inputCount) && evt.inputCount >= 0) {
      card.dataset.inputCount = String(evt.inputCount);
      card.dataset.hasInputImage = String(evt.inputCount > 0);
      // Older cards may have been created from job-known before inputCount
      // arrived; ensure a multi-input badge appears once accepted is known.
      const thumbLink = card.querySelector(".job-head > a");
      if (thumbLink && evt.inputCount > 1 && !thumbLink.querySelector(".job-input-count")) {
        const countBadge = document.createElement("span");
        countBadge.className = "job-input-count";
        countBadge.textContent = `×${evt.inputCount}`;
        countBadge.title = `${evt.inputCount} input images (gpt-image-2 received all; others received the first)`;
        thumbLink.appendChild(countBadge);
      }
    }
  } else if (evt.type === "job-start") {
    card.dataset.state = "running";
    card.dataset.startedAt = String(evt.at || Date.now());
  } else if (evt.type === "gen-start") {
    const cell = card.querySelector(`.cell[data-gen="${evt.gen}"]`);
    if (!cell || ["done", "error"].includes(cell.dataset.state)) return;
    cell.dataset.state = "running";
    cell.dataset.startedAt = String(evt.at || Date.now());
    setCellStatus(cell, "generating…", true);
  } else if (evt.type === "gen-partial") {
    const cell = card.querySelector(`.cell[data-gen="${evt.gen}"]`);
    if (!cell || ["done", "error"].includes(cell.dataset.state)) return;
    if (isImageHidden(id, evt.gen, evt.imageIndex)) return;
    setCellStatus(cell, `partial preview ${evt.partialIndex + 1} received`, true);
    const images = cell.querySelector(".cell-images");
    const selector = `img[data-partial-index="${evt.imageIndex}"]`;
    let img = images.querySelector(selector);
    if (!img) {
      const a = document.createElement("a");
      a.target = "_blank";
      a.className = "partial-image";
      img = document.createElement("img");
      img.dataset.partialIndex = String(evt.imageIndex);
      img.alt = "Partial image preview";
      // Partial bytes live only in server memory; after a restart a replayed
      // gen-partial event 404s. Drop the dead preview instead of leaving a
      // broken-image icon on old failed jobs.
      img.addEventListener("error", () => a.remove());
      a.appendChild(img);
      images.appendChild(a);
    }
    img.parentElement.href = apiUrl(`${evt.url}?v=${evt.partialIndex}`);
    img.src = apiUrl(`${evt.url}?v=${evt.partialIndex}`);
  } else if (evt.type === "gen-result") {
    const cell = card.querySelector(`.cell[data-gen="${evt.gen}"]`);
    if (!cell) return;
    const status = cell.querySelector(".cell-status");
    const images = cell.querySelector(".cell-images");
    const time = cell.querySelector(".cell-time");
    if (evt.ms > 0) time.textContent = `${(evt.ms / 1000).toFixed(1)}s`;
    images.textContent = "";
    // Multi-image results (e.g. grok-web's 4) render as a 2-column grid so
    // the cell stays roughly the same height as a single full-width image.
    images.classList.toggle(
      "multi",
      evt.ok && evt.images.filter(Boolean).length > 1 &&
        !(evt.mediaType && evt.mediaType.startsWith("video/")));

    if (evt.ok && evt.resultKind === "text" && Array.isArray(evt.texts)) {
      // Describe results: text descriptions of the job's input image(s), one
      // block per input, with a copy affordance. Clicking a block opens the
      // viewer showing the described input image beside the full text.
      cell.dataset.state = "done";
      if (evt.label) cell.querySelector(".cell-head").title = evt.label;
      cell.dataset.cost = String(evt.cost || 0);
      cell.dataset.imgCount = String(evt.texts.length);
      cell.querySelector(".cell-cost").textContent = formatCost(evt.cost);
      status.textContent = "";
      for (const t of evt.texts) {
        images.appendChild(buildDescribeResult(id, evt.gen, t, evt.texts.length, evt.sentPrompt || ""));
      }
      if (!imageViewer.hidden) renderImageViewer();
    } else if (evt.ok) {
      cell.dataset.state = "done";
      // Naming rule: the cell keeps the exact display name shown in the
      // generator chooser; the provider's internal spec string moves to a
      // tooltip and the actual returned pixel size renders beside the name.
      // (Events persisted before 2026-07-28 embed the size in label.)
      const sizeText = evt.size
        || (evt.label && (/ \u00b7 (\d+x\d+)$/.exec(evt.label) || [])[1])
        || "";
      cell.querySelector(".cell-size").textContent = sizeText;
      if (evt.label) cell.querySelector(".cell-head").title = evt.label;
      cell.dataset.cost = String(evt.cost || 0);
      cell.dataset.imgCount = String(evt.images.filter(Boolean).length);
      cell.querySelector(".cell-cost").textContent = formatCost(evt.cost);
      status.textContent = "";
      for (const [imageIndex, rawUrl] of evt.images.entries()) {
        if (!rawUrl || isImageHidden(id, evt.gen, imageIndex)) continue;
        // Event URLs are persisted server-side as "/api/..."; resolve them
        // against the page's base so they work behind the proxy prefix.
        const url = apiUrl(rawUrl);
        if (evt.mediaType && evt.mediaType.startsWith("video/")) {
          const result = document.createElement("div");
          result.className = "media-result";
          result.appendChild(createVideoPlayer(
            url,
            `grok-video-${id}-${imageIndex + 1}.mp4`));

          if (videoGeneration.available) {
            const redo = document.createElement("button");
            redo.type = "button";
            redo.className = "make-video make-video-redo";
            redo.setAttribute("aria-label", "Redo Grok video");
            redo.title = "Redo Grok video";
            redo.textContent = "redo grok video";
            redo.addEventListener("click", () => {
              const priorPrompt = card.querySelector(".job-prompt").textContent;
              const sourceUrl = apiUrl(`api/jobs/${encodeURIComponent(id)}/images/input/0`);
              openVideoDialog(id, "input", 0, sourceUrl, priorPrompt, {
                mode: evt.videoMode,
                durationSeconds: evt.videoDurationSeconds,
                resolution: evt.videoResolution,
                aspectRatio: evt.videoAspectRatio,
              });
            });
            result.appendChild(redo);
          }
          images.appendChild(result);
          continue;
        }

        const result = document.createElement("div");
        result.className = "media-result";
        const a = document.createElement("a");
        a.href = url;
        a.target = "_blank";
        a.dataset.viewerImage = "true";
        a.dataset.jobId = id;
        a.dataset.generator = evt.gen;
        a.dataset.imageIndex = String(imageIndex);
        a.dataset.generatorCount = String(evt.images.length);
        // Persistent per-browser marker: this image was viewed full-size in
        // the zoom viewer at some point (events replay, so re-apply here).
        if (viewerSeenSet.has(viewerSeenKeyFor(id, evt.gen, imageIndex))) {
          a.classList.add("viewer-seen");
        }
        const img = document.createElement("img");
        img.alt = `${genLabel(evt.gen)} image ${imageIndex + 1} of ${evt.images.length}`;
        // Cards display the <=640px server-side preview; the anchor (viewer,
        // open-in-new-tab, video source) keeps the exact original bytes.
        // B2-hosted results carry a parallel `thumbs` array of local preview
        // URLs, because ?thumb=1 means nothing to the B2 origin and would
        // pull the full-resolution original into every card. Pre-hosting
        // events have no thumbs and keep the local ?thumb=1 form.
        img.src = Array.isArray(evt.thumbs) && evt.thumbs[imageIndex]
          ? apiUrl(evt.thumbs[imageIndex])
          : `${url}?thumb=1`;
        img.loading = "lazy";
        // Reserve the final layout box before the bytes arrive: without an
        // intrinsic size every card collapses, the whole history fits inside
        // the browser's "near viewport" zone, and loading="lazy" defers
        // nothing (observed 2026-07-28: 541 eager full-res loads per refresh).
        const dims = /^(\d+)x(\d+)$/.exec(sizeText);
        if (dims) {
          img.style.aspectRatio = `${dims[1]} / ${dims[2]}`;
          img.style.width = "100%";
        }
        a.appendChild(img);
        result.appendChild(a);
        applyFavoriteMarkerToAnchor(a);
        images.appendChild(result);
      }
      if (!imageViewer.hidden) renderImageViewer();
    } else {
      cell.dataset.state = "error";
      // Keep the short generator name on failure (the long spec label just
      // adds noise next to an error) and turn the timing red, not green.
      time.classList.add("err");
      status.className = "cell-status err";
      status.textContent = evt.error || "failed";
      // Payment/auth failures arrive with a server-classified next step and
      // the URL that fixes it (billing page, key console, cookie re-export).
      if (evt.errorHint) {
        const hint = document.createElement("div");
        hint.className = "cell-hint";
        hint.textContent = `${evt.errorHint} `;
        if (evt.errorHintUrl) {
          const a = document.createElement("a");
          a.href = evt.errorHintUrl;
          a.target = "_blank";
          a.rel = "noopener";
          const hintHost = new URL(evt.errorHintUrl).hostname.replace(/^www\./, "");
          a.textContent = hintHost;
          a.setAttribute("aria-label", `${hintHost} (external link)`);
          hint.appendChild(a);
        }
        status.after(hint);
      }
    }
    updateJobProgress(card);
    updateCostTotals();
  } else if (evt.type === "grid") {
    // Video jobs may have persisted a legacy grid event containing only their
    // source image. The playable/downloadable MP4 is the useful artifact, so
    // suppress that obsolete contact-sheet link during both live and archive
    // event replay.
    if (card.querySelector('.cell[data-gen="grok-web-video"]')) return;
    if ([...hiddenImageKeys].some((key) => key.startsWith(`${id}|`))) return;
    let link = card.querySelector(".grid-link");
    if (!link) {
      link = document.createElement("div");
      link.className = "grid-link";
      link.innerHTML = `<a target="_blank"></a>`;
      card.appendChild(link);
    }
    const a = link.querySelector("a");
    a.href = apiUrl(evt.url);
    a.textContent = "combined contact sheet";
    // The on-disk path is tooltip-only; the clickable link is what matters.
    a.title = `saved: ${evt.path}`;
  } else if (evt.type === "job-done") {
    card.dataset.state = "done";
    card.querySelector(".job-connection").textContent = "complete";
    // Any cell still spinning got no gen-result (shouldn't happen, but
    // never leave an infinite spinner).
    for (const spin of card.querySelectorAll(".cell-status .spinner")) {
      const status = spin.parentElement;
      status.closest(".cell").dataset.state = "no-result";
      status.className = "cell-status err";
      status.textContent = "no result";
    }
    updateJobProgress(card);
  }
}

setInterval(() => {
  const now = Date.now();
  for (const card of jobsSection.querySelectorAll(".job")) {
    if (card.dataset.state !== "done") {
      const startedAt = Number(card.dataset.startedAt || card.dataset.createdAt || now);
      card.querySelector(".job-elapsed").textContent = `elapsed ${formatElapsed(now - startedAt)}`;
    }
    for (const cell of card.querySelectorAll('.cell[data-state="running"]')) {
      const startedAt = Number(cell.dataset.startedAt || now);
      const elapsed = now - startedAt;
      cell.querySelector(".cell-time").textContent = formatElapsed(elapsed);
    }
  }
}, 1000);

// ---------- day archive ----------

// The live feed only carries today's jobs; everything older lives in the
// archive as a list of days. Expanding a day fetches its complete jobs +
// event history once and renders them through the exact same card pipeline
// as live jobs, so copy-prompt, set-active, the viewer, video follow-ups,
// and the person filters all work identically on archived work.
const archiveSection = el("archive");
const archiveDaysEl = el("archive-days");

async function loadArchiveDays() {
  let days;
  try {
    const resp = await fetch(apiUrl("api/archive/days"));
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    days = (await resp.json()).days;
  } catch (err) {
    // Non-fatal: the live feed still works; say so instead of hiding it.
    archiveSection.hidden = favoriteBrowseUser !== null;
    archiveDaysEl.textContent = `could not list archived days: ${err}`;
    return;
  }
  if (!days.length) return;
  archiveSection.hidden = favoriteBrowseUser !== null;
  archiveDaysEl.replaceChildren(...days.map(buildArchiveDayRow));
}

function relativeDayName(dayIso) {
  const toIso = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
  const yesterday = new Date();
  yesterday.setDate(yesterday.getDate() - 1);
  return dayIso === toIso(yesterday) ? "yesterday" : "";
}

function buildArchiveDayRow(d) {
  const wrap = document.createElement("div");
  wrap.className = "archive-day";
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "archive-day-toggle";
  const relative = relativeDayName(d.day);
  btn.textContent = `${relative ? relative + " — " : ""}${d.label} · ${d.count} job${d.count === 1 ? "" : "s"}`;
  const container = document.createElement("div");
  container.className = "archive-day-jobs";
  container.hidden = true;
  btn.addEventListener("click", async () => {
    if (!container.dataset.loaded) {
      btn.disabled = true;
      try {
        await loadArchiveDay(d.day, container);
        container.dataset.loaded = "true";
      } catch (err) {
        const msg = document.createElement("p");
        msg.className = "archive-day-error";
        msg.textContent = `could not load ${d.day}: ${err}`;
        container.replaceChildren(msg);
      } finally {
        btn.disabled = false;
      }
    }
    container.hidden = !container.hidden;
    btn.classList.toggle("open", !container.hidden);
  });
  wrap.append(btn, container);
  return wrap;
}

async function loadArchiveDay(day, container) {
  const resp = await fetch(apiUrl(`api/archive/days/${encodeURIComponent(day)}`));
  if (resp.status === 401) { location.reload(); return; }
  if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
  const body = await resp.json();
  for (const item of body.jobs) {
    const j = item.job;
    const card = addJobCard(
      j.id, j.prompt, j.gens, j.hasImage, j.createdAtUnixMs, j.inputCount,
      { user: j.user, canHide: !!j.canHide, container });
    if (!card) continue;
    for (const evt of item.events) {
      applyJobEvent(j.id, card, evt);
    }
  }
}

// ---------- server RAM status ----------

const ramStatusSamples = [];
const RamStatusSampleLimit = 20; // Five minutes at the 15-second poll interval.

function formatBytesShort(n) {
  if (!(n > 0)) return "?";
  if (n < 1024) return `${n}B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)}K`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(n >= 100 * 1024 * 1024 ? 0 : 1)}M`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)}G`;
}

async function pollRamStatus() {
  const node = el("ram-status");
  if (!node) return;
  try {
    const resp = await fetch(apiUrl("api/status"));
    if (resp.status === 401) { location.reload(); return; }
    if (!resp.ok) throw new Error(String(resp.status));
    const s = await resp.json();
    // cgroup current = real process usage. high/max are systemd *limits*,
    // not consumption — label them so "1.2G" is never read as "we're using 1.2G".
    const used = s.cgroupCurrentBytes || s.workingSetBytes || 0;
    const high = s.cgroupHighBytes || 0;
    const max = s.cgroupMaxBytes || 0;
    const limit = high || max;
    ramStatusSamples.push(used);
    if (ramStatusSamples.length > RamStatusSampleLimit) ramStatusSamples.shift();
    const recentPeak = Math.max(...ramStatusSamples);
    const oldest = ramStatusSamples[0] || used;
    const changeRatio = oldest > 0 ? (used - oldest) / oldest : 0;
    const trend = changeRatio >= 0.05 ? "↑" : changeRatio <= -0.05 ? "↓" : "→";
    let text = `RAM ${formatBytesShort(used)}`;
    if (ramStatusSamples.length > 1) {
      text += ` · 5m peak ${formatBytesShort(recentPeak)} ${trend}`;
    }
    if (limit) text += ` (limit ${formatBytesShort(limit)})`;
    else text += ` (no cgroup cap)`;
    if (high && max && high !== max) text += ` hard ${formatBytesShort(max)}`;
    node.textContent = text;
    const warn = limit > 0 && used / limit >= 0.85;
    node.classList.toggle("warn", warn);
    const browserBits = [];
    if (s.grokBrowserConfigured) {
      browserBits.push(s.grokBrowserWarm ? "grok Chromium warm" : "grok Chromium idle");
    }
    if (s.metaBrowserConfigured) {
      browserBits.push(s.metaBrowserWarm ? "meta Chromium warm" : "meta Chromium idle");
    }
    node.title = [
      `in use (cgroup) ${formatBytesShort(s.cgroupCurrentBytes || used)}`,
      `working set ${formatBytesShort(s.workingSetBytes)}`,
      `process peak working set ${formatBytesShort(s.peakWorkingSetBytes)}`,
      ramStatusSamples.length > 1
        ? `this window: ${ramStatusSamples.length} samples · recent peak ${formatBytesShort(recentPeak)} · change ${trend}`
        : null,
      `managed heap ${formatBytesShort(s.managedHeapBytes)}`,
      s.cgroupHighBytes != null ? `systemd MemoryHigh (soft limit) ${formatBytesShort(s.cgroupHighBytes)}` : null,
      s.cgroupMaxBytes != null ? `systemd MemoryMax (hard limit) ${formatBytesShort(s.cgroupMaxBytes)}` : "cgroup max unlimited/unknown",
      `jobs live ${s.liveJobCount || 0} · hydrated ${s.hydratedJobCount || 0} · indexed ${s.indexedJobCount || 0}`,
      `envelope log ${s.envelopeCount || 0}`,
      `card preview cache ${s.cardPreviewCacheEntries || 0} · ${formatBytesShort(s.cardPreviewCacheBytes || 0)}`,
      browserBits.length ? browserBits.join(" · ") : null,
      "VmSize/private virtual address space is not RAM — ignore huge VSZ numbers from ps.",
    ].filter(Boolean).join("\n");
  } catch {
    node.textContent = "RAM ?";
  }
}

// ---------- boot ----------

// Every window is a view over durable server-side job history. The first
// poll (cursor=0) hydrates TODAY's jobs: they are announced chronologically
// (prepend => newest on top) and each job's full event history replays, so
// finished jobs render completely and running ones resume live. Older days
// hydrate lazily through the archive section below the feed.
loadConfig()
  .then(() => {
    pollJobEvents();
    loadKnownUsers();
    loadFavorites();
    loadArchiveDays();
    pollRamStatus();
    setInterval(pollFavorites, 5000);
    setInterval(pollRamStatus, 15000);
  })
  .catch((err) => {
    sendError.textContent = `config load failed: ${err}`;
  });
