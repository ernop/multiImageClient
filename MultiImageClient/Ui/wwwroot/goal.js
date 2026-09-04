"use strict";
// Goal loops page: a manager model designs prompts toward a goal, one image
// generator renders them, the manager reviews each render in a growing
// conversation. This page shows every entry of that conversation live and
// offers stop / resume / fork-from-here (with optional edits). All URLs
// resolve relative to the page directory so the secret reverse-proxy prefix
// works exactly as it does for index.html.

const appBase = location.pathname.replace(/[^/]*$/, "");
const apiUrl = (path) => {
  const value = String(path);
  if (/^https?:\/\//i.test(value)) return value;
  return appBase + value.replace(/^\//, "");
};
const el = (id) => document.getElementById(id);
const UsernameKey = "mic_username";

function applyEnvironmentBranding() {
  const hostname = location.hostname.toLowerCase().replace(/\.$/, "");
  const isOnline = hostname === "fuseki.net" || hostname.endsWith(".fuseki.net");
  const header = document.querySelector("header");
  el("environment-name").textContent = isOnline ? "-alpha.fuseki.net" : "-local";
  header.classList.toggle("environment-online", isOnline);
  header.classList.toggle("environment-local", !isOnline);
}
applyEnvironmentBranding();

// ---------- identity ----------
// The page reuses the composer's identity rather than asking for a name:
// the authenticated profile's display name when one exists, else the
// creating-as field of the canonical personal configuration document
// (personal-config.js), else the legacy mic_username mirror. Only when none
// exists does a name input appear; what it collects is written the same way
// the composer would read it, so both pages agree afterwards.
const PersonalConfigurationSchema = globalThis.MultiImagePersonalConfiguration;

function storedCreatingAs() {
  try {
    const doc = PersonalConfigurationSchema.parseStored(localStorage.getItem(PersonalConfigurationSchema.StorageKey));
    const value = doc && doc.creatingAs;
    if (typeof value === "string" && value.trim()) return { name: value.trim(), source: "browser" };
  } catch {
    // A malformed document is the settings panel's problem to report; this
    // page must not guess a name from it.
  }
  const legacy = localStorage.getItem(UsernameKey);
  if (legacy && legacy.trim()) return { name: legacy.trim(), source: "browser" };
  return null;
}

// Persist a name typed here: into the canonical document's creatingAs field
// when the document exists (all other fields untouched), else into the
// legacy key the composer migrates from on its next snapshot.
function persistCreatingAs(name) {
  const raw = localStorage.getItem(PersonalConfigurationSchema.StorageKey);
  if (raw) {
    try {
      const doc = PersonalConfigurationSchema.parseStored(raw);
      doc.creatingAs = name;
      localStorage.setItem(PersonalConfigurationSchema.StorageKey, JSON.stringify(doc));
      return;
    } catch {
      // Leave a malformed document alone; the legacy key still reaches the composer.
    }
  }
  localStorage.setItem(UsernameKey, name);
}

let identity = null; // { name, source: "profile" | "browser" | "typed" }

function renderIdentity() {
  const known = el("goal-identity-known");
  const input = el("goal-user");
  if (identity && identity.name) {
    known.hidden = false;
    input.hidden = true;
    el("goal-identity-name").textContent = identity.name;
    el("goal-identity-source").textContent = identity.source === "profile"
      ? "(signed-in profile)"
      : "(the composer's creating-as name)";
    el("goal-identity-change").hidden = identity.source === "profile";
  } else {
    known.hidden = true;
    input.hidden = false;
    input.required = true;
  }
}

function currentIdentityName() {
  if (identity && identity.name) return identity.name;
  return el("goal-user").value.trim().replace(/\s+/g, " ");
}

async function fetchJson(path, init) {
  const resp = await fetch(apiUrl(path), init);
  if (resp.status === 401) {
    location.reload();
    throw new Error("not logged in");
  }
  let body = null;
  try {
    body = await resp.json();
  } catch {
    body = null;
  }
  if (!resp.ok) {
    const message = body && body.error ? body.error : `HTTP ${resp.status}`;
    throw new Error(message);
  }
  return body;
}

// ---------- config + new-loop form ----------

let config = null;

function optionsFor(select, items, selectedKey) {
  select.textContent = "";
  for (const item of items) {
    const option = document.createElement("option");
    option.value = item.key;
    option.textContent = item.label;
    option.disabled = item.disabled === true;
    if (item.title) option.title = item.title;
    if (item.key === selectedKey) option.selected = true;
    select.appendChild(option);
  }
}

async function loadConfig() {
  config = await fetchJson("api/config");
  if (config.auth && config.auth.profile && config.auth.profile.displayName) {
    identity = { name: config.auth.profile.displayName, source: "profile" };
  } else {
    identity = storedCreatingAs()
      || (config.auth && config.auth.user ? { name: config.auth.user, source: "browser" } : null);
  }
  renderIdentity();

  const generators = (config.generators || [])
    .filter((g) => g.kind === "image" && !g.requiresImage && g.key !== "grok-web-video")
    .map((g) => ({
      key: g.key,
      label: g.available ? g.label : `${g.label} — ${g.availabilityProblem || "unavailable"}`,
      disabled: !g.available,
      title: g.detail || "",
    }));
  const firstGenerator = generators.find((g) => !g.disabled);
  optionsFor(el("goal-generator"), generators, firstGenerator ? firstGenerator.key : undefined);

  const managers = (config.goalLoop.managers || []).map((m) => ({
    key: m.key,
    label: m.available ? `${m.label} · ${m.model}` : `${m.label} — ${m.availabilityProblem || "unavailable"}`,
    disabled: !m.available,
    title: m.detail || "",
  }));
  const firstManager = managers.find((m) => !m.disabled);
  optionsFor(el("goal-manager"), managers, firstManager ? firstManager.key : undefined);
  updateManagerDetail();

  optionsFor(el("goal-shape"), (config.shapes || []).map((s) => ({ key: s.key, label: s.label })), "auto");
  optionsFor(el("goal-detail"), (config.details || []).map((d) => ({ key: d.key, label: d.label })), "standard");
  el("goal-max-turns").value = String(config.goalLoop.defaultMaxTurns);
  el("goal-max-turns").max = String(config.goalLoop.maxTurnsCap);
  el("goal-text").maxLength = config.goalLoop.maxGoalChars;
  updateRunningCount(config.goalLoop.runningCount);
}

function updateManagerDetail() {
  const key = el("goal-manager").value;
  const manager = (config && config.goalLoop.managers || []).find((m) => m.key === key);
  const detail = el("goal-manager-detail");
  if (!manager) {
    detail.textContent = "";
    return;
  }
  const price = manager.inputUsdPerMTok != null
    ? ` Published price $${manager.inputUsdPerMTok} in / $${manager.outputUsdPerMTok} out per million tokens.`
    : " No verified token price on file; token counts are shown without a dollar figure.";
  detail.textContent = manager.detail + price;
}
el("goal-manager").addEventListener("change", updateManagerDetail);

function updateRunningCount(count) {
  const span = el("goal-running-count");
  if (typeof count !== "number") {
    span.textContent = "";
    return;
  }
  span.textContent = count === 0 ? "no loops running" : `${count} running`;
  span.classList.toggle("active", count > 0);
}

el("goal-new-toggle").addEventListener("click", () => {
  const panel = el("goal-new");
  const open = panel.hidden;
  panel.hidden = !open;
  el("goal-new-toggle").setAttribute("aria-expanded", String(open));
  el("goal-new-toggle").classList.toggle("open", open);
});

el("goal-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const error = el("goal-form-error");
  error.textContent = "";
  const user = currentIdentityName();
  if (!user) {
    error.textContent = "enter the name to create as";
    return;
  }
  if (!identity || !identity.name) {
    persistCreatingAs(user);
    identity = { name: user, source: "typed" };
    renderIdentity();
  }
  const form = new FormData();
  form.append("goal", el("goal-text").value);
  form.append("user", user);
  form.append("generator", el("goal-generator").value);
  form.append("manager", el("goal-manager").value);
  form.append("maxTurns", el("goal-max-turns").value);
  form.append("goalKind", el("goal-kind").value);
  form.append("shape", el("goal-shape").value);
  form.append("detail", el("goal-detail").value);
  form.append("quality", el("goal-quality").value);
  form.append("moderation", el("goal-moderation").value);
  const button = el("goal-start");
  button.disabled = true;
  try {
    const body = await fetchJson("api/goal-loops", { method: "POST", body: form });
    el("goal-text").value = "";
    await pollList();
    selectLoop(body.id, true);
  } catch (ex) {
    error.textContent = ex.message;
  } finally {
    button.disabled = false;
  }
});

// ---------- loop list ----------

let loopList = [];
let listPollTimer = null;

function statusLabel(status) {
  return {
    running: "running",
    stopped: "stopped",
    done: "done",
    exhausted: "turn limit",
    failed: "failed",
  }[status] || status;
}

function formatScore(value) {
  if (typeof value !== "number") return "—";
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

function formatBytes(n) {
  if (typeof n !== "number") return "";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

function formatUsd(value) {
  if (typeof value !== "number") return "";
  return value < 0.01 ? `$${value.toFixed(4)}` : `$${value.toFixed(2)}`;
}

function formatTime(unixMs) {
  const d = new Date(unixMs);
  return d.toDateString() === new Date().toDateString() ? d.toLocaleTimeString() : d.toLocaleString();
}

function formatDuration(ms) {
  if (typeof ms !== "number" || ms <= 0) return "";
  if (ms < 1000) return `${ms}ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(1)}s`;
  const m = Math.floor(s / 60);
  return `${m}m ${Math.round(s - m * 60)}s`;
}

function renderList() {
  const container = el("goal-list");
  container.textContent = "";
  el("goal-list-count").textContent = loopList.length ? `(${loopList.length})` : "";
  for (const loop of loopList) {
    const item = document.createElement("a");
    item.className = `goal-list-item status-${loop.status}`;
    item.href = `goal.html?loop=${encodeURIComponent(loop.id)}`;
    item.dataset.loopId = loop.id;
    if (selectedLoopId === loop.id) item.classList.add("selected");
    item.addEventListener("click", (event) => {
      event.preventDefault();
      selectLoop(loop.id, true);
    });
    const top = document.createElement("div");
    top.className = "goal-list-top";
    const status = document.createElement("span");
    status.className = `goal-status status-${loop.status}`;
    status.textContent = statusLabel(loop.status);
    const turns = document.createElement("span");
    turns.className = "goal-list-turns";
    turns.textContent = `${loop.turnsRendered}/${loop.maxTurns}`;
    turns.title = "renders completed / max turns";
    const score = document.createElement("span");
    score.className = "goal-list-score";
    score.textContent = loop.bestScore != null ? `best ${formatScore(loop.bestScore)}` : "";
    top.append(status, turns, score);
    const goal = document.createElement("div");
    goal.className = "goal-list-goal";
    goal.textContent = loop.goal;
    const meta = document.createElement("div");
    meta.className = "goal-list-meta";
    meta.textContent = `${loop.generatorLabel} · ${loop.managerLabel} · ${loop.createdBy} · ${formatTime(loop.createdAtUnixMs)}`;
    item.append(top, goal, meta);
    container.appendChild(item);
  }
}

async function pollList() {
  try {
    const body = await fetchJson("api/goal-loops");
    loopList = body.loops || [];
    updateRunningCount(body.runningCount);
    renderList();
  } catch (ex) {
    if (ex.message !== "not logged in") console.warn("goal loop list poll failed", ex);
  }
}

function scheduleListPoll() {
  clearTimeout(listPollTimer);
  listPollTimer = setTimeout(async () => {
    await pollList();
    scheduleListPoll();
  }, document.hidden ? 15000 : 4000);
}

// ---------- selected loop ----------

let selectedLoopId = null;
let selectedVersion = 0;
let loopState = null;      // { loop, running, canControl, entries: [] }
let loopPollTimer = null;
let activityTimer = null;

function selectLoop(id, pushHistory) {
  if (pushHistory) {
    const url = new URL(location.href);
    url.searchParams.set("loop", id);
    history.pushState({ loop: id }, "", url);
  }
  selectedLoopId = id;
  selectedVersion++;
  loopState = null;
  renderedHeadSignature = "";
  el("goal-empty").hidden = true;
  el("goal-loop").hidden = false;
  el("goal-loop-head").textContent = "";
  el("goal-turns").textContent = "";
  renderList();
  clearTimeout(loopPollTimer);
  pollLoop();
}

window.addEventListener("popstate", () => {
  const id = new URL(location.href).searchParams.get("loop");
  if (id) selectLoop(id, false);
});

async function pollLoop() {
  if (!selectedLoopId) return;
  const version = selectedVersion;
  const id = selectedLoopId;
  const after = loopState ? loopState.entries.length : 0;
  try {
    const body = await fetchJson(`api/goal-loops/${encodeURIComponent(id)}?after=${after}`);
    if (version !== selectedVersion) return;
    if (!loopState) {
      loopState = { loop: body.loop, running: body.running, canControl: body.canControl, entries: [], revision: body.loop.revision };
    }
    loopState.loop = body.loop;
    loopState.running = body.running;
    loopState.canControl = body.canControl;
    if (body.total < loopState.entries.length || body.loop.revision !== loopState.revision) {
      // Entries only ever append for one id, except that the runner fills
      // an unrendered render-request in place and bumps the revision.
      // Either way the cursor is stale: refetch everything.
      loopState.entries = [];
      loopState.revision = body.loop.revision;
      el("goal-turns").textContent = "";
      clearTimeout(loopPollTimer);
      loopPollTimer = setTimeout(pollLoop, 50);
      return;
    }
    for (const entry of body.entries) {
      if (entry.index < loopState.entries.length) {
        loopState.entries[entry.index] = entry;
        replaceEntryElement(entry);
      } else {
        loopState.entries.push(entry);
        appendEntryElement(entry);
      }
    }
    renderLoopHead();
    if (body.total > loopState.entries.length) {
      // Cursor gap: ask again immediately from what we have.
      clearTimeout(loopPollTimer);
      loopPollTimer = setTimeout(pollLoop, 50);
      return;
    }
  } catch (ex) {
    if (ex.message === "not logged in") return;
    if (version !== selectedVersion) return;
    const head = el("goal-loop-head");
    if (!loopState) head.textContent = `could not load loop ${id}: ${ex.message}`;
  }
  if (version !== selectedVersion) return;
  const running = loopState && loopState.running;
  clearTimeout(loopPollTimer);
  loopPollTimer = setTimeout(pollLoop, document.hidden ? 5000 : (running ? 1000 : 3000));
}

// ---------- loop head ----------

let renderedHeadSignature = "";

function renderLoopHead() {
  const head = el("goal-loop-head");
  const { loop, running, canControl } = loopState;
  // Rebuild only when the loop metadata changed, so typing in the max-turns
  // box or an opened system-prompt disclosure survives the 1s poll.
  const signature = JSON.stringify([loop, running, canControl]);
  if (signature === renderedHeadSignature && head.childElementCount > 0) return;
  renderedHeadSignature = signature;
  const systemPromptOpen = !!head.querySelector("details.goal-wire[open]");
  head.textContent = "";

  const title = document.createElement("div");
  title.className = "goal-head-title";
  const status = document.createElement("span");
  status.className = `goal-status status-${loop.status}`;
  status.textContent = statusLabel(loop.status);
  const idSpan = document.createElement("span");
  idSpan.className = "goal-head-id";
  idSpan.textContent = `loop ${loop.id}`;
  title.append(status, idSpan);
  if (loop.parentLoopId) {
    const parent = document.createElement("a");
    parent.className = "goal-head-parent";
    parent.href = `goal.html?loop=${encodeURIComponent(loop.parentLoopId)}`;
    parent.textContent = `forked from ${loop.parentLoopId} at entry ${loop.forkedAtEntry}`;
    parent.addEventListener("click", (event) => {
      event.preventDefault();
      selectLoop(loop.parentLoopId, true);
    });
    title.appendChild(parent);
  }
  head.appendChild(title);

  const goal = document.createElement("div");
  goal.className = "goal-head-goal";
  goal.textContent = loop.goal;
  head.appendChild(goal);

  const facts = document.createElement("div");
  facts.className = "goal-facts";
  const fact = (label, value, cls) => {
    const box = document.createElement("div");
    box.className = `goal-fact ${cls || ""}`;
    const v = document.createElement("div");
    v.className = "goal-fact-value";
    v.textContent = value;
    const l = document.createElement("div");
    l.className = "goal-fact-label";
    l.textContent = label;
    box.append(v, l);
    facts.appendChild(box);
    return box;
  };
  fact("turns rendered / max turns", `${loop.turnsRendered} / ${loop.maxTurns}`);
  if (loop.rendersPerTurn > 1) {
    const pair = fact("renders per turn", `${loop.rendersPerTurn} — refine + fresh`, "goal-fact-pair");
    pair.title = "Every turn renders two prompts: REFINE improves the render the manager chose to continue from; FRESH is a from-scratch re-attempt with a new composition and style. The manager scores both and chooses which one the next refine builds on.";
  } else {
    fact("renders per turn", `1 (protocol v${loop.protocolVersion})`);
  }
  fact("last refine score", formatScore(loop.lastScore), "goal-fact-score");
  fact("best score", loop.bestScore != null
    ? `${formatScore(loop.bestScore)} (turn ${loop.bestTurn}${loop.bestVariant ? ` ${loop.bestVariant}` : ""})`
    : "—", "goal-fact-score");
  if (loop.protocolVersion >= 3) {
    const kindBox = fact("goal kind", loop.effectiveGoalKind
      ? `${loop.effectiveGoalKind} (${loop.goalKindSource === "operator" ? "set by you" : "manager's classification"})`
      : (loop.goalKind === "auto" ? "not yet classified by the manager" : loop.goalKind), `goal-fact-kind kind-${loop.effectiveGoalKind || "unknown"}`);
    kindBox.title = loop.effectiveGoalKind === "open-ended"
      ? "Open-ended: the loop objects to any \"done\" until a later render scores below the best one (the generator's limit is demonstrated). The turn budget is never a reason to stop."
      : "Bounded: done when the manager judges the finished state met.";
  } else {
    fact("goal kind", `protocol v${loop.protocolVersion}: no goal kinds`);
  }
  fact("image generator", loop.generatorLabel);
  fact("manager", `${loop.managerLabel}`);
  fact("model", loop.managerModel);
  fact("output", `${loop.shape} · ${loop.detail} · ${loop.quality} · moderation ${loop.moderation}`);
  const managerCost = loop.managerCostKnown ? formatUsd(loop.managerCostUsd) : `${formatUsd(loop.managerCostUsd)}+ (price unknown for this model)`;
  fact("manager cost", `${managerCost} · ${loop.managerInputTokens.toLocaleString()} in / ${loop.managerOutputTokens.toLocaleString()} out tokens`);
  fact("render cost (estimate)", formatUsd(loop.renderCostUsd) || "$0.00");
  fact("created", `${loop.createdBy} · ${formatTime(loop.createdAtUnixMs)}`);
  head.appendChild(facts);

  if (loop.statusDetail) {
    const detail = document.createElement("div");
    detail.className = `goal-status-detail ${loop.status === "failed" ? "goal-error" : ""}`;
    detail.textContent = loop.statusDetail;
    head.appendChild(detail);
  }

  const activity = document.createElement("div");
  activity.className = "goal-activity";
  activity.id = "goal-activity";
  head.appendChild(activity);
  updateActivity();

  const controls = document.createElement("div");
  controls.className = "goal-controls";
  if (running) {
    const stop = document.createElement("button");
    stop.type = "button";
    stop.textContent = "stop";
    stop.disabled = !canControl;
    stop.title = canControl
      ? "Stop after the in-flight step finishes. Resume continues from the last recorded entry."
      : "Only the loop's creator can stop it";
    stop.addEventListener("click", () => controlLoop("stop", null, stop));
    controls.appendChild(stop);
  } else if (loop.status !== "done") {
    const turnsInput = document.createElement("input");
    turnsInput.type = "number";
    turnsInput.min = "1";
    turnsInput.max = String(config.goalLoop.maxTurnsCap);
    turnsInput.value = String(loop.status === "exhausted" ? Math.min(loop.maxTurns + 3, config.goalLoop.maxTurnsCap) : loop.maxTurns);
    turnsInput.title = "max turns for the resumed loop";
    const turnsLabel = document.createElement("label");
    turnsLabel.className = "goal-inline-field";
    turnsLabel.append("max turns ", turnsInput);
    const resume = document.createElement("button");
    resume.type = "button";
    resume.textContent = loop.status === "exhausted" ? "resume with more turns" : "resume";
    resume.disabled = !canControl;
    resume.title = canControl
      ? "Continue from the last recorded entry (re-asks the manager or re-renders the interrupted step)"
      : "Only the loop's creator can resume it";
    resume.addEventListener("click", () => controlLoop("resume", { maxTurns: turnsInput.value }, resume));
    controls.append(resume, turnsLabel);
  } else {
    const doneNote = document.createElement("span");
    doneNote.className = "goal-done-note";
    doneNote.textContent = "The manager declared this loop done. Fork from any entry to continue differently.";
    controls.appendChild(doneNote);
  }
  const controlError = document.createElement("span");
  controlError.className = "goal-error";
  controlError.id = "goal-control-error";
  controls.appendChild(controlError);
  head.appendChild(controls);

  // All-turns contact sheet: one PNG with every rendered turn, its score,
  // the exact prompt, and the manager's assessment (like the composer's
  // combined contact sheet, one cell per turn instead of per generator).
  const sheetRow = document.createElement("div");
  sheetRow.className = "goal-controls goal-sheet-row";
  const stale = loop.sheetFile && loop.sheetEntryCount != null && loop.sheetEntryCount < loop.entryCount;
  if (loop.turnsRendered > 0) {
    const build = document.createElement("button");
    build.type = "button";
    build.className = loop.sheetFile ? "goal-secondary" : "";
    build.textContent = loop.sheetFile
      ? (stale ? `rebuild all-turns sheet (${loop.entryCount - loop.sheetEntryCount} new entries since)` : "rebuild all-turns sheet")
      : "build all-turns contact sheet";
    build.title = "Render one large PNG containing every rendered turn in order with its score, prompt, and the manager's assessment";
    build.addEventListener("click", () => buildSheet(build));
    sheetRow.appendChild(build);
  }
  if (loop.sheetFile) {
    const link = document.createElement("a");
    link.className = "goal-sheet-link";
    link.href = apiUrl(`api/goal-loops/${encodeURIComponent(loop.id)}/sheet?v=${loop.sheetEntryCount}`);
    link.target = "_blank";
    link.rel = "noopener";
    link.textContent = `open all-turns sheet (${loop.sheetTurns} turn${loop.sheetTurns === 1 ? "" : "s"})${stale ? " — built before the latest entries" : ""}`;
    sheetRow.appendChild(link);
  }
  const sheetError = document.createElement("span");
  sheetError.className = "goal-error";
  sheetError.id = "goal-sheet-error";
  sheetRow.appendChild(sheetError);
  if (sheetRow.childElementCount > 1) head.appendChild(sheetRow);

  const system = document.createElement("details");
  system.className = "goal-wire";
  system.open = systemPromptOpen;
  const summary = document.createElement("summary");
  summary.textContent = `manager system prompt (protocol v${loop.protocolVersion}, sent with every manager call)`;
  const pre = document.createElement("pre");
  pre.textContent = loop.systemPrompt;
  system.append(summary, pre);
  head.appendChild(system);
}

function updateActivity() {
  clearInterval(activityTimer);
  const box = el("goal-activity");
  if (!box || !loopState) return;
  const { loop, running } = loopState;
  if (!running || !loop.activity) {
    box.textContent = running ? "running — deciding the next step…" : "";
    box.classList.toggle("live", running);
    return;
  }
  box.classList.add("live");
  const paint = () => {
    const elapsed = loop.activitySinceUnixMs ? Math.max(0, Math.round((Date.now() - loop.activitySinceUnixMs) / 1000)) : 0;
    box.textContent = `${loop.activity} — ${elapsed}s`;
  };
  paint();
  activityTimer = setInterval(paint, 1000);
}

async function buildSheet(button) {
  const error = el("goal-sheet-error");
  if (error) error.textContent = "";
  const label = button.textContent;
  button.disabled = true;
  button.textContent = "building sheet…";
  try {
    await fetchJson(`api/goal-loops/${encodeURIComponent(selectedLoopId)}/sheet`, { method: "POST" });
    clearTimeout(loopPollTimer);
    await pollLoop();
  } catch (ex) {
    if (error) error.textContent = ex.message;
    button.disabled = false;
    button.textContent = label;
  }
}

async function controlLoop(action, params, button) {
  const error = el("goal-control-error");
  if (error) error.textContent = "";
  if (button) button.disabled = true;
  const form = new FormData();
  if (params && params.maxTurns) form.append("maxTurns", params.maxTurns);
  try {
    await fetchJson(`api/goal-loops/${encodeURIComponent(selectedLoopId)}/${action}`, { method: "POST", body: form });
    clearTimeout(loopPollTimer);
    await pollLoop();
    await pollList();
  } catch (ex) {
    if (error) error.textContent = ex.message;
    if (button) button.disabled = false;
  }
}

// ---------- entries ----------

const PartyLabels = { user: "you", manager: "manager", generator: "image generator", system: "loop" };
const KindLabels = {
  goal: "goal",
  design: "first design",
  "render-request": "render request",
  "render-result": "render result",
  "review-request": "review request",
  review: "review + next design",
  objection: "objection — \"done\" not accepted",
  note: "note",
};

const VariantLabels = { refine: "refine", fresh: "fresh (from scratch)" };

function variantOf(entry) {
  return (entry.render && entry.render.variant) || null;
}

// The label states what the manager decided, when that is known; a reply
// that failed the contract is labeled as such rather than as a design.
function entryKindLabel(entry) {
  const parsed = entry.manager && entry.manager.parsed;
  const refused = Boolean(entry.manager && entry.manager.providerStop);
  if (entry.kind === "render-request" || entry.kind === "render-result") {
    const variant = variantOf(entry);
    return variant ? `${KindLabels[entry.kind]} — ${VariantLabels[variant] || variant}` : KindLabels[entry.kind];
  }
  if (entry.kind === "review") {
    if (refused) return "review — provider refused";
    if (entry.error && !parsed) return "review — reply rejected";
    if (parsed && parsed.decision === "done") {
      return isObjectedReview(entry) ? "review + done (objected to below)" : "review + done";
    }
    return KindLabels.review;
  }
  if (entry.kind === "design" && refused) return "first design — provider refused";
  if (entry.kind === "design" && entry.error && !parsed) return "first design — reply rejected";
  return KindLabels[entry.kind] || entry.kind;
}

// A review whose "done" the loop rejected is followed by an objection entry
// (before any other control-flow entry); its label says so.
function isObjectedReview(entry) {
  if (!loopState) return false;
  for (let i = entry.index + 1; i < loopState.entries.length; i++) {
    const next = loopState.entries[i];
    if (next.kind === "note") continue;
    return next.kind === "objection";
  }
  return false;
}

// ---------- prompt diffs ----------
// Word-level diff (longest common subsequence over whitespace-delimited
// tokens, whitespace preserved) between the prompt of one render and the
// previous one, so the change from turn to turn is visible in place.
const DiffMaxTokens = 4000;

function tokenizeWords(text) {
  return (text || "").match(/\S+|\s+/g) || [];
}

function diffTokens(oldTokens, newTokens) {
  const a = oldTokens.filter((t) => t.trim().length);
  const b = newTokens.filter((t) => t.trim().length);
  const n = a.length;
  const m = b.length;
  // dp[i][j] = LCS length of a[i..] and b[j..]; Int32 table, n*m cells.
  const width = m + 1;
  const dp = new Int32Array((n + 1) * width);
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      dp[i * width + j] = a[i] === b[j]
        ? dp[(i + 1) * width + j + 1] + 1
        : Math.max(dp[(i + 1) * width + j], dp[i * width + j + 1]);
    }
  }
  const ops = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) { ops.push(["=", a[i]]); i++; j++; }
    else if (dp[(i + 1) * width + j] >= dp[i * width + j + 1]) { ops.push(["-", a[i]]); i++; }
    else { ops.push(["+", b[j]]); j++; }
  }
  while (i < n) ops.push(["-", a[i++]]);
  while (j < m) ops.push(["+", b[j++]]);
  return ops;
}

