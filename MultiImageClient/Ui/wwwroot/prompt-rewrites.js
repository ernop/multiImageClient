"use strict";

// History stays on disk. Each browser page holds at most 20 complete exchanges.
window.createPromptRewrites = function ({ apiUrl, promptBox, username, applyPrompt }) {
  const panel = document.getElementById("prompt-rewrite-history");
  const list = document.getElementById("prompt-rewrite-list");
  const status = document.getElementById("prompt-rewrite-status");
  const toggle = document.getElementById("prompt-history-toggle");
  const newer = document.getElementById("prompt-history-newer");
  const older = document.getElementById("prompt-history-older");
  const buttons = [...document.querySelectorAll("[data-rewrite-model]")];
  let catalog = [], busy = false, revision = 0, historyRequest = 0;
  let cursors = [null], page = 0, nextCursor = null;
  let lastSubmittedPrompt = null;
  const originalTitle = promptBox.title;
  function refreshDraft() {
    const pending = !!promptBox.value.trim() && promptBox.value.trim() !== lastSubmittedPrompt;
    promptBox.classList.toggle("prompt-not-submitted", pending);
    promptBox.title = pending ? "Changed prompt · not yet submitted for images" : originalTitle;
    for (const section of list.querySelectorAll("section")) {
      section.classList.toggle("prompt-not-submitted",
        pending && section.querySelector("pre").textContent === promptBox.value);
    }
  }
  function submitted(prompt, sourceRevision) {
    // A late acceptance belongs to the submitted version, never a newer edit.
    if (sourceRevision !== revision || promptBox.value.trim() !== prompt) return;
    lastSubmittedPrompt = prompt;
    refreshDraft();
  }
  promptBox.addEventListener("input", () => { revision++; refreshDraft(); });
  document.getElementById("username-input").addEventListener("input", () => {
    historyRequest++;
    lastSubmittedPrompt = null;
    promptBox.classList.remove("prompt-not-submitted");
    promptBox.title = originalTitle;
    list.replaceChildren();
    status.textContent = "";
    panel.hidden = true;
    toggle.setAttribute("aria-expanded", "false");
    cursors = [null]; page = 0; nextCursor = null;
  });

  function configure(models) {
    catalog = models;
    for (const button of buttons) {
      const model = catalog.find(item => item.model === button.dataset.rewriteModel);
      button.disabled = busy || !model?.available;
      button.title = model?.available
        ? "Expand the current prompt while preserving its intent and explicit details."
        : model?.availabilityProblem || "Rewrite configuration is unavailable.";
    }
    for (const button of list.querySelectorAll("button")) button.disabled = busy;
  }

  async function readResponse(response) {
    if (response.status === 401) { location.reload(); throw new Error("Sign in again."); }
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
    return body;
  }

  function show() {
    panel.hidden = false;
    toggle.setAttribute("aria-expanded", "true");
  }

  function version(text, label, exchangeId, side) {
    const section = document.createElement("section");
    const heading = document.createElement("div");
    heading.className = "prompt-version-heading";
    const name = document.createElement("strong");
    name.textContent = label;
    const restore = document.createElement("button");
    restore.type = "button";
    restore.textContent = "restore this version";
    restore.disabled = busy;
    restore.addEventListener("click", () => run("restore", { exchangeId, side }));
    const pre = document.createElement("pre");
    pre.textContent = text;
    heading.append(name, restore);
    section.append(heading, pre);
    return section;
  }

  async function loadHistory(reset = false) {
    show();
    if (reset) { cursors = [null]; page = 0; }
    const request = ++historyRequest;
    const actor = username();
    const query = new URLSearchParams({ user: actor, limit: "20" });
    if (cursors[page]) {
      query.set("beforeTime", cursors[page].time);
      query.set("beforeId", cursors[page].id);
    }
    newer.disabled = older.disabled = true;
    list.textContent = "Loading saved prompt versions…";
    try {
      const body = await readResponse(await fetch(apiUrl(`api/prompt/advice/history?${query}`)));
      if (request !== historyRequest || actor !== username()) return;
      if (!Array.isArray(body.exchanges)) throw new Error("The prompt history response is incomplete.");
      list.replaceChildren();
      for (const exchange of body.exchanges) {
        const article = document.createElement("article");
        article.className = "prompt-rewrite-exchange";
        const heading = document.createElement("h3");
        heading.textContent = `${exchange.model === "restore" ? "Restored version" : exchange.model} · ${new Date(exchange.requestedAtUnixMs).toLocaleString()}`;
        article.append(heading, version(exchange.originalPrompt, "Before this change · exact source", exchange.id, "original"));
        if (exchange.status === "succeeded") {
          article.append(version(exchange.resultPrompt, "Replacement", exchange.id, "result"));
        } else {
          const failure = document.createElement("p");
          failure.textContent = exchange.error || "Request pending. Refresh history to check its result.";
          article.append(failure);
        }
        const details = document.createElement("details");
        const summary = document.createElement("summary");
        summary.textContent = "Instruction and complete exchange";
        const wire = document.createElement("pre");
        wire.textContent = [exchange.instruction, exchange.systemPrompt, exchange.wirePrompt, exchange.rawResponse].filter(Boolean).join("\n\n");
        details.append(summary, wire);
        article.append(details);
        list.append(article);
      }
      refreshDraft();
      if (!body.exchanges.length) list.textContent = "No saved prompt versions yet.";
      const last = body.exchanges.at(-1);
      nextCursor = body.exchanges.length === 20 ? { time: last.requestedAtUnixMs, id: last.id } : null;
      newer.disabled = page === 0;
      older.disabled = !nextCursor;
    } catch (error) {
      if (request !== historyRequest || actor !== username()) return;
      list.textContent = `History unavailable: ${error.message}`;
    }
  }

  async function run(model, extra = {}) {
    if (busy) return;
    const original = promptBox.value;
    if (model !== "restore" && !original.trim()) {
      status.textContent = "Write a prompt first.";
      return;
    }
    const actor = username(), sourceRevision = revision;
    busy = true;
    configure(catalog);
    show();
    status.textContent = model === "restore" ? "Restoring saved text…" : `${model} is expanding the prompt…`;
    try {
      const form = new URLSearchParams();
      Object.entries({ model, prompt: original, user: actor, ...extra }).forEach(([key, value]) => form.append(key, value));
      const body = await readResponse(await fetch(apiUrl("api/prompt/advice"), { method: "POST", body: form }));
      if (typeof body.replacement !== "string" || !body.exchangeId || body.model !== model || body.originalPrompt !== original)
        throw new Error("The rewrite response does not match this request.");
      if (actor !== username()) return;
      if (promptBox.value === original && sourceRevision === revision) {
        applyPrompt(body.replacement);
        revision++;
        status.textContent = model === "restore" ? "Saved version restored." : "Expanded prompt applied. Every saved version remains below.";
      } else {
        status.textContent = "Your prompt changed during the request. The saved replacement is below; restore it to apply.";
      }
    } catch (error) {
      if (actor === username()) status.textContent = error.message;
    } finally {
      busy = false;
      configure(catalog);
      if (actor === username()) await loadHistory(true);
    }
  }

  for (const button of buttons) button.addEventListener("click", () => run(button.dataset.rewriteModel));
  toggle.addEventListener("click", () => {
    if (panel.hidden) loadHistory(true);
    else { panel.hidden = true; toggle.setAttribute("aria-expanded", "false"); }
  });
  document.getElementById("prompt-history-refresh").addEventListener("click", () => loadHistory(true));
  newer.addEventListener("click", () => { if (page > 0) { page--; loadHistory(); } });
  older.addEventListener("click", () => { if (nextCursor) { cursors[++page] = nextCursor; loadHistory(); } });
  return { configure, loadHistory, run, submitted, get busy() { return busy; }, get revision() { return revision; } };
};
