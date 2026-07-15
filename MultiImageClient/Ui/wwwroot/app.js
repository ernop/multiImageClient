"use strict";

// ---------- state ----------

let inputImageFile = null;   // File/Blob for the attached image
let generators = [];         // from /api/config

const el = (id) => document.getElementById(id);
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

  const fillSelect = (selectEl, entries) => {
    selectEl.innerHTML = "";
    for (const e of entries) {
      const opt = document.createElement("option");
      opt.value = e.key;
      opt.textContent = e.label;
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
  const imageCapable = new Set(["gpt2", "grok-api", "grok-api-pro", "google", "googlepro", "bfl", "ideogram", "recraft"]);
  for (const cb of gensRow.querySelectorAll("input")) {
    const providerAvailable = cb.dataset.available === "true";
    const incompatible = !!inputImageFile && !imageCapable.has(cb.value);
    cb.disabled = !providerAvailable || incompatible;
    if (incompatible)
    {
      cb.checked = false;
    }
    const label = cb.closest(".gen-toggle");
    label.classList.toggle("unavailable", cb.disabled);
    label.classList.toggle("checked", cb.checked);
    if (incompatible)
    {
      label.title = `${genLabel(cb.value)} is text-to-image only; remove the input image to use it`;
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

// ---------- image attach: paste / drop / browse ----------

function setImage(fileOrBlob) {
  if (!fileOrBlob || !fileOrBlob.type.startsWith("image/")) return;
  inputImageFile = fileOrBlob;
  preview.src = URL.createObjectURL(fileOrBlob);
  preview.hidden = false;
  clearBtn.hidden = false;
  pasteHint.hidden = true;
  pasteZone.classList.add("has-image");
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

const genLabel = (key) => (generators.find((g) => g.key === key) || { label: key }).label;

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
    // Served from the job's in-memory store, so it survives page reloads.
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
        status.textContent = "";
        for (const url of evt.images) {
          const a = document.createElement("a");
          a.href = url;
          a.target = "_blank";
          const img = document.createElement("img");
          img.src = url;
          img.loading = "lazy";
          a.appendChild(img);
          images.appendChild(a);
        }
      } else {
        cell.dataset.state = "error";
        // Keep the short generator name on failure (the long spec label just
        // adds noise next to an error) and turn the timing red, not green.
        time.classList.add("err");
        status.className = "cell-status err";
        status.textContent = evt.error || "failed";
      }
      updateJobProgress(card);
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
      if (elapsed >= 120000 && !cell.querySelector(".cell-status").textContent.includes("partial")) {
        setCellStatus(cell, "still waiting for the provider…", true);
      }
    }
  }
}, 1000);

// ---------- boot ----------

// Jobs live on the server for the life of the process; every window is just
// a view. On load, hydrate all existing jobs (the SSE stream replays each
// job's full event history, so finished jobs render completely and running
// ones resume live).
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