// Returns { node, added, removed } or null when the texts are identical.
function promptDiffBlock(oldText, newText) {
  if ((oldText || "") === (newText || "")) return null;
  const oldTokens = tokenizeWords(oldText);
  const newTokens = tokenizeWords(newText);
  const pre = document.createElement("pre");
  pre.className = "goal-text goal-prompt goal-prompt-diff";
  if (oldTokens.length > DiffMaxTokens || newTokens.length > DiffMaxTokens) {
    pre.textContent = `(prompts too long to diff word by word: ${oldTokens.length} / ${newTokens.length} tokens)`;
    return { node: pre, added: null, removed: null };
  }
  const ops = diffTokens(oldTokens, newTokens);
  let added = 0;
  let removed = 0;
  // Group consecutive same-kind ops so a changed phrase is one span.
  let run = null;
  const flush = () => {
    if (!run) return;
    const text = run.words.join(" ");
    if (run.kind === "=") {
      pre.appendChild(document.createTextNode(text));
    } else {
      const span = document.createElement(run.kind === "+" ? "ins" : "del");
      span.textContent = text;
      pre.appendChild(span);
    }
    pre.appendChild(document.createTextNode(" "));
    run = null;
  };
  for (const [kind, word] of ops) {
    if (kind === "+") added++;
    if (kind === "-") removed++;
    if (run && run.kind === kind) run.words.push(word);
    else { flush(); run = { kind, words: [word] }; }
  }
  flush();
  return { node: pre, added, removed };
}

