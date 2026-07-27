"use strict";

// ---------- state ----------

let inputImageFile = null;   // File/Blob for the attached image
let generators = [];         // from /api/config
let videoSource = null;       // { jobId, generator, index, url }
let videoGeneration = { available: false, availabilityProblem: "video configuration not loaded" };
let imageViewerState = null;  // stable { jobId, generator, imageIndex } identity
let imageViewerRenderVersion = 0;
let imageViewerHelpOpen = false;
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
const preview = el("input-preview");
const pasteHint = el("paste-hint");
const clearBtn = el("clear-image");
const fileInput = el("file-input");
const promptBox = el("prompt");
const gensRow = el("gens-row");
const gensCount = el("gens-count");
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
const imageViewerHelp = el("image-viewer-help");
const imageViewerHelpList = el("image-viewer-help-list");
const imageViewerPrompt = el("image-viewer-prompt");
const imageViewerPosition = el("image-viewer-position");
const imageViewerDimensions = el("image-viewer-dimensions");
let logsEventSource = null;
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

function ensureLogStream() {
  if (logsEventSource) return;
  logsEventSource = new EventSource("/api/logs/events");
  logsEventSource.onopen = () => {
    logsConnection.textContent = "live";
    logsConnection.className = "live";
  };
  logsEventSource.onmessage = (msg) => {
    appendLogLine(JSON.parse(msg.data));
  };
  logsEventSource.onerror = () => {
    logsConnection.textContent = "server disconnected — retrying";
    logsConnection.className = "error";
  };
}

function setLogsOpen(open) {
  logsPanel.hidden = !open;
  logsToggle.setAttribute("aria-expanded", String(open));
  logsToggle.classList.toggle("open", open);
  document.body.classList.toggle("logs-open", open);
  if (open) {
    ensureLogStream();
    requestAnimationFrame(() => {
      logsLines.scrollTop = logsLines.scrollHeight;
    });
  }
}

logsToggle.addEventListener("click", () => setLogsOpen(logsPanel.hidden));
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && !logsPanel.hidden) setLogsOpen(false);
});

// ---------- config / generator toggles ----------

async function loadConfig() {
  const resp = await fetch("/api/config");
  if (!resp.ok) {
    // A 502 from the reverse proxy (server not running) has an empty body, so
    // resp.json() would throw the opaque "unexpected end of data" -- report the
    // real cause instead.
    throw new Error(`/api/config returned HTTP ${resp.status} — is the MultiImageClient server running on :5960?`);
  }
  const cfg = await resp.json();
  generators = cfg.generators;
  videoGeneration = cfg.videoGeneration || videoGeneration;

  const fillSelect = (selectEl, entries) => {
    selectEl.innerHTML = "";
    for (const e of entries) {
      const opt = document.createElement("option");
      opt.value = e.key;
      opt.textContent = e.label;
      opt.dataset.defaultLabel = e.label;
      if (e.inputLabel) opt.dataset.inputLabel = e.inputLabel;
      selectEl.appendChild(opt);
    }
  };
  fillSelect(el("opt-shape"), cfg.shapes);
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
    label.classList.toggle("checked", cb.checked);
    gensRow.appendChild(label);
  }
  updateGeneratorCount();
}

