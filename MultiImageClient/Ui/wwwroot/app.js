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
const sendBtn = el("send");
const sendError = el("send-error");
const jobsSection = el("jobs");

// ---------- config / generator toggles ----------

async function loadConfig() {
  const resp = await fetch("/api/config");
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
    label.title = g.available ? g.detail : g.detail + " — NOT CONFIGURED (missing key or cookies)";

    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.value = g.key;
    cb.disabled = !g.available;
    cb.checked = g.available && g.defaultOn;
    cb.addEventListener("change", () => label.classList.toggle("checked", cb.checked));

    label.appendChild(cb);
    label.appendChild(document.createTextNode(g.label));
    label.classList.toggle("checked", cb.checked);
    gensRow.appendChild(label);
  }
}

// ---------- image attach: paste / drop / browse ----------

function setImage(fileOrBlob) {
  if (!fileOrBlob || !fileOrBlob.type.startsWith("image/")) return;
  inputImageFile = fileOrBlob;
  preview.src = URL.createObjectURL(fileOrBlob);
  preview.hidden = false;
  clearBtn.hidden = false;
  pasteHint.hidden = true;
  pasteZone.classList.add("has-image");
}

function clearImage() {
  inputImageFile = null;
  if (preview.src) URL.revokeObjectURL(preview.src);
  preview.removeAttribute("src");
  preview.hidden = true;
  clearBtn.hidden = true;
  pasteHint.hidden = false;
  pasteZone.classList.remove("has-image");
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
    addJobCard(body.id, prompt, gens, !!inputImageFile);
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

function addJobCard(id, prompt, gens, hasImage, createdAt) {
  const card = document.createElement("div");
  card.className = "job";
  card.id = `job-${id}`;

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
  meta.textContent = createdAt || new Date().toLocaleTimeString();
  head.appendChild(meta);
  card.appendChild(head);

  const cells = document.createElement("div");
  cells.className = "job-cells";
  for (const key of gens) {
    const cell = document.createElement("div");
    cell.className = "cell";
    cell.dataset.gen = key;
    cell.innerHTML = `
      <div class="cell-head">
        <span class="cell-name"></span>
        <span class="cell-time"></span>
      </div>
      <div class="cell-status"><div class="spinner"></div></div>
      <div class="cell-images"></div>`;
    cell.querySelector(".cell-name").textContent = genLabel(key);
    cells.appendChild(cell);
  }
  card.appendChild(cells);

  jobsSection.prepend(card);
}

function watchJob(id) {
  const es = new EventSource(`/api/jobs/${id}/events`);
  es.onmessage = (msg) => {
    const evt = JSON.parse(msg.data);
    const card = el(`job-${id}`);
    if (!card) return;

    if (evt.type === "gen-result") {
      const cell = card.querySelector(`.cell[data-gen="${evt.gen}"]`);
      if (!cell) return;
      const status = cell.querySelector(".cell-status");
      const images = cell.querySelector(".cell-images");
      const time = cell.querySelector(".cell-time");
      if (evt.ms > 0) time.textContent = `${(evt.ms / 1000).toFixed(1)}s`;

      if (evt.ok) {
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
        // Keep the short generator name on failure (the long spec label just
        // adds noise next to an error) and turn the timing red, not green.
        time.classList.add("err");
        status.className = "cell-status err";
        status.textContent = evt.error || "failed";
      }
    } else if (evt.type === "grid") {
      const link = document.createElement("div");
      link.className = "grid-link";
      link.innerHTML = `<a target="_blank"></a>`;
      const a = link.querySelector("a");
      a.href = evt.url;
      a.textContent = "combined contact sheet";
      link.appendChild(document.createTextNode(`  (saved: ${evt.path})`));
      card.appendChild(link);
    } else if (evt.type === "job-done") {
      es.close();
      // Any cell still spinning got no gen-result (shouldn't happen, but
      // never leave an infinite spinner).
      for (const spin of card.querySelectorAll(".cell-status .spinner")) {
        const status = spin.parentElement;
        status.className = "cell-status err";
        status.textContent = "no result";
      }
    }
  };
  es.onerror = () => es.close();
}

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
    addJobCard(j.id, j.prompt, j.gens, j.hasImage, j.createdAt);
    watchJob(j.id);
  }
}

loadConfig()
  .then(hydrateJobs)
  .catch((err) => {
    sendError.textContent = `config load failed: ${err}`;
  });