// The prompt block plus a "changes vs …" view when a previous prompt exists.
// The diff view is shown first; the plain text stays one click away.
function promptWithDiff(label, text, previous) {
  const diff = previous == null ? null : promptDiffBlock(previous.text, text);
  if (!diff) {
    const plain = labeled(label, textBlock(text, "goal-prompt"));
    if (previous != null) {
      const same = document.createElement("div");
      same.className = "goal-diff-summary";
      same.textContent = `identical to ${previous.label}`;
      plain.appendChild(same);
    }
    return plain;
  }
  const box = document.createElement("div");
  box.className = "goal-labeled goal-prompt-with-diff";
  const header = document.createElement("div");
  header.className = "goal-sublabel goal-diff-header";
  const title = document.createElement("span");
  title.textContent = label;
  const summary = document.createElement("span");
  summary.className = "goal-diff-summary";
  summary.textContent = diff.added == null
    ? `changed vs ${previous.label}`
    : `vs ${previous.label}: +${diff.added} word${diff.added === 1 ? "" : "s"}, −${diff.removed} word${diff.removed === 1 ? "" : "s"}`;
  const toggle = document.createElement("button");
  toggle.type = "button";
  toggle.className = "goal-diff-toggle";
  toggle.textContent = "show plain text";
  header.append(title, summary, toggle);
  const plain = textBlock(text, "goal-prompt");
  plain.hidden = true;
  toggle.addEventListener("click", () => {
    const showingDiff = !diff.node.hidden;
    diff.node.hidden = showingDiff;
    plain.hidden = !showingDiff;
    toggle.textContent = showingDiff ? "show changes" : "show plain text";
  });
  box.append(header, diff.node, plain);
  return box;
}