function updateGeneratorCompatibility() {
  // imageCapable comes from /api/config so the server stays the single
  // source of truth for which targets accept an input image.
  for (const cb of gensRow.querySelectorAll("input")) {
    const providerAvailable = cb.dataset.available === "true";
    const imageIncompatible = !!inputImageFile && cb.dataset.imageCapable !== "true";
    const aspectIncompatible =
      !!inputImageFile &&
      el("opt-shape").value !== "auto" &&
      cb.dataset.imageAspectOverride !== "true";
    const incompatible = imageIncompatible || aspectIncompatible;
    cb.disabled = !providerAvailable || incompatible;
    if (incompatible)
    {
      cb.checked = false;
    }
    const label = cb.closest(".gen-toggle");
    label.classList.toggle("unavailable", cb.disabled);
    label.classList.toggle("checked", cb.checked);
    if (imageIncompatible)
    {
      label.title = `${genLabel(cb.value)} is text-to-image only; remove the input image to use it`;
    }
    else if (aspectIncompatible)
    {
      label.title = `${genLabel(cb.value)} cannot override output AR with an input image; choose match input image to use it`;
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
  autoOption.textContent = inputImageFile && autoOption.dataset.inputLabel
    ? autoOption.dataset.inputLabel
    : autoOption.dataset.defaultLabel;
  shapeSelect.title = inputImageFile
    ? "Default: match the attached image's aspect ratio using each model's closest supported output geometry. Choose another option to override it."
    : "Default: let each model choose its output aspect ratio.";
}

function updateGeneratorCount() {
  const available = [...gensRow.querySelectorAll("input:not(:disabled)")];
  const enabled = available.filter((cb) => cb.checked).length;
  gensCount.textContent = `${enabled} of ${available.length} available enabled`;
}

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
el("opt-shape").addEventListener("change", updateGeneratorCompatibility);

// ---------- image attach: paste / drop / browse ----------

function setImage(fileOrBlob) {
  if (!fileOrBlob || !fileOrBlob.type.startsWith("image/")) return;
  inputImageFile = fileOrBlob;
  preview.src = URL.createObjectURL(fileOrBlob);
  preview.hidden = false;
  clearBtn.hidden = false;
  pasteHint.hidden = true;
  pasteZone.classList.add("has-image");
  updateShapeOptionLabel();
  updateGeneratorCompatibility();
}

function clearImage() {
  inputImageFile = null;
  if (preview.src) URL.revokeObjectURL(preview.src);
  preview.removeAttribute("src");
  preview.hidden = true;
  clearBtn.hidden = true;
  pasteHint.hidden = false;
  pasteZone.classList.remove("has-image");
  updateShapeOptionLabel();
  updateGeneratorCompatibility();
}

// Paste works anywhere on the page: grabbing the clipboard image is the
// core gesture, so don't make the user click the zone first.
document.addEventListener("paste", (e) => {
  for (const item of e.clipboardData.items) {
    if (item.type.startsWith("image/")) {
      setImage(item.getAsFile());
      e.preventDefault();
      return;
    }
  }
});

pasteZone.addEventListener("click", (e) => {
  if (e.target !== clearBtn) fileInput.click();
});
clearBtn.addEventListener("click", (e) => {
  e.stopPropagation();
  clearImage();
});
fileInput.addEventListener("change", () => {
  if (fileInput.files.length > 0) setImage(fileInput.files[0]);
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
  if (e.dataTransfer.files.length > 0) setImage(e.dataTransfer.files[0]);
});

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

  const form = new FormData();
  form.append("prompt", prompt);
  form.append("generators", gens.join(","));
  form.append("shape", el("opt-shape").value);
  form.append("detail", el("opt-detail").value);
  form.append("quality", el("opt-quality").value);
  form.append("moderation", el("opt-moderation").value);
  form.append("n", el("opt-n").value);
  if (inputImageFile) form.append("image", inputImageFile, "input.png");

  sendBtn.disabled = true;
  try {
    const resp = await fetch("/api/jobs", { method: "POST", body: form });
    const body = await resp.json();
    if (!resp.ok) { sendError.textContent = body.error || `HTTP ${resp.status}`; return; }
    addJobCard(body.id, prompt, gens, !!inputImageFile, null, Date.now());
    watchJob(body.id);
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
  form.append("mode", el("video-mode").value);
  form.append("duration", el("video-duration").value);
  form.append("resolution", el("video-resolution").value);
  form.append("aspectRatio", el("video-aspect").value);

  submitButton.disabled = true;
  error.textContent = "";
  try {
    const resp = await fetch("/api/video-jobs", { method: "POST", body: form });
    const body = await resp.json();
    if (!resp.ok) {
      error.textContent = body.error || `HTTP ${resp.status}`;
      return;
    }
    videoDialog.close();
    addJobCard(body.id, prompt, ["grok-web-video"], true, null, Date.now());
    watchJob(body.id);
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
  for (const card of jobsSection.querySelectorAll(".job")) {
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

async function renderImageViewer() {
  if (imageViewer.hidden || !imageViewerState) return;
  const prompts = getImageViewerPrompts();
  const current = locateImageViewerState(prompts);
  if (!current) {
    imageViewerImage.removeAttribute("src");
    imageViewerPrompt.textContent = "";
    imageViewerPosition.textContent = "selected image is no longer available";
    imageViewerDimensions.textContent = "";
    return;
  }

  const version = ++imageViewerRenderVersion;
  imageViewerImage.removeAttribute("src");
  imageViewerPrompt.textContent = current.prompt.prompt;
  imageViewerPrompt.title = current.prompt.prompt;
  imageViewerPosition.textContent =
    `${current.item.generator} ${current.item.imageIndex + 1}/${current.item.generatorCount}` +
    ` · prompt ${current.promptIndex + 1}/${prompts.length}`;
  imageViewerDimensions.textContent = "loading…";

  el("image-viewer-previous").disabled =
    current.promptIndex === 0 && current.itemIndex === 0;
  el("image-viewer-next").disabled =
    current.promptIndex === prompts.length - 1 &&
    current.itemIndex === current.prompt.items.length - 1;
  el("image-viewer-newer").disabled = current.promptIndex === 0;
  el("image-viewer-older").disabled = current.promptIndex === prompts.length - 1;

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

function sizeImageViewerWindow() {
  const margin = 16;
  const width = Math.max(300, Math.min(1400, window.innerWidth - margin * 2));
  const height = Math.max(260, Math.min(950, window.innerHeight - margin * 2));
  imageViewerWindow.style.width = `${width}px`;
  imageViewerWindow.style.height = `${height}px`;
  imageViewerWindow.style.left = `${Math.max(margin, (window.innerWidth - width) / 2)}px`;
  imageViewerWindow.style.top = `${Math.max(margin, (window.innerHeight - height) / 2)}px`;
  imageViewerWindow.dataset.sized = "true";
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
  if (!imageViewerWindow.dataset.sized) sizeImageViewerWindow();
  else clampImageViewerWindow();
  renderImageViewer();
  el("image-viewer-close").focus({ preventScroll: true });
}

function closeImageViewer() {
  hideImageViewerHelp();
  imageViewer.hidden = true;
  document.body.classList.remove("image-viewer-open");
  imageViewerState = null;
  imageViewerRenderVersion++;
  imageViewerWheelAccumulator = 0;
  imageViewerImage.removeAttribute("src");
  for (const [url, entry] of imageViewerCache) discardImageViewerCacheEntry(url, entry);
  const restore = imageViewerFocusBeforeOpen;
  imageViewerFocusBeforeOpen = null;
  if (restore && document.contains(restore) && typeof restore.focus === "function") {
    restore.focus({ preventScroll: true });
  }
}

jobsSection.addEventListener("click", (event) => {
  const link = event.target.closest('a[data-viewer-image="true"]');
  if (!link || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
  event.preventDefault();
  openImageViewer(link);
});

el("image-viewer-previous").addEventListener("click", () => navigateImageViewerImage(-1));
el("image-viewer-next").addEventListener("click", () => navigateImageViewerImage(1));
el("image-viewer-newer").addEventListener("click", () => navigateImageViewerPrompt(-1));
el("image-viewer-older").addEventListener("click", () => navigateImageViewerPrompt(1));
el("image-viewer-help-toggle").addEventListener("click", () => toggleImageViewerHelp());
el("image-viewer-close").addEventListener("click", closeImageViewer);

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
window.addEventListener("resize", clampImageViewerWindow);

// "~$0.25", "~$0.02", "~$1.5" — trailing zeros trimmed. Empty for 0/absent.
function formatCost(v) {
  if (!(v > 0)) return "";
  return "~$" + v.toFixed(v < 0.01 ? 4 : 3).replace(/0+$/, "").replace(/\.$/, "");
}

// Recompute per-job and session cost totals from the DOM (each cell stores
// its own cost in data attributes), so SSE replays / reconnects can never
// double-count. Estimates from each generator's GetCost(), not bills.
function updateCostTotals() {
  const perGen = new Map(); // gen key -> { cost, images }
  let grand = 0;
  let grandImages = 0;
  for (const card of jobsSection.querySelectorAll(".job")) {
    let jobTotal = 0;
    for (const cell of card.querySelectorAll(".cell")) {
      const images = Number(cell.dataset.imgCount || 0);
      if (images === 0) continue;
      const cost = Number(cell.dataset.cost || 0);
      jobTotal += cost;
      grand += cost;
      grandImages += images;
      const agg = perGen.get(cell.dataset.gen) || { cost: 0, images: 0 };
      agg.cost += cost;
      agg.images += images;
      perGen.set(cell.dataset.gen, agg);
    }
    card.querySelector(".job-cost").textContent = jobTotal > 0 ? `est. ${formatCost(jobTotal)}` : "";
  }

  const bar = el("cost-summary");
  if (grandImages === 0) {
    bar.hidden = true;
    return;
  }
  bar.hidden = false;
  const breakdown = [...perGen.entries()]
    .sort((a, b) => b[1].cost - a[1].cost)
    .map(([key, v]) => `${genLabel(key)} ${v.cost > 0 ? formatCost(v.cost) : "free"} (${v.images})`)
    .join(" \u00b7 ");
  bar.textContent = `Session est. spend ${grand > 0 ? formatCost(grand) : "$0"} for ${grandImages} image${grandImages === 1 ? "" : "s"}: ${breakdown}`;
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

function addJobCard(id, prompt, gens, hasImage, createdAt, createdAtUnixMs) {
  const existing = el(`job-${id}`);
  if (existing) return existing;

  const card = document.createElement("div");
  card.className = "job";
  card.id = `job-${id}`;
  card.dataset.state = "queued";
  card.dataset.createdAt = String(createdAtUnixMs || Date.now());

  const head = document.createElement("div");
  head.className = "job-head";
  if (hasImage) {
    // Served through the job store; completed jobs also survive server restarts.
    const thumbLink = document.createElement("a");
    thumbLink.href = `/api/jobs/${id}/images/input/0`;
    thumbLink.target = "_blank";
    const thumb = document.createElement("img");
    thumb.className = "job-input-thumb";
    thumb.src = thumbLink.href;
    thumbLink.appendChild(thumb);
    head.appendChild(thumbLink);
  }
  const promptDiv = document.createElement("div");
  promptDiv.className = "job-prompt";
  promptDiv.textContent = prompt;
  head.appendChild(promptDiv);
  const meta = document.createElement("div");
  meta.className = "job-meta";
  meta.innerHTML = `
    <span class="job-created"></span>
    <span class="job-progress">0/${gens.length} finished</span>
    <span class="job-elapsed">elapsed 0s</span>
    <span class="job-cost"></span>
    <span class="job-connection">connecting…</span>`;
  meta.querySelector(".job-created").textContent = createdAt || new Date().toLocaleTimeString();
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
        <span class="cell-cost"></span>
        <span class="cell-time"></span>
      </div>
      <div class="cell-status"><div class="spinner"></div><span>queued</span></div>
      <div class="cell-images"></div>`;
    cell.querySelector(".cell-name").textContent = genLabel(key);
    cells.appendChild(cell);
  }
  card.appendChild(cells);

  jobsSection.prepend(card);
  return card;
}

function watchJob(id) {
  const es = new EventSource(`/api/jobs/${id}/events`);
  es.onopen = () => {
    const card = el(`job-${id}`);
    if (!card || card.dataset.state === "done") return;
    const connection = card.querySelector(".job-connection");
    connection.textContent = "live";
    connection.classList.remove("err");
  };
  es.onmessage = (msg) => {
    const evt = JSON.parse(msg.data);
    const card = el(`job-${id}`);
    if (!card) return;

    if (evt.type === "accepted" || evt.type === "job-queued") {
      card.dataset.state = "queued";
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
        a.appendChild(img);
        images.appendChild(a);
      }
      img.parentElement.href = `${evt.url}?v=${evt.partialIndex}`;
      img.src = `${evt.url}?v=${evt.partialIndex}`;
    } else if (evt.type === "gen-result") {
      const cell = card.querySelector(`.cell[data-gen="${evt.gen}"]`);
      if (!cell) return;
      const status = cell.querySelector(".cell-status");
      const images = cell.querySelector(".cell-images");
      const time = cell.querySelector(".cell-time");
      if (evt.ms > 0) time.textContent = `${(evt.ms / 1000).toFixed(1)}s`;
      images.textContent = "";

      if (evt.ok) {
        cell.dataset.state = "done";
        if (evt.label) cell.querySelector(".cell-name").textContent = evt.label;
        cell.dataset.cost = String(evt.cost || 0);
        cell.dataset.imgCount = String(evt.images.length);
        cell.querySelector(".cell-cost").textContent = formatCost(evt.cost);
        status.textContent = "";
        for (const [imageIndex, url] of evt.images.entries()) {
          if (evt.mediaType && evt.mediaType.startsWith("video/")) {
            const result = document.createElement("div");
            result.className = "media-result";
            result.appendChild(createVideoPlayer(url));

            const redo = document.createElement("button");
            redo.type = "button";
            redo.className = "make-video make-video-redo";
            redo.setAttribute("aria-label", "Redo Grok video");
            redo.title = "Redo Grok video";
            redo.innerHTML =
              '<span class="make-video-symbol" aria-hidden="true">↻</span>' +
              '<span class="make-video-brand" aria-hidden="true">grok</span>';
            redo.disabled = !videoGeneration.available;
            if (!videoGeneration.available) {
              redo.title = videoGeneration.availabilityProblem || "Grok web video is unavailable";
            }
            redo.addEventListener("click", () => {
              const priorPrompt = card.querySelector(".job-prompt").textContent;
              const sourceUrl = `/api/jobs/${encodeURIComponent(id)}/images/input/0`;
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
          img.src = url;
          img.loading = "lazy";
          a.appendChild(img);
          result.appendChild(a);
          const button = document.createElement("button");
          button.type = "button";
          button.className = "make-video make-video-add";
          button.setAttribute("aria-label", "Make Grok video");
          button.title = "Make Grok video";
          button.innerHTML =
            '<span class="make-video-symbol" aria-hidden="true">+</span>' +
            '<span class="make-video-brand" aria-hidden="true">grok</span>';
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
      }
      updateJobProgress(card);
      updateCostTotals();
    } else if (evt.type === "grid") {
      let link = card.querySelector(".grid-link");
      if (!link) {
        link = document.createElement("div");
        link.className = "grid-link";
        link.innerHTML = `<a target="_blank"></a><span></span>`;
        card.appendChild(link);
      }
      const a = link.querySelector("a");
      a.href = evt.url;
      a.textContent = "combined contact sheet";
      link.querySelector("span").textContent = `  (saved: ${evt.path})`;
    } else if (evt.type === "job-done") {
      es.close();
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
  };
  es.onerror = () => {
    const card = el(`job-${id}`);
    if (!card || card.dataset.state === "done") return;
    const connection = card.querySelector(".job-connection");
    connection.textContent = "server disconnected — retrying";
    connection.classList.add("err");
  };
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

// ---------- boot ----------

// Every window is a view over durable server-side job history. On load,
// hydrate all jobs (the SSE stream replays each job's full event history, so
// finished jobs render completely and running ones resume live).
async function hydrateJobs() {
  const resp = await fetch("/api/jobs");
  const body = await resp.json();
  for (const j of body.jobs) {           // chronological; prepend => newest on top
    if (el(`job-${j.id}`)) continue;
    addJobCard(j.id, j.prompt, j.gens, j.hasImage, j.createdAt, j.createdAtUnixMs);
    watchJob(j.id);
  }
}

loadConfig()
  .then(hydrateJobs)
  .catch((err) => {
    sendError.textContent = `config load failed: ${err}`;
  });
