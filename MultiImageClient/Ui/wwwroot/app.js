"use strict";

// ---------- reverse-proxy-safe URLs ----------

// The shared-site deployment serves the app behind a secret nginx path
// prefix, so nothing may reference the origin root. Every API URL — including
// server-generated ones persisted inside events as "/api/..." — resolves
// through this helper against the page's own directory.
const appBase = location.pathname.replace(/[^/]*$/, "");
const apiUrl = (path) => appBase + String(path).replace(/^\//, "");

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
let imageViewerState = null;  // stable { jobId, generator, imageIndex } identity
let imageViewerRenderVersion = 0;
let imageViewerHelpOpen = false;
// Global "always compare with the input image" toggle (`c` in the viewer),
// sticky across images and page loads. Jobs without an input image show the
// normal single-image view even while the mode is on.
let imageViewerCompareInput = localStorage.getItem("imageViewerCompareInput") === "true";
let imageViewerContentAr = null; // current output image's aspect ratio, for window shrink-wrap
let imageViewerFocusBeforeOpen = null;
let imageViewerWheelAccumulator = 0;
let imageViewerWheelResetTimer = null;
let imageViewerPreloadActive = 0;
const imageViewerPreloadWaiters = [];
const imageViewerCache = new Map();
const ImageViewerPreloadRadius = 12;
const ImageViewerPreloadConcurrency = 4;
const ImageViewerPageJumpSize = 5;
const ImageViewerWheelThreshold = 80;

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
const imageViewerGuidance = el("image-viewer-guidance");
const imageViewerGenerator = el("image-viewer-generator");
const imageViewerDimensions = el("image-viewer-dimensions");
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
  if (Number.isInteger(cfg.maxInputImages) && cfg.maxInputImages >= 1) {
    maxInputImages = cfg.maxInputImages;
  }
  authInfo = cfg.auth || authInfo;
  applyAuthState();
  initGpt2Guidance();

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

  gensRow.innerHTML = "";
  for (const g of generators) {
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
    gensRow.appendChild(label);
  }
  updateGeneratorCount();
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
  for (const btn of document.querySelectorAll("#gen-controls .image-only-action")) {
    btn.hidden = !hasImage;
  }
  for (const cb of gensRow.querySelectorAll("input")) {
    const providerAvailable = cb.dataset.available === "true";
    const imageCapable = cb.dataset.imageCapable === "true";
    const aspectIncompatible =
      hasImage &&
      imageCapable &&
      el("opt-shape").value !== "auto" &&
      cb.dataset.imageAspectOverride !== "true";
    cb.disabled = !providerAvailable || aspectIncompatible;
    if (aspectIncompatible)
    {
      cb.checked = false;
    }
    const label = cb.closest(".gen-toggle");
    label.classList.toggle("unavailable", cb.disabled);
    label.classList.toggle("checked", cb.checked);
    if (aspectIncompatible)
    {
      label.title = `${genLabel(cb.value)} cannot override output AR with an input image; choose match input image to use it`;
    }
    else if (hasImage && !imageCapable)
    {
      label.title = `${genLabel(cb.value)} doesn't accept input images — it will run from the prompt text only; the attached image is NOT sent to it`;
    }
    else if (hasImage && inputImageItems.length > 1 && cb.value !== "gpt2" && imageCapable)
    {
      label.title = `${genLabel(cb.value)} will receive only the first of ${inputImageItems.length} attached images (gpt-image-2 receives all)`;
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

// ---------- prompt length limits (gentle, non-blocking) ----------

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
  favorite.textContent = isFavorite(item) ? "★" : "☆";
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

// ---------- submit ----------

function checkedGeneratorKeys() {
  return [...gensRow.querySelectorAll("input:checked")].map((cb) => cb.value);
}

async function submit() {
  sendError.textContent = "";
  const prompt = promptBox.value.trim();
  if (!prompt) { sendError.textContent = "prompt is empty"; return; }
  const gens = checkedGeneratorKeys();
  if (gens.length === 0) { sendError.textContent = "pick at least one generator"; return; }
  const user = currentUsername();
  if (!user) {
    sendError.textContent = "choose a username first (top of the page) — everything here is created under a name";
    usernameInput.focus();
    return;
  }

  const form = new FormData();
  form.append("prompt", prompt);
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
    addJobCard(body.id, prompt, gens, inputImageItems.length > 0, null, Date.now(), inputImageItems.length, { user });
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
    if (spellwellCtl) spellwellCtl.refresh();
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
  if (spellwellCtl) spellwellCtl.refresh();
  updatePromptLimitNotice();
  promptBox.focus();
});

// ---------- SpellWell: local dictionary highlighting + local fix ----------

// Complements the Claude button with a fully offline pass: live block
// highlights behind the prompt (pink = misspelled, blue = unknown word,
// yellow/grey boxes = double spaces) plus a free one-click fix. The module
// lives in spellwell/ as a self-contained drop-in shared across projects.
let spellwell = null;     // SpellWell checker instance (null until loaded)
let spellwellCtl = null;  // overlay controller for the prompt textarea
const spellfixLocalBtn = el("spellfix-local");

// Provider/model jargon that appears in prompts constantly and must never
// light up as a misspelling.
const spellwellJargon = [
  "grok", "xai", "recraft", "ideogram", "bfl", "gpt", "openai", "midjourney",
  "dalle", "webp", "png", "jpeg", "screenshot", "screenshots", "hyperrealistic",
  "photoreal", "photorealistic", "cinematic", "bokeh", "vaporwave", "cyberpunk",
];

async function initSpellwell() {
  try {
    spellwell = await SpellWell.create({
      affUrl: "spellwell/vendor/typo/en_US.aff",
      dicUrl: "spellwell/vendor/typo/en_US.dic",
      extraWords: spellwellJargon,
      customDictStorageKey: "mic_spellwell_custom_dict",
    });
    spellwellCtl = spellwell.attach(promptBox);
    spellfixLocalBtn.disabled = false;
  } catch (err) {
    // No dictionary means no local spellcheck, plainly reported — the Claude
    // button and native browser spellcheck still work.
    console.error(err);
    spellfixLocalBtn.title = `Local fix unavailable: ${err.message || err}`;
  }
}

spellfixLocalBtn.addEventListener("click", () => {
  if (!spellwell) return;
  const original = promptBox.value;
  if (!original.trim()) {
    sendError.textContent = "prompt is empty";
    return;
  }
  sendError.textContent = "";
  const fix = spellwell.localFix(original);
  const idleLabel = "fix typos (local)";
  if (fix.text === original) {
    spellfixLocalBtn.textContent = "no changes";
    setTimeout(() => { spellfixLocalBtn.textContent = idleLabel; }, 1600);
    return;
  }
  spellfixPrevious = original;
  promptBox.value = fix.text;
  spellfixUndoBtn.hidden = false;
  if (spellwellCtl) spellwellCtl.refresh();
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

initSpellwell();

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
    addJobCard(body.id, prompt, ["grok-web-video"], true, null, Date.now(), 1, { user: currentUsername() });
  } catch (err) {
    error.textContent = String(err);
  } finally {
    submitButton.disabled = false;
  }
});

// ---------- custom video player ----------

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

function createVideoPlayer(url) {
  const player = document.createElement("div");
  player.className = "custom-video-player";
  player.tabIndex = 0;

  const video = document.createElement("video");
  video.src = url;
  video.preload = "metadata";
  video.playsInline = true;
  video.volume = 0.5;
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
  volume.value = "0.5";
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
    mute.textContent = video.muted || video.volume === 0 ? "Unmute" : "Mute";
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
    video.muted = !video.muted;
    syncMute();
  });
  volume.addEventListener("input", () => {
    video.volume = Number(volume.value);
    video.muted = video.volume === 0;
    syncMute();
  });
  player.addEventListener("keydown", (event) => {
    if (event.target.matches("input, button")) return;
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
  // Live jobs first (newest at top), then any expanded archive days, so the
  // keyboard walk covers everything currently on screen in page order.
  for (const card of document.querySelectorAll("#jobs .job, #archive .job")) {
    // Night-hidden and person-filtered jobs are invisible to the viewer's
    // keyboard walk too, as are jobs inside a collapsed archive day.
    if (card.classList.contains("night-hidden")) continue;
    if (card.classList.contains("user-filter-hidden")) continue;
    const dayContainer = card.closest(".archive-day-jobs");
    if (dayContainer && dayContainer.hidden) continue;
    const items = [...card.querySelectorAll('a[data-viewer-image="true"]')].map((link) => ({
      jobId: card.id.substring("job-".length),
      generator: link.dataset.generator,
      imageIndex: Number(link.dataset.imageIndex),
      generatorCount: Number(link.dataset.generatorCount),
      url: link.href,
    }));
    if (items.length === 0) continue;
    prompts.push({
      jobId: card.id.substring("job-".length),
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

function acquireImageViewerPreloadSlot() {
  if (imageViewerPreloadActive < ImageViewerPreloadConcurrency) {
    imageViewerPreloadActive++;
    return Promise.resolve();
  }
  return new Promise((resolve) => imageViewerPreloadWaiters.push(resolve));
}

function releaseImageViewerPreloadSlot() {
  const next = imageViewerPreloadWaiters.shift();
  if (next) {
    next();
    return;
  }
  imageViewerPreloadActive = Math.max(0, imageViewerPreloadActive - 1);
}

function discardImageViewerCacheEntry(url, entry) {
  if (imageViewerCache.get(url) !== entry) return;
  imageViewerCache.delete(url);
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

function loadImageViewerEntry(url) {
  const existing = imageViewerCache.get(url);
  if (existing) return existing;

  const controller = new AbortController();
  const entry = { promise: null, blobUrl: null, image: null, controller };
  entry.promise = (async () => {
    await acquireImageViewerPreloadSlot();
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

      entry.blobUrl = URL.createObjectURL(blob);
      entry.image = new Image();
      entry.image.src = entry.blobUrl;
      await entry.image.decode();
      return entry;
    } finally {
      releaseImageViewerPreloadSlot();
    }
  })().catch((error) => {
    if (entry.blobUrl) URL.revokeObjectURL(entry.blobUrl);
    if (imageViewerCache.get(url) === entry) imageViewerCache.delete(url);
    throw error;
  });
  imageViewerCache.set(url, entry);
  return entry;
}

function prepareImageViewerWindow(prompts, current) {
  const allItems = prompts.flatMap((prompt) => prompt.items);
  const currentIndex = allItems.findIndex((item) =>
    item.jobId === current.item.jobId &&
    item.generator === current.item.generator &&
    item.imageIndex === current.item.imageIndex);
  const first = Math.max(0, currentIndex - ImageViewerPreloadRadius);
  const last = Math.min(allItems.length, currentIndex + ImageViewerPreloadRadius + 1);
  const wantedUrls = new Set(allItems.slice(first, last).map((item) => item.url));

  for (const [url, entry] of imageViewerCache) {
    if (!wantedUrls.has(url)) discardImageViewerCacheEntry(url, entry);
  }

  // Current image first, then fan outward by distance so Left/Right neighbors
  // beat the far edge of the preload window for decoder slots.
  const currentEntry = loadImageViewerEntry(current.item.url);
  for (let distance = 1; distance <= ImageViewerPreloadRadius; distance++) {
    for (const index of [currentIndex + distance, currentIndex - distance]) {
      if (index < first || index >= last) continue;
      loadImageViewerEntry(allItems[index].url).promise.catch(() => {});
    }
  }
  return currentEntry.promise;
}

function setImageViewerIdentity(item) {
  imageViewerState = {
    jobId: item.jobId,
    generator: item.generator,
    imageIndex: item.imageIndex,
  };
  renderImageViewer();
}

// Left pane of the `c` comparison: the job's archived input image, straight
// from the durable input URL (immutable-cached for finished jobs).
function applyImageViewerCompare(current) {
  const active = !!current && imageViewerCompareInput && current.prompt.hasInput;
  imageViewerStage.classList.toggle("compare", active);
  imageViewerInputImage.hidden = !active;
  imageViewerInputLabel.hidden = !active;
  imageViewerOutputLabel.hidden = !active;
  if (active) {
    const inputUrl = apiUrl(`api/jobs/${encodeURIComponent(current.item.jobId)}/images/input/0`);
    if (!imageViewerInputImage.src.endsWith(inputUrl)) imageViewerInputImage.src = inputUrl;
  } else {
    imageViewerInputImage.removeAttribute("src");
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

async function renderImageViewer() {
  if (imageViewer.hidden || !imageViewerState) return;
  const prompts = getImageViewerPrompts();
  const current = locateImageViewerState(prompts);
  applyImageViewerCompare(current);
  if (!current) {
    imageViewerImage.removeAttribute("src");
    imageViewerPrompt.textContent = "";
    renderImageViewerGuidance(null);
    imageViewerGenerator.textContent = "selected image is no longer available";
    imageViewerDimensions.textContent = "";
    return;
  }

  const version = ++imageViewerRenderVersion;
  imageViewerImage.removeAttribute("src");
  imageViewerPrompt.textContent = current.prompt.prompt;
  renderImageViewerGuidance(current);
  imageViewerGenerator.textContent = genLabel(current.item.generator);
  imageViewerDimensions.textContent = "loading…";

  try {
    const entry = await prepareImageViewerWindow(prompts, current);
    if (version !== imageViewerRenderVersion || imageViewer.hidden) return;
    const latest = locateImageViewerState(getImageViewerPrompts());
    if (!latest ||
        latest.item.jobId !== current.item.jobId ||
        latest.item.generator !== current.item.generator ||
        latest.item.imageIndex !== current.item.imageIndex) return;
    imageViewerImage.src = entry.blobUrl;
    imageViewerImage.alt =
      `${current.item.generator} image ${current.item.imageIndex + 1} of ${current.item.generatorCount}`;
    imageViewerDimensions.textContent = `${entry.image.naturalWidth}×${entry.image.naturalHeight}`;
    imageViewerContentAr = entry.image.naturalWidth / entry.image.naturalHeight;
    fitImageViewerWindow();
  } catch (error) {
    if (version !== imageViewerRenderVersion || imageViewer.hidden) return;
    if (error && error.name === "AbortError") return;
    imageViewerImage.removeAttribute("src");
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
  setImageViewerIdentity(allItems[targetIndex]);
}

function navigateImageViewerAbsolute(index) {
  const allItems = getImageViewerFlatItems();
  if (allItems.length === 0) return;
  const targetIndex = index < 0 ? allItems.length - 1 : index;
  if (targetIndex < 0 || targetIndex >= allItems.length) return;
  setImageViewerIdentity(allItems[targetIndex]);
}

function navigateImageViewerPrompt(delta) {
  const prompts = getImageViewerPrompts();
  const current = locateImageViewerState(prompts);
  if (!current) return;
  const targetPrompt = prompts[current.promptIndex + delta];
  // At either boundary, keep the current prompt and return to its first image.
  // Prompt navigation never guesses a different destination.
  setImageViewerIdentity((targetPrompt || current.prompt).items[0]);
}

function hideImageViewerHelp() {
  imageViewerHelpOpen = false;
  imageViewerHelp.hidden = true;
}

function showImageViewerHelp() {
  imageViewerHelpList.textContent = "";
  for (const command of ImageViewerCommands) {
    if (!command.help) continue;
    const item = document.createElement("li");
    const keys = document.createElement("kbd");
    keys.textContent = command.keys.join(" / ");
    const label = document.createElement("span");
    label.textContent = command.help;
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
    id: "previous",
    keys: ["Left", "Up"],
    match: (event) =>
      !event.ctrlKey && !event.metaKey &&
      (event.key === "ArrowLeft" || event.key === "ArrowUp"),
    help: "Previous image (crosses prompts)",
    run: () => navigateImageViewerImage(-1),
  },
  {
    id: "next",
    keys: ["Right", "Down"],
    match: (event) =>
      !event.ctrlKey && !event.metaKey &&
      (event.key === "ArrowRight" || event.key === "ArrowDown"),
    help: "Next image (crosses prompts)",
    run: () => navigateImageViewerImage(1),
  },
  {
    id: "newerPrompt",
    keys: ["Ctrl+Left"],
    match: (event) =>
      (event.ctrlKey || event.metaKey) && event.key === "ArrowLeft",
    help: "First image of newer prompt",
    run: () => navigateImageViewerPrompt(-1),
  },
  {
    id: "olderPrompt",
    keys: ["Ctrl+Right"],
    match: (event) =>
      (event.ctrlKey || event.metaKey) && event.key === "ArrowRight",
    help: "First image of older prompt",
    run: () => navigateImageViewerPrompt(1),
  },
  {
    id: "pageBack",
    keys: ["PageUp"],
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "PageUp",
    help: `Jump ${ImageViewerPageJumpSize} images back`,
    run: () => navigateImageViewerImage(-ImageViewerPageJumpSize),
  },
  {
    id: "pageForward",
    keys: ["PageDown"],
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "PageDown",
    help: `Jump ${ImageViewerPageJumpSize} images forward`,
    run: () => navigateImageViewerImage(ImageViewerPageJumpSize),
  },
  {
    id: "first",
    keys: ["Home"],
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "Home",
    help: "First image in gallery",
    run: () => navigateImageViewerAbsolute(0),
  },
  {
    id: "last",
    keys: ["End"],
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "End",
    help: "Last image in gallery",
    run: () => navigateImageViewerAbsolute(-1),
  },
  {
    id: "compareInput",
    keys: ["c"],
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey &&
      (event.key === "c" || event.key === "C"),
    help: "Compare with input image (left: input, right: output; applies to every image whose job had an input)",
    run: () => toggleImageViewerCompare(),
  },
  {
    id: "help",
    keys: ["?", "/"],
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey &&
      (event.key === "?" || event.key === "/"),
    help: "Toggle this shortcut list",
    run: () => toggleImageViewerHelp(),
  },
  {
    id: "close",
    keys: ["Escape"],
    match: (event) => event.key === "Escape",
    help: "Close shortcut list, then viewer",
    run: () => {
      if (imageViewerHelpOpen) hideImageViewerHelp();
      else closeImageViewer();
    },
  },
];

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
// Two passes because the status bar's height depends on the window width
// (the prompt wraps differently when the window narrows).
function fitImageViewerWindow() {
  if (imageViewer.hidden || imageViewerWindow.dataset.userSized || !imageViewerContentAr) return;
  const margin = 16;
  const gap = 4; // matches #image-viewer-stage.compare gap
  const availWidth = Math.max(440, window.innerWidth - margin * 2);
  const availHeight = Math.max(320, window.innerHeight - margin * 2);
  for (let pass = 0; pass < 2; pass++) {
    const statusHeight = el("image-viewer-status").offsetHeight;
    const stageMaxHeight = Math.max(120, availHeight - statusHeight);
    let stageWidth;
    let stageHeight;
    if (imageViewerStage.classList.contains("compare")) {
      const inputAr = imageViewerInputImage.naturalWidth > 0
        ? imageViewerInputImage.naturalWidth / imageViewerInputImage.naturalHeight
        : imageViewerContentAr;
      // Equal-width panes: the wider aspect dictates the pane width needed
      // for both images to reach the shared stage height.
      const paneAr = Math.max(inputAr, imageViewerContentAr);
      const paneMaxWidth = (availWidth - gap) / 2;
      stageHeight = Math.min(stageMaxHeight, paneMaxWidth / paneAr);
      stageWidth = stageHeight * paneAr * 2 + gap;
    } else {
      stageHeight = Math.min(stageMaxHeight, availWidth / imageViewerContentAr);
      stageWidth = stageHeight * imageViewerContentAr;
    }
    const width = Math.max(440, Math.min(availWidth, Math.round(stageWidth)));
    const height = Math.max(320, Math.min(availHeight, Math.round(stageHeight + statusHeight)));
    imageViewerWindow.style.width = `${width}px`;
    imageViewerWindow.style.height = `${height}px`;
    imageViewerWindow.style.left = `${Math.max(margin, (window.innerWidth - width) / 2)}px`;
    imageViewerWindow.style.top = `${Math.max(margin, (window.innerHeight - height) / 2)}px`;
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
  imageViewerState = {
    jobId: link.dataset.jobId,
    generator: link.dataset.generator,
    imageIndex: Number(link.dataset.imageIndex),
  };
  hideImageViewerHelp();
  imageViewerWheelAccumulator = 0;
  imageViewer.hidden = false;
  document.body.classList.add("image-viewer-open");
  // A manual drag-resize takes over until reload. Otherwise the very first
  // open pre-sizes to the viewport (content unknown while loading); later
  // opens keep the previous shrink-wrapped size until the new image decodes
  // and fitImageViewerWindow refits, avoiding a full-screen flash.
  imageViewerContentAr = null;
  if (imageViewerWindow.dataset.userSized) clampImageViewerWindow();
  else if (!imageViewerWindow.style.width) sizeImageViewerWindow();
  else clampImageViewerWindow();
  renderImageViewer();
  imageViewerWindow.focus({ preventScroll: true });
}

function closeImageViewer() {
  hideImageViewerHelp();
  imageViewer.hidden = true;
  document.body.classList.remove("image-viewer-open");
  imageViewerState = null;
  imageViewerRenderVersion++;
  imageViewerWheelAccumulator = 0;
  imageViewerImage.removeAttribute("src");
  applyImageViewerCompare(null);
  for (const [url, entry] of imageViewerCache) discardImageViewerCacheEntry(url, entry);
  const restore = imageViewerFocusBeforeOpen;
  imageViewerFocusBeforeOpen = null;
  if (restore && document.contains(restore) && typeof restore.focus === "function") {
    restore.focus({ preventScroll: true });
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

// Navigation is keyboard-only (see ImageViewerCommands; ? shows the list).
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

imageViewer.addEventListener("wheel", (event) => {
  if (imageViewer.hidden) return;
  event.preventDefault();
  if (imageViewerHelpOpen) hideImageViewerHelp();
  imageViewerWheelAccumulator += event.deltaY;
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

document.addEventListener("keydown", (event) => {
  if (imageViewer.hidden) return;

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
  if (imageViewerHelpOpen && command.id !== "help" && command.id !== "close") {
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
  costHeadline = `Session est. spend ${grand > 0 ? formatCost(grand) : "$0"} for ${grandImages} image${grandImages === 1 ? "" : "s"}`;
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
  for (const cb of gensRow.querySelectorAll("input")) {
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
function addJobCard(id, prompt, gens, hasImage, createdAt, createdAtUnixMs, inputCount, opts = {}) {
  const existing = el(`job-${id}`);
  if (existing) return existing;

  const resolvedInputCount = Math.max(
    0,
    Number.isFinite(inputCount) ? inputCount : (hasImage ? 1 : 0));

  const card = document.createElement("div");
  card.className = "job";
  card.id = `job-${id}`;
  card.dataset.state = "queued";
  card.dataset.createdAt = String(createdAtUnixMs || Date.now());
  card.dataset.user = opts.user || "";
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
    copyWrap.append(copyBtn, copyNote);
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
  meta.querySelector(".job-created").textContent = createdAt || new Date().toLocaleTimeString();
  // Video jobs aren't composer setups, so they get no set-active button.
  if (!gens.includes("grok-web-video")) {
    const setActive = document.createElement("button");
    setActive.type = "button";
    setActive.className = "job-set-active";
    setActive.textContent = "⤴ set active";
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
      // Honest marker: this target ran from the prompt alone; the job's
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
    const resp = await fetch(apiUrl(`api/events/poll?cursor=${jobsPollCursor}`));
    if (resp.status === 401) { location.reload(); return; }
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    const body = await resp.json();
    // A cursor lower than ours means the server restarted and resynced us
    // from 0; the replayed history is applied idempotently.
    jobsPollCursor = body.cursor;
    for (const envelope of body.envelopes) {
      if (envelope.kind === "job-known") {
        const j = envelope.job;
        addJobCard(j.id, j.prompt, j.gens, j.hasImage, j.createdAt, j.createdAtUnixMs, j.inputCount, { user: j.user });
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
      evt.ok && evt.images.length > 1 && !(evt.mediaType && evt.mediaType.startsWith("video/")));

    if (evt.ok) {
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
      cell.dataset.imgCount = String(evt.images.length);
      cell.querySelector(".cell-cost").textContent = formatCost(evt.cost);
      status.textContent = "";
      for (const [imageIndex, rawUrl] of evt.images.entries()) {
        // Event URLs are persisted server-side as "/api/..."; resolve them
        // against the page's base so they work behind the proxy prefix.
        const url = apiUrl(rawUrl);
        if (evt.mediaType && evt.mediaType.startsWith("video/")) {
          const result = document.createElement("div");
          result.className = "media-result";
          result.appendChild(createVideoPlayer(url));

          const redo = document.createElement("button");
          redo.type = "button";
          redo.className = "make-video make-video-redo";
          redo.setAttribute("aria-label", "Redo Grok video");
          redo.title = "Redo Grok video";
          redo.textContent = "↻ grok video";
          redo.disabled = !videoGeneration.available;
          if (!videoGeneration.available) {
            redo.title = videoGeneration.availabilityProblem || "Grok web video is unavailable";
          }
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
        const img = document.createElement("img");
        // Cards display the <=640px server-side preview; the anchor (viewer,
        // open-in-new-tab, video source) keeps the exact original bytes.
        img.src = `${url}?thumb=1`;
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
        const button = document.createElement("button");
        button.type = "button";
        button.className = "make-video make-video-add";
        button.setAttribute("aria-label", "Make Grok video from this image");
        button.title = "Make Grok video from this image";
        button.textContent = "grok video";
        button.disabled = !videoGeneration.available;
        if (!videoGeneration.available) {
          button.title = videoGeneration.availabilityProblem || "Grok web video is unavailable";
        }
        button.addEventListener("click", () => {
          const sourcePrompt = card.querySelector(".job-prompt").textContent;
          openVideoDialog(id, evt.gen, imageIndex, url, sourcePrompt);
        });
        result.appendChild(button);
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
          a.textContent = new URL(evt.errorHintUrl).hostname.replace(/^www\./, "") + " ↗";
          hint.appendChild(a);
        }
        status.after(hint);
      }
    }
    updateJobProgress(card);
    updateCostTotals();
  } else if (evt.type === "grid") {
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
    archiveSection.hidden = false;
    archiveDaysEl.textContent = `could not list archived days: ${err}`;
    return;
  }
  if (!days.length) return;
  archiveSection.hidden = false;
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
      j.id, j.prompt, j.gens, j.hasImage, j.createdAt, j.createdAtUnixMs, j.inputCount,
      { user: j.user, container });
    for (const evt of item.events) {
      applyJobEvent(j.id, card, evt);
    }
  }
}

// ---------- server RAM status ----------

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
    let text = `RAM ${formatBytesShort(used)}`;
    if (limit) text += ` (limit ${formatBytesShort(limit)})`;
    else text += ` (no cgroup cap)`;
    if (high && max && high !== max) text += ` hard ${formatBytesShort(max)}`;
    node.textContent = text;
    const warn = limit > 0 && used / limit >= 0.85;
    node.classList.toggle("warn", warn);
    node.title = [
      `in use (cgroup) ${formatBytesShort(s.cgroupCurrentBytes || used)}`,
      `working set ${formatBytesShort(s.workingSetBytes)}`,
      `managed heap ${formatBytesShort(s.managedHeapBytes)}`,
      s.cgroupHighBytes != null ? `systemd MemoryHigh (soft limit) ${formatBytesShort(s.cgroupHighBytes)}` : null,
      s.cgroupMaxBytes != null ? `systemd MemoryMax (hard limit) ${formatBytesShort(s.cgroupMaxBytes)}` : "cgroup max unlimited/unknown",
      `card preview cache ${s.cardPreviewCacheEntries || 0} · ${formatBytesShort(s.cardPreviewCacheBytes || 0)}`,
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
    loadArchiveDays();
    pollRamStatus();
    setInterval(pollRamStatus, 15000);
  })
  .catch((err) => {
    sendError.textContent = `config load failed: ${err}`;
  });