// The prompt rendered before this entry (a render-request earlier in the
// list), for diffing; null on the first render.
function previousRenderedPrompt(beforeIndex) {
  if (!loopState) return null;
  for (let i = beforeIndex - 1; i >= 0; i--) {
    const e = loopState.entries[i];
    if (e.kind === "render-request") return { text: e.text, label: `turn ${e.turn} prompt` };
  }
  return null;
}

// The prompt rendered in this same turn (the review compares the manager's
// next design against what was just rendered). With two renders per turn,
// variant selects which one; null matches a single-render turn.
function renderedPromptOfTurn(turn, beforeIndex, variant) {
  if (!loopState) return null;
  for (let i = beforeIndex - 1; i >= 0; i--) {
    const e = loopState.entries[i];
    if (e.kind === "render-request" && e.turn === turn && (variant === undefined || variantOf(e) === variant)) {
      const v = variantOf(e);
      return { text: e.text, label: v ? `turn ${turn} ${v} prompt` : `turn ${turn} prompt` };
    }
  }
  return null;
}

// The prompt a REFINE render-request builds on: the previous turn's render
// that the manager chose to continue from. Single-render loops compare
// against the previous rendered prompt.
function lineageBasePrompt(entry) {
  const variant = variantOf(entry);
  if (!variant) return previousRenderedPrompt(entry.index);
  if (variant !== "refine") return null;
  for (let i = entry.index - 1; i >= 0; i--) {
    const e = loopState.entries[i];
    if ((e.kind === "review" || e.kind === "design") && e.manager && e.manager.parsed && !e.error) {
      const from = e.manager.parsed.continueFrom;
      return from ? renderedPromptOfTurn(e.turn, entry.index, from) : null;
    }
  }
  return null;
}

