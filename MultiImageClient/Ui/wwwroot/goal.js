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
  el("environment-name").textContent = window.MicEnvironment ? window.MicEnvironment.name : isOnline ? "-alpha.fuseki.net" : "-local";
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
  for (const loop of [body?.loop, ...(body?.loops || [])].filter(Boolean)) {
    loop.managerLabel = GoalRecap.shortName(loop.managerLabel);
    for (const critic of loop.critics || []) critic.label = GoalRecap.shortName(critic.label);
  }
  for (const manager of body?.goalLoop?.managers || []) manager.label = GoalRecap.shortName(manager.label);
  for (const entry of body?.entries || []) {
    if (entry.critic?.label) entry.critic.label = GoalRecap.shortName(entry.critic.label);
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

// Selection stays in catalog order, which defines source letters.
const gensRow = el("gens-row");
let generators = [];
let standardGeneratorGroups = [];
let generatorPreferences = null;
let generatorEndpointConfiguration = {};
let authInfo = { enabled: false };
const GeneratorPreferencesLocalKey = "mic_generator_preferences_v1";
const storedPersonalField = (name) => PersonalConfigurationSchema.parseStored(
  localStorage.getItem(PersonalConfigurationSchema.StorageKey))?.[name];
function describeSectionIsOpen() { return false; }
function bulkGeneratorInputs() { return [...gensRow.querySelectorAll("input:not(:disabled)")]; }
function renderGoalGeneratorPicker(checkedKeys = generatorPreferences.defaultSelectedKeys) {
  gensRow.replaceChildren();
  for (const generator of generators) {
    if (!generator.available || generator.kind !== "image" || generator.requiresImage ||
        generatorPreferences.hiddenGeneratorKeys.includes(generator.key) || !generatorInActiveView(generator)) continue;
    const chip = buildGenChip(generator);
    const box = chip.querySelector("input");
    box.checked = checkedKeys.includes(generator.key) && generatorPreferences.showImageSection;
    chip.classList.toggle("checked", box.checked);
    gensRow.appendChild(chip);
  }
  gensRow.hidden = !generatorPreferences.showImageSection;
  renderGeneratorPresetButtons();
  for (const control of document.querySelectorAll(".generator-main-control")) {
    control.classList.toggle("preference-hidden", !generatorPreferences.showImageSection);
  }
  updateGeneratorCount();
}
async function generatorPreferencesSaved(normalized) {
  PersonalConfigurationSchema.saveGeneratorPreferences(localStorage, normalized);
  const selected = selectedGeneratorKeys();
  generatorPreferences = normalized;
  activeGeneratorView = normalized.defaultView;
  renderGoalGeneratorPicker(selected);
}
initializeGeneratorControls();
initializeGeneratorConfig();
initializeGeneratorBulkControls();

function selectedGeneratorBoxes() {
  return Array.from(gensRow.querySelectorAll("input[type=checkbox]:checked"));
}

function selectedGeneratorKeys() {
  return selectedGeneratorBoxes().map((box) => box.value);
}

function updateGeneratorCount() {
  const boxes = selectedGeneratorBoxes();
  const max = config && config.goalLoop && config.goalLoop.maxGenerators;
  const span = el("goal-generators-count");
  // Full catalog names, never the internal keys (full-model-names rule).
  const named = boxes.map((box, i) => `${String.fromCharCode(65 + i)} ${generators.find((g) => g.key === box.value).label}`);
  span.textContent = boxes.length === 0
    ? "— pick at least one"
    : `— ${boxes.length} selected${max ? ` of ${max} max` : ""}: ${named.join(", ")} · ${2 * boxes.length} renders per turn`;
  span.classList.toggle("over", Boolean(max) && boxes.length > max);
}

// Protocol 6: independent critics. One checkbox per manager-catalog model,
// in catalog order; the first checked is Critic 1.
function criticChoices(container, items) {
  container.textContent = "";
  for (const item of items) {
    const label = document.createElement("label");
    label.className = `goal-generator-choice ${item.disabled ? "disabled" : ""}`;
    if (item.title) label.title = item.title;
    const box = document.createElement("input");
    box.type = "checkbox";
    box.value = item.key;
    box.disabled = item.disabled;
    box.addEventListener("change", updateCriticCount);
    const text = document.createElement("span");
    text.textContent = item.label;
    label.append(box, text);
    container.appendChild(label);
  }
  updateCriticCount();
}

function selectedCriticBoxes() {
  return Array.from(el("goal-critics").querySelectorAll("input[type=checkbox]:checked"));
}

function updateCriticCount() {
  const boxes = selectedCriticBoxes();
  const max = config && config.goalLoop && config.goalLoop.maxCritics;
  const span = el("goal-critics-count");
  const named = boxes.map((box, i) => `Critic ${i + 1}: ${box.parentElement.querySelector("span").textContent}`);
  span.textContent = boxes.length === 0
    ? "— none: the manager judges alone"
    : `— ${boxes.length} selected${max ? ` of ${max} max` : ""}: ${named.join(", ")}`;
  span.classList.toggle("over", Boolean(max) && boxes.length > max);
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

  generators = config.generators;
  authInfo = config.auth || { enabled: false };
  generatorEndpointConfiguration = config.generatorEndpointConfiguration;
  standardGeneratorGroups = normalizeStandardGeneratorGroups(config.standardGeneratorGroups);
  generatorPreferences = loadGeneratorPreferences(config);
  activeGeneratorView = generatorPreferences.defaultView;
  renderGoalGeneratorPicker();

  const managers = (config.goalLoop.managers || []).filter((m) => m.available).map((m) => ({
    key: m.key,
    label: m.available ? m.label : `${m.label} — ${m.availabilityProblem || "unavailable"}`,
    disabled: !m.available,
    title: m.detail || "",
  }));
  const firstManager = managers.find((m) => !m.disabled);
  optionsFor(el("goal-manager"), managers, firstManager ? firstManager.key : undefined);
  updateManagerDetail();
  // Critic picker: the same catalog, none checked by default (each critic
  // is one more vision call per turn).
  criticChoices(el("goal-critics"), managers);

  optionsFor(el("goal-shape"), (config.shapes || []).map((s) => ({ key: s.key, label: s.label })), "auto");
  optionsFor(el("goal-detail"), (config.details || []).map((d) => ({ key: d.key, label: d.label })), "standard");
  el("goal-max-turns").value = String(config.goalLoop.defaultMaxTurns);
  el("goal-max-turns").max = String(config.goalLoop.maxTurnsCap);
  el("goal-text").maxLength = config.goalLoop.maxGoalChars;
  const pursuit = config.goalLoop.protocolVersion >= 7;
  const fanout = config.goalLoop.protocolVersion >= 8;
  el("goal-samples-field").hidden = !(config.goalLoop.protocolVersion >= 9);
  if (config.goalLoop.protocolVersion >= 9) el("goal-samples").value = String(config.goalLoop.defaultSamplePolicy);
  el("goal-candidates-field").hidden = !fanout;
  if (fanout) el("goal-max-candidates").value = String(config.goalLoop.defaultMaxCandidates);
  el("goal-kind-help").textContent = pursuit
    ? "Compare results against the full goal. A pause for review does not mean success. Grant more turns when needed."
    : "Open-ended searches require evidence of a limit before stopping. Grant more turns when needed.";
  el("goal-search-help").textContent = fanout
    ? "Test up to three ideas each round. Pursue, branch, hold, or drop earlier ideas. Choose any mix of experiments."
    : pursuit
    ? "Each turn tests two candidates on selected sources. Experiments can explore, simplify, rebuild, pursue, or repeat a prompt."
    : "Each generator renders a refinement and a fresh design each turn.";
  document.querySelectorAll(".goal-pursuit-help").forEach((node) => { node.hidden = !pursuit; });
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
  span.textContent = count === 0 ? "" : `${count} running`;
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
  const generatorKeys = selectedGeneratorKeys();
  if (generatorKeys.length === 0) {
    error.textContent = "pick at least one image generator";
    return;
  }
  const maxGenerators = config.goalLoop.maxGenerators;
  if (maxGenerators && generatorKeys.length > maxGenerators) {
    error.textContent = `pick at most ${maxGenerators} image generators`;
    return;
  }
  const criticKeys = selectedCriticBoxes().map((box) => box.value);
  const maxCritics = config.goalLoop.maxCritics;
  if (maxCritics && criticKeys.length > maxCritics) {
    error.textContent = `pick at most ${maxCritics} critics`;
    return;
  }
  const form = new FormData();
  form.append("goal", el("goal-text").value);
  form.append("user", user);
  for (const key of generatorKeys) form.append("generators", key);
  form.append("manager", el("goal-manager").value);
  for (const key of criticKeys) form.append("critics", key);
  form.append("maxTurns", el("goal-max-turns").value);
  if (config.goalLoop.protocolVersion >= 8) form.append("maxCandidates", el("goal-max-candidates").value);
  if (config.goalLoop.protocolVersion >= 9) form.append("samplesPerPrompt", el("goal-samples").value);
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
    plateau: "search paused",
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
    score.textContent = loop.bestScore != null
      ? loop.protocolVersion >= 7 ? `selected turn ${loop.bestTurn}` : `best ${formatScore(loop.bestScore)}` : "";
    top.append(status, turns, score);
    const goal = document.createElement("div");
    goal.className = "goal-list-goal";
    goal.textContent = loop.goal;
    const meta = document.createElement("div");
    meta.className = "goal-list-meta";
    const criticNote = (loop.critics || []).length > 0 ? ` · ${loop.critics.length} critic${loop.critics.length === 1 ? "" : "s"}` : "";
    meta.textContent = `${generatorsSummary(loop)} · ${loop.managerLabel}${criticNote} · ${loop.createdBy} · ${formatTime(loop.createdAtUnixMs)}`;
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
let treeView = false;
let treeSignature = "";

function setLoopView(tree) {
  treeView = tree && loopState?.loop.protocolVersion >= 8;
  el("goal-turns").hidden = treeView;
  el("goal-tree").hidden = !treeView;
  el("goal-work-mode").setAttribute("aria-pressed", String(!treeView));
  el("goal-tree-mode").setAttribute("aria-pressed", String(treeView));
  if (treeView) refreshTree();
}
el("goal-work-mode").addEventListener("click", () => setLoopView(false));
el("goal-tree-mode").addEventListener("click", () => setLoopView(true));

function refreshTree() {
  el("goal-view-modes").hidden = !(loopState?.loop.protocolVersion >= 8);
  if (loopState?.loop.protocolVersion >= 8) {
    const best = loopState.entries.find(e => e.kind === "render-result" && e.turn === loopState.loop.bestTurn
      && variantOf(e) === loopState.loop.bestVariant && sourceOf(e) === loopState.loop.bestSource);
    const bestBody = best && el("goal-turns").querySelector(`.goal-entry[data-index="${best.index}"] .goal-render-body`);
    for (const mark of el("goal-turns").querySelectorAll(".goal-render-body .goal-chosen-mark"))
      if (mark.parentElement !== bestBody) mark.remove();
    if (bestBody && !bestBody.querySelector(".goal-chosen-mark")) {
      const mark = document.createElement("span"); mark.className = "goal-chosen-mark";
      mark.textContent = "Selected best"; bestBody.prepend(mark);
    }
  }
  if (!treeView) return;
  const data = GoalTree.collect(loopState.loop, loopState.entries);
  const signature = `${el("goal-tree").clientWidth}:${JSON.stringify(data)}`;
  if (signature === treeSignature) return;
  const viewport = el("goal-tree").querySelector(".goal-tree-viewport");
  const scroll = viewport && { left: viewport.scrollLeft, top: viewport.scrollTop };
  GoalTree.render(el("goal-tree"), data, revealEntry, apiUrl);
  const next = el("goal-tree").querySelector(".goal-tree-viewport");
  if (next && scroll) { next.scrollLeft = scroll.left; next.scrollTop = scroll.top; }
  treeSignature = signature;
}

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
  el("goal-new").hidden = true;
  el("goal-new-toggle").setAttribute("aria-expanded", "false");
  el("goal-new-toggle").classList.remove("open");
  el("goal-empty").hidden = true;
  el("goal-loop").hidden = false;
  el("goal-loop-head").textContent = "";
  el("goal-turns").textContent = "";
  el("goal-tree").replaceChildren();
  treeSignature = "";
  el("goal-view-modes").hidden = true;
  setLoopView(false);
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
    for (const turn of new Set(body.entries.map((entry) => entry.turn))) refreshTurnComparison(turn);
    renderLoopHead();
    refreshTree();
    refreshFeedbackControls();
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
  const settingsOpen = !!head.querySelector(".goal-settings[open]");
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

  const progress = document.createElement("div");
  progress.className = "goal-progress";
  progress.textContent = `Turn ${loop.turnsRendered} / ${loop.maxTurns}`;
  if (loop.bestScore != null) {
    progress.append((loop.protocolVersion >= 7 ? ` · Selected turn ${loop.bestTurn}` : ` · Best ${formatScore(loop.bestScore)}/10 · turn ${loop.bestTurn}`)
      + `${loop.bestSource ? ` · ${loop.bestSource}` : ""}${loop.bestVariant ? ` ${loop.bestVariant}` : ""}`);
  }
  head.appendChild(progress);

  const roster = document.createElement("div");
  roster.className = "goal-roster";
  roster.setAttribute("aria-label", "Contributors");
  roster.appendChild(contributorBadge("manager"));
  for (const c of loop.critics || []) roster.appendChild(contributorBadge("critic", c));
  for (const g of loop.generators || [{ label: loop.generatorLabel }]) {
    roster.appendChild(contributorBadge("generator", g));
  }
  head.appendChild(roster);

  const settings = document.createElement("div");
  settings.className = "goal-settings-body";
  const line = (label, value) => {
    const row = document.createElement("div");
    const name = document.createElement("strong");
    name.textContent = label + ": ";
    row.append(name, value);
    settings.appendChild(row);
  };
  line("Output", `${loop.shape} · ${loop.detail} · ${loop.quality} · moderation ${loop.moderation}`);
  line(loop.protocolVersion >= 7 ? "Maximum images per turn" : "Images per turn", String(loop.rendersPerTurn));
  if (loop.protocolVersion >= 8) line("Candidate limit", String(loop.maxCandidates));
  if (loop.protocolVersion >= 9) line("Images per prompt / source", loop.samplesPerPrompt === 0 ? "grok-web: 2; others: 1" : String(loop.samplesPerPrompt));
  line("Goal kind", loop.effectiveGoalKind || loop.goalKind || "not classified");
  line(loop.protocolVersion >= 7 ? "Last compatibility score" : "Last refine score", formatScore(loop.lastScore));
  line("Manager model", loop.managerModel);
  const cost = (value, known) => known ? formatUsd(value) : (value > 0 ? `${formatUsd(value)} + unpriced calls` : "price unknown");
  line("Manager cost", `${cost(loop.managerCostUsd, loop.managerCostKnown)} · ${loop.managerInputTokens.toLocaleString()} input / ${loop.managerOutputTokens.toLocaleString()} output tokens`);
  if ((loop.critics || []).length) {
    line("Critic cost", `${cost(loop.criticCostUsd, loop.criticCostKnown)} · ${(loop.criticInputTokens || 0).toLocaleString()} input / ${(loop.criticOutputTokens || 0).toLocaleString()} output tokens`);
  }
  line("Image cost estimate", formatUsd(loop.renderCostUsd));
  line("Created", `${loop.createdBy} · ${formatTime(loop.createdAtUnixMs)}`);
  const settingsDetails = detailsBlock("Settings & usage", settings, settingsOpen);
  settingsDetails.classList.add("goal-settings");
  head.appendChild(settingsDetails);

  if (loop.statusDetail && loop.status !== "done") {
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
  } else if (loop.status !== "done" || hasUnseenFeedback()) {
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
    resume.textContent = hasUnseenFeedback() ? "resume with your feedback" : loop.status === "exhausted" ? "resume with more turns" : "resume";
    resume.disabled = !canControl;
    resume.title = canControl
      ? "Continue from the last recorded entry (re-asks the manager or re-renders the interrupted step)"
      : "Only the loop's creator can resume it";
    resume.addEventListener("click", () => controlLoop("resume", { maxTurns: turnsInput.value }, resume));
    controls.append(resume, turnsLabel);
  } else {
    const doneNote = document.createElement("span");
    doneNote.className = "goal-done-note";
    doneNote.textContent = "Open a contribution to fork from that point.";
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
  const recap = document.createElement("a");
  recap.className = "goal-sheet-link";
  recap.href = `recap.html?loop=${encodeURIComponent(loop.id)}`;
  recap.textContent = "Visual recap · browse / PNG / HTML";
  sheetRow.appendChild(recap);
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
  summary.textContent = `System prompt · v${loop.protocolVersion}`;
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

const PartyLabels = { user: "you", manager: "manager", generator: "image generator", system: "loop", critic: "critic" };
const VariantLabels = { refine: "Refine", fresh: "Fresh" };

function variantLabel(variant) {
  if (/^c[1-3]-s[1-4]$/.test(variant)) return GoalRecap.variantLabel(variant);
  if (/^c[1-3]$/.test(variant)) return `Candidate ${variant.slice(1)}`;
  if (loopState?.loop.protocolVersion >= 7)
    return ({ refine: "Candidate 1", fresh: "Candidate 2" })[variant] || "Image";
  return VariantLabels[variant] || "Image";
}

function variantOrder(variant) {
  return GoalRecap.variantOrder(variant);
}

function feedbackControls(scope, target) {
  if (!target || !config?.goalLoop.feedbackEnabled || !loopState.canControl) return null;
  const row = document.createElement("div"); row.className = "goal-feedback-controls";
  row.dataset.scope = scope; row.dataset.target = String(target.index);
  const label = document.createElement("span"); label.textContent = `Your ${scope} points`;
  const total = document.createElement("strong"); total.className = "goal-feedback-total";
  total.textContent = String(GoalFeedback.total(loopState.entries, scope, target));
  const status = document.createElement("span"); status.className = "goal-feedback-status"; status.setAttribute("aria-live", "polite");
  const retry = document.createElement("button"); retry.type = "button"; retry.textContent = "Retry vote"; retry.hidden = true;
  const buttons = [];
  let pending = null;
  const submit = async () => {
    if (!pending) return;
    const operation = pending, version = selectedVersion, loopId = selectedLoopId;
    buttons.forEach(b => b.disabled = true); retry.hidden = true; status.textContent = "Saving…";
    try {
      const form = new FormData();
      for (const [key, value] of Object.entries(operation)) form.append(key, String(value));
      await fetchJson(`api/goal-loops/${encodeURIComponent(loopId)}/feedback`, { method: "POST", body: form });
      pending = null;
      if (version !== selectedVersion) return;
      status.textContent = loopState.running ? "Saved for planning" : "Saved for resume";
      await pollLoop();
    } catch (error) {
      if (version !== selectedVersion) return;
      status.textContent = `Vote not confirmed: ${error.message}`; retry.hidden = false;
    } finally {
      buttons.forEach(b => b.disabled = !!pending);
    }
  };
  for (const delta of [1, -1]) {
    const button = document.createElement("button"); button.type = "button"; button.textContent = delta === 1 ? "+1" : "−1";
    button.setAttribute("aria-label", `${delta === 1 ? "Add" : "Subtract"} one ${scope} point for entry ${target.index}`);
    button.addEventListener("click", () => {
      pending = { requestId: crypto.randomUUID(), scope, entryIndex: target.index, delta }; submit();
    });
    buttons.push(button);
  }
  retry.addEventListener("click", submit);
  row.append(label, total, ...buttons, retry, status);
  return row;
}

function refreshFeedbackControls() {
  for (const row of el("goal-turns").querySelectorAll(".goal-feedback-controls")) {
    const target = loopState.entries.find(e => e.index === Number(row.dataset.target));
    row.querySelector(".goal-feedback-total").textContent = String(GoalFeedback.total(loopState.entries, row.dataset.scope, target));
  }
}

function hasUnseenFeedback() {
  const feedback = loopState?.entries.findLast(e => e.feedback);
  const manager = loopState?.entries.findLast(e => ["design", "review"].includes(e.kind) && !e.error && e.manager?.parsed);
  return !!feedback && feedback.index > (manager?.manager.feedbackThroughEntry ?? -1);
}

function variantOf(entry) {
  return (entry.render && entry.render.variant) || null;
}

// Protocol 5 source letter of a render entry; null on earlier loops.
function sourceOf(entry) {
  return (entry.render && entry.render.source) || null;
}

function generatorsSummary(loop) {
  const gens = loop.generators || [];
  if (gens.length <= 1 || !gens[0].source) return loop.generatorLabel;
  return gens.map((g) => `${g.source} ${g.label}`).join(" · ");
}

// "refine · source B (grok-web pro)" — the render identity as the page
// names it; the source part is absent on single-source loops.
function renderName(variant, source, generatorLabel) {
  const parts = [variant ? variantLabel(variant) : null];
  if (source) parts.push(`source ${source}${generatorLabel ? ` (${generatorLabel})` : ""}`);
  return parts.filter(Boolean).join(" · ");
}

function generatorLabelOfSource(source) {
  if (!source || !loopState) return "";
  const g = (loopState.loop.generators || []).find((x) => x.source === source);
  return g ? g.label : "";
}

// The manager's lineage choice on a reply, normalized across protocols:
// protocol 4 stores a variant string, protocol 5 a variant + source.
function continueFromOf(parsed) {
  if (!parsed || !parsed.continueFrom) return null;
  return { variant: parsed.continueFrom, source: parsed.continueFromSource || null };
}

function sameRender(a, variant, source) {
  return Boolean(a) && a.variant === variant && (a.source || null) === (source || null);
}

// Every evaluation on a reply as [{variant, source, evaluation}], across
// the protocol-4 pair fields and the protocol-5 array.
function evaluationsOf(parsed) {
  if (!parsed) return [];
  if (Array.isArray(parsed.renderEvaluations)) {
    return parsed.renderEvaluations.map((r) => ({ variant: r.variant, source: r.source || null, evaluation: r.evaluation }));
  }
  const list = [];
  if (parsed.evaluation) list.push({ variant: parsed.freshEvaluation ? "refine" : null, source: null, evaluation: parsed.evaluation });
  if (parsed.freshEvaluation) list.push({ variant: "fresh", source: null, evaluation: parsed.freshEvaluation });
  return list;
}

function hasEvaluations(parsed) {
  return evaluationsOf(parsed).length > 0;
}

// The label states what the manager decided, when that is known; a reply
// that failed the contract is labeled as such rather than as a design.
// Labels identify the action; the adjacent badge identifies its author.
function entryKindLabel(entry) {
  if (entry.manager?.providerStop || entry.critic?.providerStop) return "Provider refused";
  if (entry.error) return "Failed";
  const parsed = entry.manager?.parsed;
  if (entry.kind === "review" && parsed) {
    if (parsed.decision === "done") return isObjectedReview(entry) ? "Stop disputed" : parsed.completion?.outcome === "plateau" ? "Search paused" : "Done";
    if (parsed.candidateDecisions) return `Plan ${parsed.plan.candidates.length} candidate${parsed.plan.candidates.length === 1 ? "" : "s"}`;
    const from = continueFromOf(parsed);
    return from ? `Continue from ${[from.source, from.variant].filter(Boolean).join(" ")}` : "Next design";
  }
  if (entry.kind === "render-result") return variantLabel(variantOf(entry));
  if (entry.kind === "render-request") return `${variantLabel(variantOf(entry))} request`;
  return { design: "Design", critique: "Feedback", "critique-request": "Critique request",
    "review-request": "Review request", goal: "Goal", note: "Event", objection: "Stop disputed" }[entry.kind] || entry.kind;
}

// Role + recorded model name distinguish separate instances of the same model.
function contributorBadge(role, data = {}) {
  const loop = loopState.loop;
  const badge = document.createElement("span");
  badge.className = `goal-contributor contributor-${role}`;
  const tag = document.createElement("span");
  tag.className = "goal-contributor-role";
  let name;
  if (role === "manager") {
    tag.textContent = "Manager";
    name = loop.managerLabel;
    badge.title = loop.managerModel;
  } else if (role === "critic") {
    tag.textContent = `Critic ${data.index + 1}`;
    name = data.label;
    badge.dataset.critic = String(data.index);
    badge.title = data.model;
  } else if (role === "generator") {
    tag.textContent = data.source || "Image";
    name = data.generatorLabel || data.label;
  } else {
    tag.textContent = role === "user" ? "Goal" : "System";
    name = role === "user" ? loop.createdBy : "Loop";
  }
  const label = document.createElement("strong");
  label.textContent = name;
  badge.append(tag, " ", label);
  return badge;
}

function entryContributor(entry) {
  if (entry.critic) return contributorBadge("critic", entry.critic);
  if (entry.render) return contributorBadge("generator", entry.render);
  if (entry.manager || entry.to === "manager") return contributorBadge("manager");
  return contributorBadge(entry.from);
}

// Protocol 6: a critic's message (goal + this turn's images) and its reply.
function critiqueRequestBody(entry) {
  const body = document.createElement("div");
  body.appendChild(labeled("message sent to the critic (a fresh instance; it sees only this)", textBlock(entry.text)));
  for (const sent of entry.images || []) {
    const row = document.createElement("div");
    row.className = "goal-sent-image";
    const figure = imageFigure(sent.url, sent.thumbUrl, `${sent.originalWidth}x${sent.originalHeight}`, "image sent to the critic",
      `${sent.jobId}|${sent.generatorKey}|${sent.imageIndex}`);
    figure.classList.add("small");
    const caption = document.createElement("div");
    caption.className = "goal-sent-caption";
    caption.textContent = `${renderName(sent.variant, sent.source, generatorLabelOfSource(sent.source) || "")} — original ${sent.width}x${sent.height} ${sent.mime}, ${formatBytes(sent.bytes)} — sent ${sent.transport || "verbatim"}`;
    row.append(figure, caption);
    body.appendChild(row);
  }
  return body;
}

function critiqueBody(entry) {
  const body = document.createElement("div");
  const data = entry.critic || {};
  if (data.providerStop) {
    body.appendChild(labeled("provider stop", textBlock(data.providerStop, "goal-error")));
  }
  if (entry.error && !data.parsed) {
    body.appendChild(labeled("contract error — the loop re-asks this critic on resume", textBlock(entry.error, "goal-error")));
    body.appendChild(detailsBlock("raw reply", textBlock(entry.text), false));
  }
  const parsed = data.parsed;
  if (parsed) {
    for (const c of parsed.critiques || []) {
      const row = document.createElement("div");
      row.className = "goal-eval-row";
      row.appendChild(scoreBlock(c));
      const text = document.createElement("div");
      text.className = "goal-eval-text";
      const h = document.createElement("div");
      h.className = "goal-eval-heading";
      h.textContent = renderName(c.variant, c.source, generatorLabelOfSource(c.source) || "?") + " render";
      text.appendChild(h);
      text.appendChild(labeled("assessment", textBlock(c.assessment)));
      if (c.components) text.appendChild(componentTable(c.components));
      if (c.experimentAssessment) text.appendChild(labeled("Subtask result", textBlock(c.experimentAssessment)));
      if (c.problems && c.problems.length) text.appendChild(labeled("problems", listBlock(c.problems, "problems")));
      if (c.ideas && c.ideas.length) text.appendChild(labeled("ideas", listBlock(c.ideas, "keep")));
      row.appendChild(text);
      body.appendChild(row);
    }
    body.appendChild(labeled("overall verdict", textBlock(parsed.overall)));
    if (parsed.rubricConcerns) body.appendChild(labeled("goal interpretation audit", textBlock(parsed.rubricConcerns)));
  }
  if (data.providerReasoning) {
    body.appendChild(detailsBlock("Provider reasoning", textBlock(data.providerReasoning), false));
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
function renderedPromptOfTurn(turn, beforeIndex, variant, source) {
  if (!loopState) return null;
  for (let i = beforeIndex - 1; i >= 0; i--) {
    const e = loopState.entries[i];
    if (e.kind === "render-request" && e.turn === turn && (variant === undefined || variantOf(e) === variant)
      && (source === undefined || sourceOf(e) === source)) {
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
      const from = continueFromOf(e.manager.parsed);
      return from ? renderedPromptOfTurn(e.turn, entry.index, from.variant, from.source) : null;
    }
  }
  return null;
}

// A fresh prompt is a from-scratch re-attempt: diffing it against anything
// would only show noise, so it is shown plain with that statement.
function freshPromptBlock(label, text) {
  return labeled(label, textBlock(text, "goal-prompt goal-prompt-fresh"));
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
    heading.textContent = turn === 0 ? "Goal record" : `Turn ${turn}`;
    if (turn > 0) section.appendChild(heading);
    const images = document.createElement("div");
    images.className = "goal-turn-images";
    const scores = document.createElement("div");
    scores.className = "goal-turn-scores";
    const contributions = document.createElement("div");
    contributions.className = "goal-contributions";
    const log = detailsBlock(turn === 0 ? "Original goal & fork controls" : "Requests & events", document.createElement("div"), false);
    log.classList.add("goal-turn-log");
    log.lastElementChild.className = "goal-turn-events";
    section.append(images, scores, contributions, log);
    container.appendChild(section);
  }
  return section;
}

function appendEntryElement(entry) {
  entryContainer(entry).appendChild(buildEntryElement(entry));
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
  if (existing) {
    if (existing.tagName === "DETAILS") fresh.open = existing.open;
    const oldDetails = Array.from(existing.querySelectorAll("details"));
    Array.from(fresh.querySelectorAll("details")).forEach((d, i) => { d.open = oldDetails[i]?.open || false; });
    existing.replaceWith(fresh);
  }
  else entryContainer(entry).appendChild(fresh);
}

function entryContainer(entry) {
  const section = turnSection(entry.turn);
  if (entry.kind === "render-result") return section.querySelector(".goal-turn-images");
  if (["design", "review", "critique", "objection"].includes(entry.kind)) return section.querySelector(".goal-contributions");
  return section.querySelector(".goal-turn-events");
}

function revealEntry(index) {
  setLoopView(false);
  const node = el("goal-turns").querySelector(`.goal-entry[data-index="${index}"]`);
  if (!node) return;
  for (let parent = node; parent && parent !== el("goal-turns"); parent = parent.parentElement) {
    if (parent.tagName === "DETAILS") parent.open = true;
  }
  node.scrollIntoView({ block: "center", behavior: "smooth" });
}

function refreshTurnComparison(turn) {
  const section = turnSection(turn);
  const entries = loopState.entries.filter((e) => e.turn === turn);
  for (const e of entries.filter((e) => e.kind === "critique" && e.error)) {
    const acceptedLater = entries.some((next) => next.index > e.index && next.kind === "critique"
      && next.critic?.index === e.critic?.index && next.critic?.parsed && !next.error);
    if (acceptedLater) {
      const node = section.querySelector(`.goal-entry[data-index="${e.index}"]`);
      section.querySelector(".goal-turn-events").appendChild(node);
    }
  }
  const events = section.querySelector(".goal-turn-events");
  // Keep the exact entry order inside the request/event disclosure.
  Array.from(events.children).sort((a, b) => Number(a.dataset.index) - Number(b.dataset.index)).forEach((e) => events.appendChild(e));
  const failures = events.querySelectorAll(".kind-critique.has-error, .kind-design.has-error, .kind-review.has-error").length;
  section.querySelector(".goal-turn-log > summary").textContent = (turn === 0 ? "Original goal & fork controls" : "Requests & events")
    + ` (${events.childElementCount}${failures ? ` · ${failures} failed replies` : ""})`;
  const results = entries.filter((e) => e.kind === "render-result").sort((a, b) =>
    (sourceOf(a) || "").localeCompare(sourceOf(b) || "")
    || variantOrder(variantOf(a)) - variantOrder(variantOf(b)) || a.index - b.index);
  const imageGrid = section.querySelector(".goal-turn-images");
  for (const result of results) imageGrid.appendChild(imageGrid.querySelector(`[data-index="${result.index}"]`));
  const container = section.querySelector(".goal-turn-scores");
  container.textContent = "";
  const pursuit = loopState.loop.protocolVersion >= 7;
  const design = loopState.entries.findLast((e) => e.manager?.parsed?.plan
    && !e.error && (e.kind === "design" ? e.turn === turn : e.turn === turn - 1));
  if (!results.length) {
    if (pursuit && design) container.appendChild(compactSearchPlan(design.manager.parsed.plan));
    return;
  }
  const table = document.createElement("table");
  table.className = "goal-score-table";
  const caption = document.createElement("caption");
  caption.textContent = pursuit ? "Required outcomes met · select a result to read evidence" : "Scores / 10 · select a score to read the feedback";
  table.appendChild(caption);
  const header = table.createTHead().insertRow();
  const corner = document.createElement("th");
  corner.scope = "col";
  corner.textContent = "Contributor";
  header.appendChild(corner);
  for (const result of results) {
    const th = document.createElement("th");
    th.scope = "col";
    th.textContent = [sourceOf(result), variantLabel(variantOf(result))].filter(Boolean).join(" · ");
    th.title = result.render.generatorLabel;
    header.appendChild(th);
  }
  const rows = table.createTBody();
  const contributors = [{ role: "manager" }, ...(loopState.loop.critics || []).map((data) => ({ role: "critic", data }))];
  for (const c of contributors) {
    const row = rows.insertRow();
    const label = document.createElement("th");
    label.scope = "row";
    label.appendChild(contributorBadge(c.role, c.data));
    row.appendChild(label);
    // Use the last reply for this exact contributor. Failed attempts have no score.
    const reply = entries.findLast((e) => c.role === "manager"
      ? e.kind === "review"
      : e.kind === "critique" && e.critic?.index === c.data.index);
    const scores = reply && !reply.error ? (c.role === "manager"
      ? evaluationsOf(reply.manager?.parsed)
      : (reply.critic?.parsed?.critiques || []).map((x) => ({ ...x, evaluation: x }))) : [];
    for (const result of results) {
      const cell = row.insertCell();
      const hit = scores.find((x) => sameRender(x, variantOf(result), sourceOf(result)));
      if (hit) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "goal-score-link";
        button.textContent = pursuit ? componentSummary(hit.evaluation) : formatScore(hit.evaluation.score);
        button.setAttribute("aria-label", `${label.textContent}: ${cell.cellIndex > 0 ? header.cells[cell.cellIndex].textContent : ""}, ${pursuit ? componentSummary(hit.evaluation) : `${formatScore(hit.evaluation.score)} out of 10`}`);
        button.title = `${hit.evaluation.goalMet ? "Goal met" : "Goal not met"}: ${hit.evaluation.assessment}`;
        button.addEventListener("click", () => revealEntry(reply.index));
        cell.appendChild(button);
        if (pursuit && hit.evaluation.components) {
          const profile = document.createElement(loopState.loop.protocolVersion >= 8 ? "details" : "div");
          profile.className = "goal-component-profile";
          if (profile.tagName === "DETAILS") {
            const summary = document.createElement("summary"); summary.textContent = "Components"; profile.appendChild(summary);
          }
          for (const component of hit.evaluation.components) {
            const criterion = pursuitRubric().find((r) => r.id === component.criterion);
            const line = document.createElement("div");
            line.textContent = `${component.criterion.replace(/[_-]/g, " ")}: ${component.status}`;
            line.title = `${criterion?.description || component.criterion}: ${component.evidence} (confidence: ${component.confidence})`;
            profile.appendChild(line);
          }
          cell.appendChild(profile);
        }
      } else {
        cell.textContent = reply?.error ? "Failed" : "—";
        cell.title = reply?.error || "No accepted score yet";
      }
    }
  }
  container.appendChild(table);
  if (pursuit && design) container.appendChild(compactSearchPlan(design.manager.parsed.plan));
}

function compactSearchPlan(plan) {
  return loopState.loop.protocolVersion >= 8
    ? detailsBlock(`Plan: ${plan.objective}`, searchPlanBlock(plan), false) : searchPlanBlock(plan);
}

function candidateContext(entry) {
  const candidate = GoalTree.candidate(loopState.entries, entry.turn, variantOf(entry));
  if (!candidate) return null;
  const box = document.createElement("div"); box.className = "goal-node-context";
  const title = document.createElement("div"); title.className = "goal-node-title";
  title.textContent = `${candidate.title} · ${candidate.mode} · ${candidate.scope}`;
  box.appendChild(title);
  if (!candidate.parents.length) box.appendChild(textBlock("New idea"));
  for (const parent of candidate.parents) {
    const result = loopState.entries.find(e => e.kind === "render-result" && e.turn === parent.turn
      && variantOf(e) === parent.variant && sourceOf(e) === parent.source && e.render.ok);
    const link = document.createElement("button"); link.type = "button"; link.className = "goal-score-link";
    link.textContent = `From turn ${parent.turn} · ${variantLabel(parent.variant)} · source ${parent.source}`;
    link.title = parent.contribution;
    if (result) link.addEventListener("click", () => revealEntry(result.index)); else link.disabled = true;
    box.appendChild(link);
  }
  const actualPrompt = GoalTree.renderPrompt(loopState.entries, entry);
  box.appendChild(detailsBlock("Prompt being tried", textBlock(actualPrompt || "Prompt record unavailable.", "goal-prompt"), false));
  return box;
}

function pursuitRubric() {
  return loopState?.entries.find((e) => e.kind === "design" && !e.error)?.manager?.parsed?.rubric || [];
}

function componentSummary(evaluation) {
  const required = pursuitRubric().filter((r) => r.importance === "required");
  const met = required.filter((r) => evaluation.components?.some((c) => c.criterion === r.id && c.status === "met"));
  return `${met.length}/${required.length} required`;
}

function componentTable(components) {
  const table = document.createElement("table");
  table.className = "goal-components";
  const head = table.createTHead().insertRow();
  for (const title of ["Criterion", "Importance", "Status", "Visible evidence", "Confidence"]) {
    const th = document.createElement("th"); th.scope = "col"; th.textContent = title; head.appendChild(th);
  }
  const body = table.createTBody();
  for (const c of components) {
    const r = pursuitRubric().find((r) => r.id === c.criterion);
    const row = body.insertRow();
    for (const value of [r?.description || c.criterion, r?.importance || "", c.status, c.evidence, c.confidence])
      row.insertCell().textContent = value;
  }
  return table;
}

function searchPlanBlock(plan) {
  const box = document.createElement("div");
  box.className = "goal-search-plan";
  box.appendChild(labeled("Current objective", textBlock(plan.objective)));
  if (plan.allocationReason) box.appendChild(labeled("Why this number and mix", textBlock(plan.allocationReason)));
  box.appendChild(labeled("Options and reasons", listBlock((plan.options || []).map((o) => `${o.description}: ${o.reason}`))));
  for (const c of plan.candidates || []) {
    const card = document.createElement("div");
    card.className = "goal-experiment";
    card.appendChild(labeled(`${variantLabel(c.variant)}${c.title ? ` · ${c.title}` : ""} · ${c.mode}`, textBlock(c.question)));
    if (c.parents) {
      for (const parent of c.parents) {
        const result = loopState.entries.find(e => e.kind === "render-result" && e.turn === parent.turn
          && variantOf(e) === parent.variant && sourceOf(e) === parent.source);
        const link = document.createElement("button");
        link.type = "button"; link.className = "goal-score-link";
        link.textContent = `From turn ${parent.turn} · ${variantLabel(parent.variant)} · source ${parent.source}`;
        if (result) link.addEventListener("click", () => revealEntry(result.index));
        else link.disabled = true;
        card.append(link, textBlock(parent.contribution));
      }
      if (!c.parents.length) card.appendChild(textBlock("New idea"));
    }
    card.appendChild(labeled(c.scope === "component" ? "Component study" : "Full scene",
      textBlock([c.componentGoal, `Sources: ${c.sources?.join(", ") || "all selected"}`,
        `Quality: ${c.quality || loopState.loop.quality}; detail: ${c.detail || loopState.loop.detail}`].filter(Boolean).join(" · "))));
    card.appendChild(labeled("Expected evidence", textBlock(c.expected)));
    card.appendChild(labeled("Change", listBlock(c.changes || [])));
    card.appendChild(labeled("Hold", listBlock(c.holds || [])));
    if (c.prompt) card.appendChild(detailsBlock("Complete candidate prompt", textBlock(c.prompt, "goal-prompt"), false));
    if (c.deferredCriteria?.length) {
      card.appendChild(labeled("Temporarily omitted", listBlock(c.deferredCriteria.map((id) =>
        pursuitRubric().find((r) => r.id === id)?.description || id))));
      card.appendChild(labeled("Restore next", textBlock(c.restoreNext)));
    }
    box.appendChild(card);
  }
  return box;
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
  const results = loopState.entries
    .filter((entry) => entry.kind === "render-result" && entry.render && entry.render.ok && entry.render.imageUrl)
    .sort((a, b) => a.turn - b.turn
      || variantOrder(variantOf(a)) - variantOrder(variantOf(b))
      || (sourceOf(a) || "").localeCompare(sourceOf(b) || "")
      || a.index - b.index);
  for (const entry of results) {
    const r = entry.render;
    const variant = variantOf(entry);
    const source = sourceOf(entry);
    const request = renderedPromptOfTurn(entry.turn, entry.index, variant, source);
    const reviewed = reviewOfRender(entry);
    const review = reviewed ? reviewed.parsed : null;
    const evaluation = reviewed ? reviewed.evaluation : null;
    const match = /^(\d+)x(\d+)$/.exec(r.size || "");
    const meta = [
      { label: source ? `generator (source ${source})` : "generator", value: r.generatorLabel },
      { label: "pixels", value: r.size },
      { label: "render time", value: formatDuration(entry.ms) },
      { label: "cost (estimate)", value: r.cost != null ? formatUsd(r.cost) : "" },
      { label: "job", value: r.jobId },
    ];
    if (variant) {
      meta.unshift({ label: "render", value: loopState.loop.protocolVersion >= 7 ? variantLabel(variant)
        : variant === "fresh" ? "fresh — from-scratch re-attempt" : "refine — continues the chosen lineage" });
    }
    if (evaluation) {
      meta.push({ label: "goal met", value: evaluation.goalMet ? "yes" : "no" });
      if (evaluation.problems && evaluation.problems.length) {
        meta.push({ label: "problems", value: evaluation.problems.join("; ") });
      }
      const from = continueFromOf(review);
      meta.push({
        label: "decision",
        value: review.decision === "done"
          ? "done"
          : (from ? `render again — continue from the ${renderName(from.variant, from.source, generatorLabelOfSource(from.source))} render` : "render again"),
      });
    }
    items.push({
      id: viewerItemId(r),
      url: r.imageUrl,
      thumbUrl: r.thumbUrl || (/^https?:\/\//i.test(r.imageUrl) ? "" : r.imageUrl + (r.imageUrl.includes("?") ? "&thumb=1" : "?thumb=1")),
      width: match ? Number(match[1]) : 0,
      height: match ? Number(match[2]) : 0,
      title: `Turn ${entry.turn} of ${loopState.loop.maxTurns}${variant ? ` — ${renderName(variant, source, r.generatorLabel)} render` : ""} — loop ${loopState.loop.id}`,
      subtitle: evaluation
        ? `${evaluation.components ? componentSummary(evaluation) : `${formatScore(evaluation.score)}/10`} — ${evaluation.assessment}`
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
  number.textContent = evaluation.components ? componentSummary(evaluation) : formatScore(evaluation.score);
  const denominator = document.createElement("span");
  denominator.className = "goal-score-denominator";
  denominator.textContent = evaluation.components ? ` · summary ${formatScore(evaluation.score)}/10` : "/10";
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
    const pair = Boolean(parsed.freshEvaluation || parsed.freshPrompt || parsed.renderEvaluations);
    const scored = evaluationsOf(parsed);
    const from = continueFromOf(parsed);
    const multi = scored.some((s) => s.source);
    const evaluationBlock = (evaluation, heading, chosen) => {
      const evalRow = document.createElement("div");
      evalRow.className = `goal-eval-row ${chosen ? "chosen" : ""}`;
      evalRow.appendChild(scoreBlock(evaluation));
      const evalText = document.createElement("div");
      evalText.className = "goal-eval-text";
      if (heading) {
        const h = document.createElement("div");
        h.className = "goal-eval-heading";
        h.textContent = heading + (chosen ? parsed.rubric ? " — selected reference" : " — continue from this one" : "");
        evalText.appendChild(h);
      }
      evalText.appendChild(labeled("assessment", textBlock(evaluation.assessment)));
      if (evaluation.components) evalText.appendChild(componentTable(evaluation.components));
      if (evaluation.experimentAssessment) evalText.appendChild(labeled("Subtask result", textBlock(evaluation.experimentAssessment)));
      if (evaluation.problems && evaluation.problems.length) {
        evalText.appendChild(labeled("problems", listBlock(evaluation.problems, "problems")));
      }
      if (evaluation.keep && evaluation.keep.length) {
        evalText.appendChild(labeled("keep", listBlock(evaluation.keep, "keep")));
      }
      evalRow.appendChild(evalText);
      return evalRow;
    };
    for (const s of scored) {
      let heading = null;
      if (s.variant) {
        heading = loopState.loop.protocolVersion >= 7 ? variantLabel(s.variant)
          : s.variant === "fresh" ? "fresh render (from scratch)" : "refine render";
        if (s.source) heading += ` · source ${s.source} (${generatorLabelOfSource(s.source) || "?"})`;
      }
      body.appendChild(evaluationBlock(s.evaluation, heading, pair && sameRender(from, s.variant, s.source)));
    }
    const decision = document.createElement("div");
    decision.className = `goal-decision decision-${parsed.decision}`;
    decision.textContent = parsed.decision === "done"
      ? parsed.completion?.outcome === "plateau" ? "decision: pause for review" : "decision: achieved"
      : parsed.candidateDecisions ? `decision: test ${parsed.plan.candidates.length} candidate${parsed.plan.candidates.length === 1 ? "" : "s"}`
      : (hasEvaluations(parsed)
        ? (pair ? `decision: render again — continue from the ${from ? renderName(from.variant, from.source, generatorLabelOfSource(from.source)) : "?"} render` : "decision: render again with a new design")
        : (pair ? (multi || (loopState && (loopState.loop.generators || []).length > 1) ? "decision: render these two first designs on every source" : "decision: render these two first designs") : "decision: render this first design"));
    if (parsed.bestTurn) {
      const best = document.createElement("span");
      best.className = "goal-best-turn";
      best.textContent = ` best so far: turn ${parsed.bestTurn}${parsed.bestVariant ? ` ${parsed.bestVariant}` : ""}${parsed.bestSource ? ` · source ${parsed.bestSource}` : ""}`;
      decision.appendChild(best);
    }
    body.appendChild(decision);
    if (parsed.rubric) {
      body.appendChild(labeled("Stable goal criteria", listBlock(parsed.rubric.map((r) =>
        `${r.description} — ${r.importance}. Goal: “${r.basis}”`))));
    }
    if (parsed.findings) {
      for (const [key, title] of [["observation", "Observed"], ["inference", "Tentative explanation"],
        ["uncertainty", "Uncertainty"], ["criticDisagreements", "Critic disagreements"], ["nextTest", "Next useful test"]]) {
        body.appendChild(labeled(title, textBlock(parsed.findings[key])));
      }
    }
    if (parsed.candidateDecisions?.length) body.appendChild(labeled("Decisions for the reviewed ideas",
      listBlock(parsed.candidateDecisions.map(d => `${variantLabel(d.variant)} · ${d.action}: ${d.reason}`))));
    if (parsed.plan) body.appendChild(searchPlanBlock(parsed.plan));
    if (parsed.completion) {
      body.appendChild(labeled(parsed.completion.outcome === "plateau" ? "Pause for review, not success" : "Completion evidence",
        textBlock(`${parsed.completion.rationale}\nEvidence turns: ${parsed.completion.evidenceTurns.join(", ")}`)));
      body.appendChild(labeled("Remaining gaps", listBlock(parsed.completion.remainingGaps)));
    }
    if (parsed.doneStatement) {
      body.appendChild(labeled("done statement", textBlock(parsed.doneStatement)));
    }
    if (parsed.prompt) {
      if (parsed.rubric) {
        body.appendChild(labeled("Candidate 1 prompt", textBlock(parsed.prompt, "goal-prompt")));
      } else if (!hasEvaluations(parsed)) {
        body.appendChild(labeled(pair ? "refine prompt to render (primary design)" : "prompt to render", textBlock(parsed.prompt, "goal-prompt")));
      } else if (pair) {
        const base = from ? renderedPromptOfTurn(entry.turn, entry.index, from.variant, from.source) : null;
        body.appendChild(promptWithDiff(`next refine prompt (builds on the turn ${entry.turn} ${from ? renderName(from.variant, from.source, "") : ""} render)`, parsed.prompt, base));
      } else {
        body.appendChild(promptWithDiff("next prompt to render", parsed.prompt, renderedPromptOfTurn(entry.turn, entry.index)));
      }
    }
    if (parsed.designNotes) {
      body.appendChild(labeled(parsed.rubric ? "Candidate 1 notes" : pair ? "refine design notes" : "design notes", textBlock(parsed.designNotes)));
    }
    if (parsed.freshPrompt) {
      body.appendChild(parsed.rubric ? labeled("Candidate 2 prompt", textBlock(parsed.freshPrompt, "goal-prompt"))
        : freshPromptBlock("next fresh prompt", parsed.freshPrompt));
    }
    if (parsed.freshDesignNotes) {
      body.appendChild(labeled(parsed.rubric ? "Candidate 2 notes" : "fresh design notes", textBlock(parsed.freshDesignNotes)));
    }
    body.appendChild(detailsBlock("Rationale", textBlock(parsed.reasoning), true));
  }
  if (data.providerReasoning) {
    body.appendChild(detailsBlock("Provider reasoning", textBlock(data.providerReasoning), false));
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
  const points = feedbackControls("prompt", entry);
  if (points) body.appendChild(points);
  const variant = variantOf(entry);
  if (loopState?.loop.protocolVersion >= 7) {
    body.appendChild(labeled(`${variantLabel(variant)} prompt sent to the image generator`, textBlock(entry.text, "goal-prompt")));
  } else if (variant === "fresh") {
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
    r.source ? `source ${r.source}: ${r.generatorLabel}` : r.generatorLabel,
    r.shape && `shape ${r.shape}`,
    r.detail && `detail ${r.detail}`,
    r.quality && `quality ${r.quality}`,
    r.moderation && `moderation ${r.moderation}`,
    r.jobId ? `job ${r.jobId}` : "not rendered yet",
  ].filter(Boolean).join(" · ");
  body.appendChild(meta);
  return body;
}

// The render {variant, source} the manager's accepted review of this turn
// chose to continue from, or null while the turn is unreviewed / on
// single-render loops.
function chosenRenderOfTurn(turn, afterIndex) {
  if (!loopState) return null;
  for (let i = afterIndex + 1; i < loopState.entries.length; i++) {
    const e = loopState.entries[i];
    if (e.kind === "review" && e.turn === turn && !e.error && e.manager && e.manager.parsed && e.manager.parsed.continueFrom) {
      return continueFromOf(e.manager.parsed);
    }
  }
  return null;
}

// The review of this render (first accepted review of the same turn after it)
// and its evaluation of this render's variant + source.
function reviewOfRender(entry) {
  if (!loopState) return null;
  const variant = variantOf(entry);
  const source = sourceOf(entry);
  for (let i = entry.index + 1; i < loopState.entries.length; i++) {
    const e = loopState.entries[i];
    if (e.kind === "review" && e.turn === entry.turn && !e.error && e.manager && e.manager.parsed) {
      const hit = evaluationsOf(e.manager.parsed).find((s) => (s.variant || null) === variant && (s.source || null) === source)
        || (!variant ? evaluationsOf(e.manager.parsed)[0] : null);
      if (hit) return { parsed: e.manager.parsed, evaluation: hit.evaluation };
    }
  }
  return null;
}

function renderResultBody(entry) {
  const body = document.createElement("div");
  body.className = "goal-render-body";
  const r = entry.render || {};
  const variant = variantOf(entry);
  const source = sourceOf(entry);
  const loop = loopState.loop;
  const isBest = entry.turn === loop.bestTurn && (loop.bestVariant || null) === variant && (loop.bestSource || null) === source;
  const imagePoints = r.ok ? feedbackControls("image", entry) : null;
  const promptPoints = feedbackControls("prompt", GoalFeedback.requestFor(loopState.entries, entry));
  if (imagePoints) body.appendChild(imagePoints);
  if (promptPoints) body.appendChild(promptPoints);
  if (loop.protocolVersion >= 8) {
    const context = candidateContext(entry);
    if (context) body.appendChild(context);
  }
  const continues = sameRender(chosenRenderOfTurn(entry.turn, entry.index), variant, source);
  if (isBest || continues) {
    const mark = document.createElement("span");
    mark.className = "goal-chosen-mark";
    mark.textContent = [isBest ? loop.protocolVersion >= 7 ? "Selected best" : `Best · ${formatScore(loop.bestScore)}/10` : "",
      continues ? loop.protocolVersion >= 7 ? "Selected reference" : "Continues here" : ""].filter(Boolean).join(" · ");
    body.appendChild(mark);
  }
  if (r.ok) {
    const row = document.createElement("div");
    row.className = "goal-render-row";
    row.appendChild(imageFigure(r.imageUrl, r.thumbUrl, r.size, `turn ${entry.turn}${variant ? ` ${variant}` : ""}${source ? ` source ${source}` : ""} render`, viewerItemId(r)));
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

    fact("pixels", r.size);
    fact("render time", formatDuration(entry.ms));
    fact("cost (estimate)", r.cost != null ? formatUsd(r.cost) : "");
    fact("provider label", r.label);
    fact("media type", r.mediaType);
    fact("job", r.jobId);
    const caption = document.createElement("div");
    caption.className = "goal-image-caption";
    caption.textContent = [r.size, r.cost != null ? formatUsd(r.cost) : ""].filter(Boolean).join(" · ");
    row.appendChild(caption);
    facts.classList.add("goal-image-details");
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
    meta.textContent = [source ? `source ${source}: ${r.generatorLabel}` : r.generatorLabel, formatDuration(entry.ms), r.jobId && `job ${r.jobId}`].filter(Boolean).join(" · ");
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
  const isResult = entry.kind === "render-result";
  const article = document.createElement(isResult ? "article" : "details");
  if (!isResult) article.open = entry.kind === "objection";
  article.className = `goal-entry kind-${entry.kind}`;
  article.dataset.index = String(entry.index);
  if (entry.error && entry.kind !== "render-result") article.classList.add("has-error");
  const entryVariant = variantOf(entry);
  if (entryVariant) {
    article.classList.add(`variant-${entryVariant}`);
    if (entry.kind === "render-result" && sameRender(chosenRenderOfTurn(entry.turn, entry.index), entryVariant, sourceOf(entry))) {
      article.classList.add("chosen-lineage");
    }
    if (sourceOf(entry)) article.dataset.source = sourceOf(entry);
  }

  const head = document.createElement(isResult ? "div" : "summary");
  head.className = "goal-entry-head";
  head.appendChild(entryContributor(entry));
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
  const excerpt = entry.manager?.parsed?.reasoning || entry.critic?.parsed?.overall;
  if (excerpt && !isResult) {
    const preview = document.createElement("span");
    preview.className = "goal-contribution-excerpt";
    preview.textContent = excerpt;
    head.appendChild(preview);
  }
  article.appendChild(head);
  const meta = document.createElement("div");
  meta.className = "goal-entry-meta";
  meta.textContent = `${PartyLabels[entry.from] || entry.from} → ${PartyLabels[entry.to] || entry.to} · ${formatTime(entry.at)}${entry.ms ? ` · ${formatDuration(entry.ms)}` : ""} · #${entry.index}`;
  const actionDetails = detailsBlock("Details & actions", meta, false);
  if (!["note", "feedback"].includes(entry.kind)) {
    const actions = document.createElement("span");
    actions.className = "goal-entry-actions";
    if (["goal", "design", "render-request", "review-request", "review"].includes(entry.kind)) {
      const edit = document.createElement("button");
      edit.type = "button";
      edit.textContent = "edit & fork";
      edit.title = "Copy this loop up to this entry, replace this entry's text, and continue as a new loop";
      edit.addEventListener("click", () => openEditor(article, entry));
      actions.appendChild(edit);
    }
    const fork = document.createElement("button");
    fork.type = "button";
    fork.textContent = "fork here";
    fork.title = "Copy this loop up to this entry (unchanged) and continue as a new loop; later entries are left behind";
    fork.addEventListener("click", () => submitFork(entry.index, null, null, fork));
    actions.appendChild(fork);
    meta.appendChild(actions);
  }

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
    case "critique-request":
      body.appendChild(critiqueRequestBody(entry));
      break;
    case "critique":
      body.appendChild(critiqueBody(entry));
      break;
    default: {
      const note = textBlock(entry.text, "goal-note-text");
      if (entry.error) note.classList.add("goal-error");
      body.appendChild(note);
    }
  }
  const imageDetails = body.querySelector(".goal-image-details");
  if (imageDetails) actionDetails.appendChild(imageDetails);
  article.appendChild(body);
  article.appendChild(actionDetails);

  if (entry.wireRequest || entry.wireResponse) {
    const wire = document.createElement("div");
    if (entry.wireRequest) {
      wire.appendChild(wireBlock("exact request sent (image base64 replaced by placeholders)", entry.wireRequest));
    }
    if (entry.wireResponse) {
      wire.appendChild(wireBlock(entry.kind === "render-result" ? "exact gen-result event recorded by the job" : "raw provider response", entry.wireResponse));
    }
    const details = detailsBlock("Wire data", wire, false);
    details.classList.add("goal-wire");
    actionDetails.appendChild(details);
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