// A fresh prompt is a from-scratch re-attempt: diffing it against anything
// would only show noise, so it is shown plain with that statement.
function freshPromptBlock(label, text) {
  const box = labeled(label, textBlock(text, "goal-prompt goal-prompt-fresh"));
  const note = document.createElement("div");
  note.className = "goal-diff-summary";
  note.textContent = "from scratch — new composition and style, not compared to earlier prompts";
  box.appendChild(note);
  return box;
}

function turnSection(turn) {
  const container = el("goal-turns");
  let section = container.querySelector(`.goal-turn[data-turn="${turn}"]`);
  if (!section) {
    section = document.createElement("section");
    section.className = "goal-turn";
    section.dataset.turn = String(turn);
    const heading = document.createElement("h3");
    heading.className = "goal-turn-heading";
    heading.textContent = turn === 0 ? "Start" : `Turn ${turn}`;
    section.appendChild(heading);
    container.appendChild(section);
  }
  return section;
}

function appendEntryElement(entry) {
  turnSection(entry.turn).appendChild(buildEntryElement(entry));
  if (entry.kind === "review" && !entry.error && entry.manager && entry.manager.parsed) {
    // The turn's render results show their score and the chosen-lineage
    // mark, both of which this review decides.
    for (let i = entry.index - 1; i >= 0; i--) {
      const prev = loopState.entries[i];
      if (prev.turn !== entry.turn) break;
      if (prev.kind === "render-result") replaceEntryElement(prev);
    }
  }
  if (entry.kind === "objection") {
    // The objected-to review's label depends on this later entry.
    for (let i = entry.index - 1; i >= 0; i--) {
      const prev = loopState.entries[i];
      if (prev.kind === "note") continue;
      if (prev.kind === "review") replaceEntryElement(prev);
      break;
    }
  }
  if (entry.kind === "render-result" && entry.render && entry.render.ok) viewer.refresh();
}

function replaceEntryElement(entry) {
  const existing = el("goal-turns").querySelector(`.goal-entry[data-index="${entry.index}"]`);
  const fresh = buildEntryElement(entry);
  if (existing) existing.replaceWith(fresh);
  else turnSection(entry.turn).appendChild(fresh);
}

function party(name) {
  const span = document.createElement("span");
  span.className = `goal-party party-${name}`;
  span.textContent = PartyLabels[name] || name;
  return span;
}

function textBlock(text, cls) {
  const pre = document.createElement("pre");
  pre.className = `goal-text ${cls || ""}`;
  pre.textContent = text || "";
  return pre;
}

function labeled(label, node, cls) {
  const box = document.createElement("div");
  box.className = `goal-labeled ${cls || ""}`;
  const l = document.createElement("div");
  l.className = "goal-sublabel";
  l.textContent = label;
  box.append(l, node);
  return box;
}

function detailsBlock(summaryText, node, open) {
  const details = document.createElement("details");
  details.className = "goal-details";
  if (open) details.open = true;
  const summary = document.createElement("summary");
  summary.textContent = summaryText;
  details.append(summary, node);
  return details;
}

function prettyJson(text) {
  if (!text) return "";
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

// Provider payloads carry JSON *inside* strings: the manager's reply is a
// JSON object serialized into a "text" field, and every replayed assistant
// turn in the request is the same. For reading, a string whose whole value
// parses as a JSON object or array is shown as that object; every other
// value is untouched. Returns null when nothing was expandable, so the caller
// shows only the verbatim block.
function expandNestedJson(value) {
  let expanded = false;
  const walk = (v) => {
    if (typeof v === "string") {
      const t = v.trim();
      if ((t.startsWith("{") && t.endsWith("}")) || (t.startsWith("[") && t.endsWith("]"))) {
        try {
          const parsed = JSON.parse(t);
          if (parsed && typeof parsed === "object") {
            expanded = true;
            return walk(parsed);
          }
        } catch {
          // Not JSON; leave the string as it is.
        }
      }
      return v;
    }
    if (Array.isArray(v)) return v.map(walk);
    if (v && typeof v === "object") {
      const out = {};
      for (const [k, inner] of Object.entries(v)) out[k] = walk(inner);
      return out;
    }
    return v;
  };
  const result = walk(value);
  return expanded ? result : null;
}

// One wire payload: the readable expansion first when it exists, the exact
// verbatim text always available beneath it.
function wireBlock(label, text) {
  const wrap = document.createElement("div");
  let expanded = null;
  try {
    expanded = expandNestedJson(JSON.parse(text));
  } catch {
    expanded = null;
  }
  if (expanded) {
    wrap.appendChild(labeled(`${label} — embedded JSON strings expanded for reading`, textBlock(JSON.stringify(expanded, null, 2), "goal-wire-text")));
    wrap.appendChild(detailsBlock(`${label} — verbatim`, textBlock(prettyJson(text), "goal-wire-text"), false));
  } else {
    wrap.appendChild(labeled(label, textBlock(prettyJson(text), "goal-wire-text")));
  }
  return wrap;
}

function thumbFor(imageUrl, thumbUrl) {
  if (thumbUrl) return apiUrl(thumbUrl);
  if (!imageUrl) return "";
  if (/^https?:\/\//i.test(imageUrl)) return imageUrl;
  return apiUrl(imageUrl + (imageUrl.includes("?") ? "&thumb=1" : "?thumb=1"));
}

// ---------- shared viewer over this loop's renders ----------
// One item per successful render, in turn order. The viewer re-reads this
// list on every step, so turns that finish while it is open join the walk.
function viewerItemId(render) {
  return `${render.jobId}|${render.generatorKey}|0`;
}

function loopViewerItems() {
  if (!loopState) return [];
  const items = [];
  // Walk order: turn, then refine before fresh (results land on file in
  // completion order).
  const VariantOrder = { refine: 0, fresh: 1 };
  const results = loopState.entries
    .filter((entry) => entry.kind === "render-result" && entry.render && entry.render.ok && entry.render.imageUrl)
    .sort((a, b) => a.turn - b.turn
      || (VariantOrder[variantOf(a)] || 0) - (VariantOrder[variantOf(b)] || 0)
      || a.index - b.index);
  for (const entry of results) {
    const r = entry.render;
    const variant = variantOf(entry);
    const request = renderedPromptOfTurn(entry.turn, entry.index, variant === null ? undefined : variant);
    const reviewed = reviewOfRender(entry);
    const review = reviewed ? reviewed.parsed : null;
    const evaluation = reviewed ? reviewed.evaluation : null;
    const match = /^(\d+)x(\d+)$/.exec(r.size || "");
    const meta = [
      { label: "generator", value: r.generatorLabel },
      { label: "pixels", value: r.size },
      { label: "render time", value: formatDuration(entry.ms) },
      { label: "cost (estimate)", value: r.cost != null ? formatUsd(r.cost) : "" },
      { label: "job", value: r.jobId },
    ];
    if (variant) {
      meta.unshift({ label: "render", value: variant === "fresh" ? "fresh — from-scratch re-attempt" : "refine — continues the chosen lineage" });
    }
    if (evaluation) {
      meta.push({ label: "goal met", value: evaluation.goalMet ? "yes" : "no" });
      if (evaluation.problems && evaluation.problems.length) {
        meta.push({ label: "problems", value: evaluation.problems.join("; ") });
      }
      meta.push({
        label: "decision",
        value: review.decision === "done"
          ? "done"
          : (review.continueFrom ? `render again — continue from the ${review.continueFrom} render` : "render again"),
      });
    }
    items.push({
      id: viewerItemId(r),
      url: r.imageUrl,
      thumbUrl: r.thumbUrl || (/^https?:\/\//i.test(r.imageUrl) ? "" : r.imageUrl + (r.imageUrl.includes("?") ? "&thumb=1" : "?thumb=1")),
      width: match ? Number(match[1]) : 0,
      height: match ? Number(match[2]) : 0,
      title: `Turn ${entry.turn} of ${loopState.loop.maxTurns}${variant ? ` — ${variant} render` : ""} — loop ${loopState.loop.id}`,
      subtitle: evaluation
        ? `${formatScore(evaluation.score)}/10 — ${evaluation.assessment}`
        : "not yet reviewed by the manager",
      prompt: request ? request.text : "",
      meta,
      actions: [
        {
          label: "open original in new tab",
          title: "the exact full-resolution file",
          run: () => window.open(apiUrl(r.imageUrl), "_blank", "noopener"),
        },
        {
          label: "scroll to this turn",
          title: "close the viewer and scroll the page to this render",
          run: () => {
            viewer.close();
            const node = el("goal-turns").querySelector(`.goal-entry[data-index="${entry.index}"]`);
            if (node) node.scrollIntoView({ block: "center", behavior: "smooth" });
          },
        },
      ],
    });
  }
  return items;
}

const viewer = MultiImageViewer.create({
  items: loopViewerItems,
  resolveUrl: apiUrl,
});

function imageFigure(imageUrl, thumbUrl, sizeText, alt, viewerId) {
  const link = document.createElement("a");
  link.href = apiUrl(imageUrl);
  link.target = "_blank";
  link.rel = "noopener";
  link.className = "goal-image-link";
  link.title = viewerId
    ? "open in the viewer (arrow keys / wheel walk every render of this loop); Ctrl-click for a new tab"
    : "open the full-resolution image in a new tab";
  const img = document.createElement("img");
  img.src = thumbFor(imageUrl, thumbUrl);
  img.alt = alt || "rendered image";
  img.loading = "lazy";
  img.decoding = "async";
  const match = /^(\d+)x(\d+)$/.exec(sizeText || "");
  if (match) img.style.aspectRatio = `${match[1]} / ${match[2]}`;
  link.appendChild(img);
  if (viewerId) {
    link.addEventListener("click", (event) => {
      if (event.ctrlKey || event.metaKey || event.shiftKey || event.button !== 0) return;
      event.preventDefault();
      if (!viewer.open(viewerId)) window.open(link.href, "_blank", "noopener");
    });
    let hoverTimer = null;
    link.addEventListener("pointerenter", () => {
      hoverTimer = setTimeout(() => viewer.prefetch(imageUrl), 150);
    });
    link.addEventListener("pointerleave", () => clearTimeout(hoverTimer));
  }
  return link;
}

function scoreBlock(evaluation) {
  const box = document.createElement("div");
  box.className = "goal-score";
  const number = document.createElement("div");
  number.className = "goal-score-number";
  number.textContent = formatScore(evaluation.score);
  const denominator = document.createElement("span");
  denominator.className = "goal-score-denominator";
  denominator.textContent = "/10";
  number.appendChild(denominator);
  const met = document.createElement("div");
  met.className = `goal-goal-met ${evaluation.goalMet ? "met" : "not-met"}`;
  met.textContent = evaluation.goalMet ? "goal met" : "goal not met";
  box.append(number, met);
  return box;
}

function listBlock(items, cls) {
  const ul = document.createElement("ul");
  ul.className = `goal-list-items ${cls || ""}`;
  for (const item of items) {
    const li = document.createElement("li");
    li.textContent = item;
    ul.appendChild(li);
  }
  return ul;
}

function managerBody(entry) {
  const body = document.createElement("div");
  body.className = "goal-manager-body";
  const data = entry.manager || {};
  const parsed = data.parsed;
  if (entry.error || !parsed) {
    const err = document.createElement("div");
    err.className = "goal-error goal-error-block";
    err.textContent = data.providerStop
      ? `The manager did not answer. The provider declared the reply abnormal, and the loop does not advance. Resume retries this step with the same manager. ${data.providerStop}`
      : `This reply did not follow the JSON contract and does not advance the loop: ${entry.error || data.parseError || "unparseable"}`;
    body.appendChild(err);
    if (!data.providerStop || entry.text) {
      body.appendChild(labeled("raw reply", textBlock(entry.text)));
    }
  } else {
    const pair = Boolean(parsed.freshEvaluation || parsed.freshPrompt);
    const evaluationBlock = (evaluation, heading, chosen) => {
      const evalRow = document.createElement("div");
      evalRow.className = `goal-eval-row ${chosen ? "chosen" : ""}`;
      evalRow.appendChild(scoreBlock(evaluation));
      const evalText = document.createElement("div");
      evalText.className = "goal-eval-text";
      if (heading) {
        const h = document.createElement("div");
        h.className = "goal-eval-heading";
        h.textContent = heading + (chosen ? " — continue from this one" : "");
        evalText.appendChild(h);
      }
      evalText.appendChild(labeled("assessment of the rendered image", textBlock(evaluation.assessment)));
      if (evaluation.problems && evaluation.problems.length) {
        evalText.appendChild(labeled("problems", listBlock(evaluation.problems, "problems")));
      }
      if (evaluation.keep && evaluation.keep.length) {
        evalText.appendChild(labeled("keep", listBlock(evaluation.keep, "keep")));
      }
      evalRow.appendChild(evalText);
      return evalRow;
    };
    if (parsed.evaluation) {
      body.appendChild(evaluationBlock(parsed.evaluation, pair ? "refine render" : null, pair && parsed.continueFrom === "refine"));
    }
    if (parsed.freshEvaluation) {
      body.appendChild(evaluationBlock(parsed.freshEvaluation, "fresh render (from scratch)", parsed.continueFrom === "fresh"));
    }
    const decision = document.createElement("div");
    decision.className = `goal-decision decision-${parsed.decision}`;
    decision.textContent = parsed.decision === "done"
      ? "decision: done — stop the loop"
      : (parsed.evaluation
        ? (pair ? `decision: render again — continue from the ${parsed.continueFrom || "?"} render` : "decision: render again with a new design")
        : (pair ? "decision: render these two first designs" : "decision: render this first design"));
    if (parsed.bestTurn) {
      const best = document.createElement("span");
      best.className = "goal-best-turn";
      best.textContent = ` best so far: turn ${parsed.bestTurn}${parsed.bestVariant ? ` ${parsed.bestVariant}` : ""}`;
      decision.appendChild(best);
    }
    body.appendChild(decision);
    if (parsed.doneStatement) {
      body.appendChild(labeled("done statement", textBlock(parsed.doneStatement)));
    }
    if (parsed.prompt) {
      if (!parsed.evaluation) {
        body.appendChild(labeled(pair ? "refine prompt to render (primary design)" : "prompt to render", textBlock(parsed.prompt, "goal-prompt")));
      } else if (pair) {
        const base = parsed.continueFrom ? renderedPromptOfTurn(entry.turn, entry.index, parsed.continueFrom) : null;
        body.appendChild(promptWithDiff(`next refine prompt (builds on the turn ${entry.turn} ${parsed.continueFrom || ""} render)`, parsed.prompt, base));
      } else {
        body.appendChild(promptWithDiff("next prompt to render", parsed.prompt, renderedPromptOfTurn(entry.turn, entry.index)));
      }
    }
    if (parsed.designNotes) {
      body.appendChild(labeled(pair ? "refine design notes" : "design notes", textBlock(parsed.designNotes)));
    }
    if (parsed.freshPrompt) {
      body.appendChild(freshPromptBlock("next fresh prompt", parsed.freshPrompt));
    }
    if (parsed.freshDesignNotes) {
      body.appendChild(labeled("fresh design notes", textBlock(parsed.freshDesignNotes)));
    }
    body.appendChild(detailsBlock(`manager reasoning (${parsed.reasoning.length.toLocaleString()} chars)`, textBlock(parsed.reasoning), true));
  }
  if (data.providerReasoning) {
    body.appendChild(detailsBlock(`provider thinking / reasoning summary (${data.providerReasoning.length.toLocaleString()} chars)`, textBlock(data.providerReasoning), false));
  }
  const usage = document.createElement("div");
  usage.className = "goal-usage";
  const parts = [];
  if (data.model) parts.push(data.model);
  if (data.inputTokens != null) parts.push(`${data.inputTokens.toLocaleString()} in`);
  if (data.outputTokens != null) parts.push(`${data.outputTokens.toLocaleString()} out tokens`);
  if (data.costUsd != null) parts.push(formatUsd(data.costUsd));
  else if (data.inputTokens != null) parts.push("price not on file");
  if (data.requestBytes != null) parts.push(`request ${formatBytes(data.requestBytes)}`);
  usage.textContent = parts.join(" · ");
  body.appendChild(usage);
  return body;
}

function renderRequestBody(entry) {
  const body = document.createElement("div");
  const variant = variantOf(entry);
  if (variant === "fresh") {
    body.appendChild(freshPromptBlock("fresh prompt sent to the image generator", entry.text));
  } else {
    const base = lineageBasePrompt(entry);
    const label = variant === "refine"
      ? (base ? `refine prompt sent to the image generator (builds on ${base.label.replace(/ prompt$/, " render")})` : "refine prompt sent to the image generator (primary design)")
      : "prompt sent to the image generator";
    body.appendChild(promptWithDiff(label, entry.text, base));
  }
  const r = entry.render || {};
  const meta = document.createElement("div");
  meta.className = "goal-usage";
  meta.textContent = [
    r.generatorLabel,
    r.shape && `shape ${r.shape}`,
    r.detail && `detail ${r.detail}`,
    r.quality && `quality ${r.quality}`,
    r.moderation && `moderation ${r.moderation}`,
    r.jobId ? `job ${r.jobId}` : "not rendered yet",
  ].filter(Boolean).join(" · ");
  body.appendChild(meta);
  return body;
}

// The variant the manager's accepted review of this turn chose to continue
// from, or null while the turn is unreviewed / on single-render loops.
function chosenVariantOfTurn(turn, afterIndex) {
  if (!loopState) return null;
  for (let i = afterIndex + 1; i < loopState.entries.length; i++) {
    const e = loopState.entries[i];
    if (e.kind === "review" && e.turn === turn && !e.error && e.manager && e.manager.parsed && e.manager.parsed.continueFrom) {
      return e.manager.parsed.continueFrom;
    }
  }
  return null;
}

// The review of this render (first accepted review of the same turn after it)
// and its evaluation of this render's variant.
function reviewOfRender(entry) {
  if (!loopState) return null;
  const variant = variantOf(entry);
  for (let i = entry.index + 1; i < loopState.entries.length; i++) {
    const e = loopState.entries[i];
    if (e.kind === "review" && e.turn === entry.turn && !e.error && e.manager && e.manager.parsed) {
      const evaluation = variant === "fresh" ? e.manager.parsed.freshEvaluation : e.manager.parsed.evaluation;
      if (evaluation) return { parsed: e.manager.parsed, evaluation };
    }
  }
  return null;
}

function renderResultBody(entry) {
  const body = document.createElement("div");
  const r = entry.render || {};
  const variant = variantOf(entry);
  if (variant) {
    const badge = document.createElement("div");
    badge.className = `goal-variant-badge variant-${variant}`;
    badge.textContent = variant === "fresh" ? "FRESH — from-scratch re-attempt" : "REFINE — continues the chosen lineage";
    const chosen = chosenVariantOfTurn(entry.turn, entry.index);
    if (chosen === variant) {
      const mark = document.createElement("span");
      mark.className = "goal-chosen-mark";
      mark.textContent = "manager continues from this one";
      badge.appendChild(mark);
    }
    body.appendChild(badge);
  }
  if (r.ok) {
    const row = document.createElement("div");
    row.className = "goal-render-row";
    row.appendChild(imageFigure(r.imageUrl, r.thumbUrl, r.size, `turn ${entry.turn}${variant ? ` ${variant}` : ""} render`, viewerItemId(r)));
    const facts = document.createElement("div");
    facts.className = "goal-render-facts";
    const fact = (label, value) => {
      if (!value) return;
      const box = document.createElement("div");
      box.className = "goal-fact";
      const v = document.createElement("div");
      v.className = "goal-fact-value";
      v.textContent = value;
      const l = document.createElement("div");
      l.className = "goal-fact-label";
      l.textContent = label;
      box.append(v, l);
      facts.appendChild(box);
    };
    const reviewed = reviewOfRender(entry);
    if (reviewed) fact("manager score", `${formatScore(reviewed.evaluation.score)}/10`);
    fact("pixels", r.size);
    fact("render time", formatDuration(entry.ms));
    fact("cost (estimate)", r.cost != null ? formatUsd(r.cost) : "");
    fact("generator", r.generatorLabel);
    fact("provider label", r.label);
    fact("media type", r.mediaType);
    fact("job", r.jobId);
    row.appendChild(facts);
    body.appendChild(row);
  } else {
    const err = document.createElement("div");
    err.className = "goal-error goal-error-block";
    err.textContent = `render failed: ${entry.error || "no error text"}`;
    body.appendChild(err);
    if (r.errorHint) {
      const hint = document.createElement("div");
      hint.className = "goal-hint";
      if (r.errorHintUrl) {
        const a = document.createElement("a");
        a.href = r.errorHintUrl;
        a.target = "_blank";
        a.rel = "noopener";
        a.textContent = r.errorHint;
        hint.appendChild(a);
      } else {
        hint.textContent = r.errorHint;
      }
      body.appendChild(hint);
    }
    const meta = document.createElement("div");
    meta.className = "goal-usage";
    meta.textContent = [r.generatorLabel, formatDuration(entry.ms), r.jobId && `job ${r.jobId}`].filter(Boolean).join(" · ");
    body.appendChild(meta);
  }
  return body;
}

function reviewRequestBody(entry) {
  const body = document.createElement("div");
  body.appendChild(labeled("message sent to the manager", textBlock(entry.text)));
  for (const sent of entry.images || []) {
    const row = document.createElement("div");
    row.className = "goal-sent-image";
    const figure = imageFigure(sent.url, sent.thumbUrl, `${sent.originalWidth}x${sent.originalHeight}`, "image sent to the manager",
      `${sent.jobId}|${sent.generatorKey}|${sent.imageIndex}`);
    figure.classList.add("small");
    const caption = document.createElement("div");
    caption.className = "goal-sent-caption";
    if (sent.downscaled) {
      // Legacy record from before full-resolution transport (2026-09-04).
      caption.textContent = `attached as ${sent.width}x${sent.height} ${sent.mime}, ${formatBytes(sent.bytes)} — downscaled from ${sent.originalWidth}x${sent.originalHeight} (pre-2026-09-04 transport)`;
    } else {
      caption.textContent = `original ${sent.width}x${sent.height} ${sent.mime}, ${formatBytes(sent.bytes)} — sent ${sent.transport || "verbatim"}`;
    }
    row.append(figure, caption);
    body.appendChild(row);
  }
  return body;
}

function buildEntryElement(entry) {
  const article = document.createElement("article");
  article.className = `goal-entry kind-${entry.kind}`;
  article.dataset.index = String(entry.index);
  if (entry.error && entry.kind !== "render-result") article.classList.add("has-error");
  const entryVariant = variantOf(entry);
  if (entryVariant) {
    article.classList.add(`variant-${entryVariant}`);
    if (entry.kind === "render-result" && chosenVariantOfTurn(entry.turn, entry.index) === entryVariant) {
      article.classList.add("chosen-lineage");
    }
  }

  const head = document.createElement("div");
  head.className = "goal-entry-head";
  head.appendChild(party(entry.from));
  const arrow = document.createElement("span");
  arrow.className = "goal-arrow";
  arrow.textContent = "→";
  head.appendChild(arrow);
  head.appendChild(party(entry.to));
  const kind = document.createElement("span");
  kind.className = "goal-kind";
  kind.textContent = entryKindLabel(entry);
  head.appendChild(kind);
  if (entry.edited) {
    const edited = document.createElement("span");
    edited.className = "goal-edited";
    edited.textContent = "edited by operator";
    edited.title = entry.originalText ? `original text:\n${entry.originalText}` : "";
    head.appendChild(edited);
  }
  const when = document.createElement("span");
  when.className = "goal-when";
  when.textContent = formatTime(entry.at);
  head.appendChild(when);
  if (entry.ms) {
    const ms = document.createElement("span");
    ms.className = "goal-ms";
    ms.textContent = formatDuration(entry.ms);
    head.appendChild(ms);
  }
  const index = document.createElement("span");
  index.className = "goal-entry-index";
  index.textContent = `#${entry.index}`;
  head.appendChild(index);
  if (entry.kind !== "note") {
    const actions = document.createElement("span");
    actions.className = "goal-entry-actions";
    if (["goal", "design", "render-request", "review-request", "review"].includes(entry.kind)) {
      const edit = document.createElement("button");
      edit.type = "button";
      edit.textContent = "edit & fork from here";
      edit.title = "Copy this loop up to this entry, replace this entry's text, and continue as a new loop";
      edit.addEventListener("click", () => openEditor(article, entry));
      actions.appendChild(edit);
    }
    const fork = document.createElement("button");
    fork.type = "button";
    fork.textContent = "resume from here";
    fork.title = "Copy this loop up to this entry (unchanged) and continue as a new loop; later entries are left behind";
    fork.addEventListener("click", () => submitFork(entry.index, null, null, fork));
    actions.appendChild(fork);
    head.appendChild(actions);
  }
  article.appendChild(head);

  const body = document.createElement("div");
  body.className = "goal-entry-body";
  switch (entry.kind) {
    case "goal":
      body.appendChild(labeled("goal text", textBlock(entry.text, "goal-goal-text")));
      break;
    case "design":
    case "review":
      body.appendChild(managerBody(entry));
      break;
    case "render-request":
      body.appendChild(renderRequestBody(entry));
      break;
    case "render-result":
      body.appendChild(renderResultBody(entry));
      break;
    case "review-request":
      body.appendChild(reviewRequestBody(entry));
      break;
    default: {
      const note = textBlock(entry.text, "goal-note-text");
      if (entry.error) note.classList.add("goal-error");
      body.appendChild(note);
    }
  }
  article.appendChild(body);

  if (entry.wireRequest || entry.wireResponse) {
    const wire = document.createElement("div");
    if (entry.wireRequest) {
      wire.appendChild(wireBlock("exact request sent (image base64 replaced by placeholders)", entry.wireRequest));
    }
    if (entry.wireResponse) {
      wire.appendChild(wireBlock(entry.kind === "render-result" ? "exact gen-result event recorded by the job" : "raw provider response", entry.wireResponse));
    }
    const details = detailsBlock("everything sent and received", wire, false);
    details.classList.add("goal-wire");
    article.appendChild(details);
  }
  return article;
}

function openEditor(article, entry) {
  const existing = article.querySelector(".goal-editor");
  if (existing) {
    existing.remove();
    return;
  }
  const editor = document.createElement("div");
  editor.className = "goal-editor";
  const label = document.createElement("div");
  label.className = "goal-sublabel";
  label.textContent = entry.kind === "design" || entry.kind === "review"
    ? "edit the manager's reply (must stay a valid JSON object per the contract); the fork continues as if the manager had said this"
    : entry.kind === "render-request"
      ? "edit the prompt; the fork renders this text instead, and the manager is told the operator edited it"
      : entry.kind === "goal"
        ? "edit the goal; the fork asks the manager for a new first design"
        : "edit the message to the manager; the fork sends this text with the same image";
  const textarea = document.createElement("textarea");
  textarea.value = entry.text;
  textarea.rows = Math.min(30, Math.max(6, entry.text.split("\n").length + 2));
  textarea.maxLength = config.goalLoop.maxEditedTextChars;
  const row = document.createElement("div");
  row.className = "goal-form-row";
  const turnsInput = document.createElement("input");
  turnsInput.type = "number";
  turnsInput.min = "1";
  turnsInput.max = String(config.goalLoop.maxTurnsCap);
  turnsInput.value = String(loopState.loop.maxTurns);
  const turnsLabel = document.createElement("label");
  turnsLabel.className = "goal-inline-field";
  turnsLabel.append("max turns ", turnsInput);
  const submit = document.createElement("button");
  submit.type = "button";
  submit.textContent = "fork with this text";
  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.textContent = "cancel";
  cancel.className = "goal-secondary";
  cancel.addEventListener("click", () => editor.remove());
  const error = document.createElement("span");
  error.className = "goal-error";
  submit.addEventListener("click", () => submitFork(entry.index, textarea.value, turnsInput.value, submit, error));
  row.append(submit, cancel, turnsLabel, error);
  editor.append(label, textarea, row);
  article.appendChild(editor);
  textarea.focus();
}

async function submitFork(entryIndex, text, maxTurns, button, errorBox) {
  if (button) button.disabled = true;
  const form = new FormData();
  form.append("entryIndex", String(entryIndex));
  if (text != null) form.append("text", text);
  if (maxTurns) form.append("maxTurns", maxTurns);
  form.append("user", currentIdentityName());
  try {
    const body = await fetchJson(`api/goal-loops/${encodeURIComponent(selectedLoopId)}/fork`, { method: "POST", body: form });
    await pollList();
    selectLoop(body.id, true);
  } catch (ex) {
    if (errorBox) errorBox.textContent = ex.message;
    else if (el("goal-control-error")) el("goal-control-error").textContent = ex.message;
    if (button) button.disabled = false;
  }
}

// ---------- boot ----------

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) {
    pollList();
    if (selectedLoopId) {
      clearTimeout(loopPollTimer);
      pollLoop();
    }
  }
});

(async () => {
  try {
    await loadConfig();
  } catch (ex) {
    el("goal-form-error").textContent = `could not load configuration: ${ex.message}`;
    return;
  }
  await pollList();
  scheduleListPoll();
  const id = new URL(location.href).searchParams.get("loop");
  if (id) selectLoop(id, false);
})();
