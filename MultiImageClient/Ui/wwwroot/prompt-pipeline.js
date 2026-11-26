// Stage-1 text pipeline bar (Direct / Rewrite / Split multi-page),
// intermediate Stage-1 results tray with per-model review & child-model
// selection, and feed split-group round-trip controls.
// Loaded as a classic script after app.js; shares composer globals and helpers.

const Stage1MaxWriters = 4;
const Stage1MaxPersonalPresets = 20;

const promptSplitBtn = el("prompt-split");
const stage1Bar = el("stage1-bar");
const stage1Summary = el("stage1-summary");
const stage1Body = el("stage1-body");
const stage1WriterChips = el("stage1-writer-chips");
const stage1AddWriterBtn = el("stage1-add-writer");
const stage1MakeDefaultBtn = el("stage1-make-default");
const stage1RewriteFields = el("stage1-rewrite-fields");
const stage1RewriteInstruction = el("stage1-rewrite-instruction");
const stage1RewriteClearBtn = el("stage1-rewrite-clear");
const stage1RewritePresetSelect = el("stage1-rewrite-preset");
const stage1RewritePresetSaveBtn = el("stage1-rewrite-preset-save");
const stage1RewritePresetDeleteBtn = el("stage1-rewrite-preset-delete");
const stage1RewritePresetBar = el("stage1-rewrite-preset-bar");
const stage1RewritePresetName = el("stage1-rewrite-preset-name");
const stage1RewritePresetConfirmBtn = el("stage1-rewrite-preset-confirm");
const stage1RewritePresetCancelBtn = el("stage1-rewrite-preset-cancel");
const stage1SplitFields = el("stage1-split-fields");
const stage1SplitInstructionLabel = el("stage1-split-instruction-label");
const stage1SplitInstruction = el("stage1-split-instruction");
const stage1SplitDefaultBtn = el("stage1-split-default");
const stage1SplitPresetSelect = el("stage1-split-preset");
const stage1SplitPresetSaveBtn = el("stage1-split-preset-save");
const stage1SplitPresetDeleteBtn = el("stage1-split-preset-delete");
const stage1SplitPresetBar = el("stage1-split-preset-bar");
const stage1SplitPresetName = el("stage1-split-preset-name");
const stage1SplitPresetConfirmBtn = el("stage1-split-preset-confirm");
const stage1SplitPresetCancelBtn = el("stage1-split-preset-cancel");
const stage1SplitStyle = el("stage1-split-style");
const stage1RunBtn = el("stage1-run");
const stage1AutoRenderInput = el("stage1-auto-render");
const stage1RunNote = el("stage1-run-note");
const stage1Error = el("stage1-error");
const stage1ResultsSection = el("stage1-results");
const stage1ResultsCount = el("stage1-results-count");
const stage1ResultsCancelRunningBtn = el("stage1-results-cancel-running");
const stage1ResultsClearBtn = el("stage1-results-clear");
const stage1ResultsList = el("stage1-results-list");

let stage1Mode = "direct"; // "direct" | "rewrite" | "split"
const stage1WritersByMode = {
  rewrite: [],
  split: [],
};
const stage1WritersCustomized = {
  rewrite: false,
  split: false,
};
let stage1SplitInputsHydrated = false;
let stage1SyncingSplitInputs = false;
let stage1CardSeq = 0;
const stage1Cards = [];
const splitGroupExchangeCache = new Map();

function formatSplitPagesCopyBlock(pages) {
  return pages
    .map((page, idx) => {
      const num = Number.isFinite(page.index) && page.index > 0 ? page.index : idx + 1;
      const title = (page.title || "").trim();
      const header = title ? `Page ${num} (${title}):` : `Page ${num}:`;
      return `${header}\n${page.prompt || ""}`;
    })
    .join("\n\n");
}

function promptSplitErrorNodes(error) {
  const nodes = [error instanceof Error ? error.message : String(error)];
  if (error?.providerHint) {
    nodes.push(` ${error.providerHint} `);
    if (error.providerHintUrl) {
      try {
        const a = document.createElement("a");
        a.href = error.providerHintUrl;
        a.target = "_blank";
        a.rel = "noopener";
        a.textContent = new URL(error.providerHintUrl).hostname.replace(/^www\./, "");
        nodes.push(a);
      } catch {
        // Ignore malformed URLs from untrusted error text.
      }
    }
  }
  return nodes;
}

function stage1SlotForMode(mode) {
  return mode === "split" ? "split" : "advice";
}

function stage1DefaultModelEntry(mode) {
  const slot = stage1SlotForMode(mode);
  if (promptAssistantModels.length) {
    return resolvePromptAssistantModel(slot);
  }
  if (mode === "split" && promptSplitConfig) {
    return {
      key: promptSplitConfig.key || promptSplitConfig.model || "",
      model: promptSplitConfig.model || "",
      label: promptSplitConfig.label || "Gemini 4 Neon",
      available: !!promptSplitConfig.available,
      availabilityProblem: promptSplitConfig.availabilityProblem || null,
    };
  }
  return {
    key: claudeAdvice.key || claudeAdvice.model || "",
    model: claudeAdvice.model || "",
    label: claudeAdvice.label || "Gemini 4 Neon",
    available: !!claudeAdvice.available,
    availabilityProblem: claudeAdvice.availabilityProblem || null,
  };
}

function stage1AllWriterOptions(mode) {
  if (promptAssistantModels.length) {
    return promptAssistantModels;
  }
  const fallback = stage1DefaultModelEntry(mode);
  return fallback.key ? [fallback] : [];
}

function stage1AvailableWriterOptions(mode) {
  return stage1AllWriterOptions(mode).filter((entry) => entry.available);
}

function stage1FindWriter(mode, key) {
  const all = stage1AllWriterOptions(mode);
  return all.find((entry) => entry.key === key || entry.model === key) || null;
}

function ensureStage1WritersForMode(mode) {
  if (mode !== "rewrite" && mode !== "split") return [];
  const all = stage1AllWriterOptions(mode);
  const available = all.filter((entry) => entry.available);
  let list = stage1WritersByMode[mode].filter((key) => {
    const found = stage1FindWriter(mode, key);
    return found && found.available;
  });
  // Preserve order while removing duplicates.
  list = [...new Set(list)].slice(0, Stage1MaxWriters);
  if (!list.length) {
    const def = stage1DefaultModelEntry(mode);
    if (def.key && def.available) {
      list = [def.key];
    } else if (available.length) {
      list = [available[0].key];
    } else if (def.key) {
      list = [def.key];
    }
  }
  stage1WritersByMode[mode] = list;
  return list;
}

function stage1BuiltinPresetsFor(mode) {
  if (mode === "split") {
    if (Array.isArray(promptSplitConfig?.presets) && promptSplitConfig.presets.length) {
      return promptSplitConfig.presets;
    }
    const def = promptSplitConfig?.defaultInstruction || "";
    return def ? [{ id: "default", name: "Faithful split (default)", text: def }] : [];
  }
  if (Array.isArray(claudeAdvice?.rewritePresets) && claudeAdvice.rewritePresets.length) {
    return claudeAdvice.rewritePresets;
  }
  return [{ id: "default", name: "Standard flesh-out (default)", text: "" }];
}

function stage1PersonalPresetsFor(mode) {
  const key = mode === "split" ? "splitPresets" : "rewritePresets";
  if (!Array.isArray(promptSplitSettings[key])) promptSplitSettings[key] = [];
  return promptSplitSettings[key];
}

function stage1PresetDom(mode) {
  if (mode === "split") {
    return {
      textarea: stage1SplitInstruction,
      select: stage1SplitPresetSelect,
      saveBtn: stage1SplitPresetSaveBtn,
      deleteBtn: stage1SplitPresetDeleteBtn,
      bar: stage1SplitPresetBar,
      nameInput: stage1SplitPresetName,
      confirmBtn: stage1SplitPresetConfirmBtn,
      cancelBtn: stage1SplitPresetCancelBtn,
    };
  }
  return {
    textarea: stage1RewriteInstruction,
    select: stage1RewritePresetSelect,
    saveBtn: stage1RewritePresetSaveBtn,
    deleteBtn: stage1RewritePresetDeleteBtn,
    bar: stage1RewritePresetBar,
    nameInput: stage1RewritePresetName,
    confirmBtn: stage1RewritePresetConfirmBtn,
    cancelBtn: stage1RewritePresetCancelBtn,
  };
}

function renderStage1PresetSelector(mode) {
  const dom = stage1PresetDom(mode);
  if (!dom.select || !dom.textarea) return;
  const builtins = stage1BuiltinPresetsFor(mode);
  const personal = stage1PersonalPresetsFor(mode);
  const currentText = dom.textarea.value;

  dom.select.replaceChildren();

  let matchedValue = "__custom__";
  const builtGroup = document.createElement("optgroup");
  builtGroup.label = "Built-in presets";
  for (const preset of builtins) {
    const opt = document.createElement("option");
    opt.value = `builtin:${preset.id}`;
    opt.textContent = preset.name;
    builtGroup.appendChild(opt);
    if (matchedValue === "__custom__" && currentText.trim() === String(preset.text || "").trim()) {
      matchedValue = opt.value;
    }
  }
  dom.select.appendChild(builtGroup);

  if (personal.length) {
    const personalGroup = document.createElement("optgroup");
    personalGroup.label = "My saved presets";
    personal.forEach((preset, idx) => {
      const opt = document.createElement("option");
      opt.value = `personal:${idx}`;
      opt.textContent = preset.name;
      personalGroup.appendChild(opt);
      if (matchedValue === "__custom__" && currentText === preset.text) {
        matchedValue = opt.value;
      }
    });
    dom.select.appendChild(personalGroup);
  }

  const customOpt = document.createElement("option");
  customOpt.value = "__custom__";
  customOpt.textContent = "Custom instruction";
  dom.select.appendChild(customOpt);

  dom.select.value = matchedValue;
  if (dom.deleteBtn) {
    dom.deleteBtn.hidden = !matchedValue.startsWith("personal:");
  }
}

function refreshStage1PresetSelectors() {
  renderStage1PresetSelector("split");
  renderStage1PresetSelector("rewrite");
}

function applyStage1PresetSelection(mode) {
  const dom = stage1PresetDom(mode);
  if (!dom.select || !dom.textarea) return;
  const val = dom.select.value;
  if (val === "__custom__") return;
  trackTelemetry("stage1.preset", { mode, kind: val.startsWith("builtin:") ? "builtin" : "personal" });
  let nextText = null;
  if (val.startsWith("builtin:")) {
    const id = val.slice("builtin:".length);
    const found = stage1BuiltinPresetsFor(mode).find((p) => p.id === id);
    if (found) nextText = found.text || "";
  } else if (val.startsWith("personal:")) {
    const idx = Number(val.slice("personal:".length));
    const found = stage1PersonalPresetsFor(mode)[idx];
    if (found) nextText = found.text;
  }
  if (nextText === null) return;
  MultiImageTextEntry.replace(dom.textarea, nextText);
  if (mode === "split") {
    saveStage1SplitSettings();
  } else {
    stage1RewriteClearBtn.hidden = dom.textarea.value.length === 0;
  }
  renderStage1PresetSelector(mode);
  dom.textarea.focus();
}

function openStage1PresetSaveBar(mode) {
  const dom = stage1PresetDom(mode);
  if (!dom.bar || !dom.nameInput || !dom.textarea) return;
  if (!dom.textarea.value.trim()) {
    stage1Error.textContent = "Write an instruction first before saving a preset.";
    dom.textarea.focus();
    return;
  }
  stage1Error.textContent = "";
  const val = dom.select?.value || "";
  if (val.startsWith("personal:")) {
    const idx = Number(val.slice("personal:".length));
    dom.nameInput.value = stage1PersonalPresetsFor(mode)[idx]?.name || "";
  } else {
    dom.nameInput.value = "";
  }
  dom.bar.hidden = false;
  dom.nameInput.focus();
  dom.nameInput.select();
}

function saveStage1PersonalPreset(mode) {
  const dom = stage1PresetDom(mode);
  if (!dom.bar || !dom.nameInput || !dom.textarea) return;
  const name = dom.nameInput.value.trim().replace(/\s+/g, " ");
  const text = dom.textarea.value;
  if (!name) {
    stage1Error.textContent = "Enter a preset name.";
    dom.nameInput.focus();
    return;
  }
  if (name.length > 60) {
    stage1Error.textContent = "Preset name must be at most 60 characters.";
    dom.nameInput.focus();
    return;
  }
  if (!text.trim()) {
    stage1Error.textContent = "Instruction text must not be blank.";
    dom.textarea.focus();
    return;
  }
  const list = stage1PersonalPresetsFor(mode);
  const lower = name.toLowerCase();
  const existingIdx = list.findIndex((p) => p.name.toLowerCase() === lower);
  const backup = list.map((p) => ({ name: p.name, text: p.text }));
  if (existingIdx >= 0) {
    list[existingIdx] = { name, text };
  } else {
    if (list.length >= Stage1MaxPersonalPresets) {
      stage1Error.textContent = `You can save at most ${Stage1MaxPersonalPresets} presets here. Delete one first.`;
      return;
    }
    list.push({ name, text });
  }
  try {
    persistPersonalConfigurationSnapshot();
    dom.bar.hidden = true;
    dom.nameInput.value = "";
    stage1Error.textContent = "";
    stage1RunNote.textContent = `Saved preset "${name}".`;
    renderStage1PresetSelector(mode);
  } catch (error) {
    promptSplitSettings[mode === "split" ? "splitPresets" : "rewritePresets"] = backup;
    stage1Error.textContent = `Could not save preset: ${error.message || error}`;
  }
}

function deleteSelectedStage1PersonalPreset(mode) {
  const dom = stage1PresetDom(mode);
  if (!dom.select) return;
  const val = dom.select.value;
  if (!val.startsWith("personal:")) return;
  const idx = Number(val.slice("personal:".length));
  const list = stage1PersonalPresetsFor(mode);
  if (!Number.isInteger(idx) || idx < 0 || idx >= list.length) return;
  const removed = list[idx];
  const backup = list.map((p) => ({ name: p.name, text: p.text }));
  list.splice(idx, 1);
  try {
    persistPersonalConfigurationSnapshot();
    stage1Error.textContent = "";
    stage1RunNote.textContent = `Deleted preset "${removed.name}".`;
    renderStage1PresetSelector(mode);
  } catch (error) {
    promptSplitSettings[mode === "split" ? "splitPresets" : "rewritePresets"] = backup;
    stage1Error.textContent = `Could not delete preset: ${error.message || error}`;
  }
}

function syncStage1SplitInputsFromSettings({ force = false } = {}) {
  if (stage1AutoRenderInput) {
    stage1AutoRenderInput.checked = !!promptSplitSettings.stage1AutoRender;
  }
  if (!stage1SplitInstruction || !stage1SplitStyle) return;
  const def = promptSplitConfig?.defaultInstruction || "";
  if (!stage1SplitInputsHydrated || force) {
    stage1SyncingSplitInputs = true;
    try {
      const nextInstruction = promptSplitSettings.instruction || def;
      if (stage1SplitInstruction.value !== nextInstruction) {
        MultiImageTextEntry.replace(stage1SplitInstruction, nextInstruction);
      }
      if (stage1SplitStyle.value !== promptSplitSettings.styleNote) {
        MultiImageTextEntry.replace(stage1SplitStyle, promptSplitSettings.styleNote);
      }
      if (def) stage1SplitInputsHydrated = true;
    } finally {
      stage1SyncingSplitInputs = false;
    }
  }
  refreshStage1PresetSelectors();
}

function saveStage1AutoRenderSetting() {
  if (!stage1AutoRenderInput) return;
  promptSplitSettings.stage1AutoRender = !!stage1AutoRenderInput.checked;
  trackTelemetry("stage1.auto_render", { enabled: promptSplitSettings.stage1AutoRender });
  try {
    persistPersonalConfigurationSnapshot();
    stage1Error.textContent = "";
  } catch (error) {
    stage1Error.textContent = `Could not save auto-render setting: ${error.message || error}`;
  }
  renderStage1ActionRow();
}

function saveStage1SplitSettings() {
  if (stage1SyncingSplitInputs) return;
  const instruction = stage1SplitInstruction.value;
  const def = promptSplitConfig?.defaultInstruction || "";
  promptSplitSettings.instruction =
    def && instruction.trim() === def ? "" : instruction;
  promptSplitSettings.styleNote = stage1SplitStyle.value;
  try {
    persistPersonalConfigurationSnapshot();
    stage1Error.textContent = "";
  } catch (error) {
    stage1Error.textContent = `Could not save split text: ${error.message || error}`;
  }
}

function renderStage1Writers() {
  if (stage1Mode !== "rewrite" && stage1Mode !== "split") return;
  const writers = ensureStage1WritersForMode(stage1Mode);
  const available = stage1AvailableWriterOptions(stage1Mode);
  const all = stage1AllWriterOptions(stage1Mode);
  stage1WriterChips.replaceChildren();

  writers.forEach((selectedKey, index) => {
    const chip = document.createElement("span");
    chip.className = "stage1-writer-chip";

    const select = document.createElement("select");
    select.className = "stage1-writer-select";
    select.setAttribute("aria-label", `Text model ${index + 1}`);
    const optionsSource = available.length ? available : all;
    for (const entry of optionsSource) {
      const option = document.createElement("option");
      option.value = entry.key;
      option.textContent = entry.available
        ? entry.label
        : `${entry.label} (unavailable)`;
      option.disabled = !entry.available || (entry.key !== selectedKey && writers.includes(entry.key));
      select.appendChild(option);
    }
    if (selectedKey && !optionsSource.some((entry) => entry.key === selectedKey)) {
      const fallback = stage1FindWriter(stage1Mode, selectedKey);
      const option = document.createElement("option");
      option.value = selectedKey;
      option.textContent = fallback ? fallback.label : selectedKey;
      select.appendChild(option);
    }
    select.value = selectedKey;
    select.addEventListener("change", () => {
      stage1WritersByMode[stage1Mode][index] = select.value;
      stage1WritersCustomized[stage1Mode] = true;
      renderStage1Writers();
      renderStage1ActionRow();
    });
    chip.appendChild(select);

    if (writers.length > 1) {
      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "stage1-writer-remove";
      remove.textContent = "×";
      remove.title = "Remove this text model";
      remove.setAttribute("aria-label", "Remove this text model");
      remove.addEventListener("click", () => {
        stage1WritersByMode[stage1Mode].splice(index, 1);
        stage1WritersCustomized[stage1Mode] = true;
        renderStage1Writers();
        renderStage1ActionRow();
      });
      chip.appendChild(remove);
    }
    stage1WriterChips.appendChild(chip);
  });

  const canAddMore =
    writers.length < Stage1MaxWriters &&
    available.some((entry) => !writers.includes(entry.key));
  stage1AddWriterBtn.disabled = !canAddMore;
  stage1AddWriterBtn.title = canAddMore
    ? "Run another text model in parallel and compare both results"
    : writers.length >= Stage1MaxWriters
      ? `At most ${Stage1MaxWriters} text models can run in parallel`
      : "No other configured text models are available";

  const slot = stage1SlotForMode(stage1Mode);
  const currentDefault = resolvePromptAssistantModel(slot);
  const firstKey = writers[0] || "";
  const firstEntry = stage1FindWriter(stage1Mode, firstKey);
  const differsFromDefault =
    !!firstEntry?.available &&
    !!firstKey &&
    firstKey !== currentDefault.key &&
    promptAssistantModels.length > 0;
  stage1MakeDefaultBtn.hidden = !differsFromDefault;
  if (differsFromDefault) {
    stage1MakeDefaultBtn.textContent = `make ${firstEntry.label} default`;
  }
}

function renderStage1ActionRow() {
  const mode = stage1Mode;
  if (stage1AutoRenderInput) {
    stage1AutoRenderInput.checked = !!promptSplitSettings.stage1AutoRender;
  }
  if (mode === "direct") {
    stage1Summary.textContent = "";
    return;
  }
  const writers = ensureStage1WritersForMode(mode);
  const entries = writers
    .map((key) => stage1FindWriter(mode, key))
    .filter((entry) => entry && entry.available);
  const verb = mode === "split" ? "Split" : "Rewrite";
  if (entries.length === 0) {
    stage1RunBtn.disabled = true;
    stage1RunBtn.textContent = `${verb} unavailable`;
    const problem = mode === "split"
      ? (promptSplitConfig?.availabilityProblem || "No text model is configured.")
      : (claudeAdvice?.availabilityProblem || "No text model is configured.");
    stage1RunNote.textContent = problem;
    stage1Summary.textContent = problem;
    return;
  }
  const autoRender = !!promptSplitSettings.stage1AutoRender;
  const selectedImageGens = checkedGeneratorKeys().filter((key) => !isDescribeGenKey(key));
  stage1RunBtn.disabled = false;
  const baseLabel = entries.length === 1
    ? `${verb} with ${entries[0].label}`
    : `${verb} with ${entries.length} models`;
  if (autoRender) {
    stage1RunBtn.textContent = selectedImageGens.length > 0
      ? `${baseLabel} & render (${selectedImageGens.length})`
      : `${baseLabel} & render`;
    stage1RunNote.textContent = selectedImageGens.length > 0
      ? `Auto-renders directly to ${selectedImageGens.map(genLabel).join(", ")}.`
      : "Select at least one image model below to auto-render.";
  } else {
    stage1RunBtn.textContent = baseLabel;
    stage1RunNote.textContent = "Review results below before rendering.";
  }
  const writerNames = entries.map((entry) => entry.label).join(" · ");
  stage1Summary.textContent = autoRender ? `${writerNames} · auto-render` : writerNames;
  if (stage1SplitInstructionLabel && mode === "split") {
    stage1SplitInstructionLabel.textContent = entries.length === 1
      ? `Tell ${entries[0].label} how to split`
      : "Tell the selected text models how to split";
  }
}

function setStage1Mode(nextMode, { focus = false } = {}) {
  if (!["direct", "rewrite", "split"].includes(nextMode)) return;
  if (stage1Mode !== nextMode) {
    trackTelemetry("stage1.mode", { mode: nextMode });
  }
  stage1Mode = nextMode;
  stage1Error.textContent = "";
  for (const tab of stage1Bar.querySelectorAll("[data-stage1-mode]")) {
    const active = tab.dataset.stage1Mode === stage1Mode;
    tab.classList.toggle("active", active);
    tab.setAttribute("aria-selected", String(active));
  }
  const open = stage1Mode !== "direct";
  stage1Body.hidden = !open;
  stage1RewriteFields.hidden = stage1Mode !== "rewrite";
  stage1SplitFields.hidden = stage1Mode !== "split";
  if (stage1Mode === "split") {
    syncStage1SplitInputsFromSettings();
  } else if (stage1Mode === "rewrite") {
    renderStage1PresetSelector("rewrite");
  }
  if (open) {
    renderStage1Writers();
  }
  renderStage1ActionRow();
  if (focus) {
    stage1Bar.scrollIntoView({ behavior: "smooth", block: "nearest" });
    if (stage1Mode === "split") stage1SplitInstruction.focus();
    else if (stage1Mode === "rewrite") stage1RewriteInstruction.focus();
  }
}

// Called by app.js whenever config or prompt assistant models change.
function applyPromptSplitAvailability() {
  const available = !!promptSplitConfig?.available || stage1AvailableWriterOptions("split").length > 0;
  const label = promptSplitConfig?.label || "Gemini 4 Neon";
  if (promptSplitBtn) {
    promptSplitBtn.disabled = !available;
    promptSplitBtn.title = available
      ? `Open the inline multi-page split step (default: ${label})`
      : `Splitting is unavailable: ${promptSplitConfig?.availabilityProblem || "not configured"}`;
  }
  // Follow the saved default automatically until the user customizes the
  // Stage-1 writer list for that mode.
  for (const mode of ["split", "rewrite"]) {
    const def = stage1DefaultModelEntry(mode);
    if (!stage1WritersCustomized[mode] && def.key) {
      stage1WritersByMode[mode] = [def.key];
    }
  }
  syncStage1SplitInputsFromSettings();
  if (stage1Mode !== "direct") {
    renderStage1Writers();
  }
  renderStage1ActionRow();
  refreshAllStage1CardPickers();
}

// ---------- Stage-1 Results Tray ----------

function updateStage1TrayHeader() {
  const total = stage1Cards.length;
  stage1ResultsSection.hidden = total === 0;
  if (!total) return;
  const running = stage1Cards.filter((card) => card.status === "running").length;
  stage1ResultsCount.textContent = running > 0
    ? `${total} result${total === 1 ? "" : "s"} (${running} running)`
    : `${total} result${total === 1 ? "" : "s"}`;
  if (stage1ResultsCancelRunningBtn) {
    stage1ResultsCancelRunningBtn.hidden = running === 0;
  }
  const hasDismissible = stage1Cards.some((card) => card.status !== "running");
  stage1ResultsClearBtn.disabled = !hasDismissible;
}

function removeStage1Card(cardState) {
  if (cardState.controller) {
    try { cardState.controller.abort(); } catch {}
    cardState.controller = null;
  }
  if (cardState.timer) {
    clearInterval(cardState.timer);
    cardState.timer = 0;
  }
  cardState.el?.remove();
  const idx = stage1Cards.indexOf(cardState);
  if (idx >= 0) stage1Cards.splice(idx, 1);
  updateStage1TrayHeader();
}

function stage1AvailableShapes() {
  if (Array.isArray(shapeOptions) && shapeOptions.length > 0) {
    return shapeOptions;
  }
  const select = el("opt-shape");
  if (!select) return [];
  return [...select.options].map((opt) => ({
    key: opt.value,
    label: opt.dataset.defaultLabel || opt.textContent || opt.value,
    inputLabel: opt.dataset.inputLabel || opt.textContent || opt.value,
  }));
}

function isKnownShapeKey(key) {
  return typeof key === "string" && stage1AvailableShapes().some((s) => s.key === key);
}

function stage1ShapeLabel(key, hasImage) {
  const entry = stage1AvailableShapes().find((s) => s.key === key);
  if (!entry) return key || "auto";
  if (key === "auto") {
    return hasImage ? (entry.inputLabel || "match input image") : "auto (vary per page)";
  }
  return entry.label || key;
}

function isMultiPartSplitCard(cardState) {
  return cardState?.kind === "split" && (cardState.plan?.imageCount ?? 0) >= 2;
}

function resolveInitialSeriesShape(requestedShape, hasImage, suggestedShape) {
  if (requestedShape && requestedShape !== "auto" && isKnownShapeKey(requestedShape)) {
    return requestedShape;
  }
  if (hasImage) {
    return "auto";
  }
  if (suggestedShape && isKnownShapeKey(suggestedShape)) {
    return suggestedShape;
  }
  return "auto";
}

function effectiveShapeForCard(cardState) {
  if (isMultiPartSplitCard(cardState) && isKnownShapeKey(cardState.selectedShape)) {
    return cardState.selectedShape;
  }
  return el("opt-shape")?.value || "auto";
}

function seriesShapeNoteText(cardState) {
  const selected = effectiveShapeForCard(cardState);
  const hasImage = cardState.images.length > 0;
  const requested = cardState.requestedShape || "auto";
  const suggested = isKnownShapeKey(cardState.plan?.suggestedShape)
    ? cardState.plan.suggestedShape
    : null;
  if (cardState.shapeCustomized) {
    if (selected === "auto") {
      return hasImage ? "matches attached image" : "each image model picks per page";
    }
    return "applies to every page";
  }
  if (requested !== "auto" && selected === requested) {
    if (suggested && suggested !== "auto" && suggested !== requested) {
      return `from composer (model suggested ${stage1ShapeLabel(suggested, false)})`;
    }
    return "from composer output AR";
  }
  if (hasImage && selected === "auto") {
    return "matches attached image";
  }
  if (suggested && suggested !== "auto" && selected === suggested) {
    return "applies to all pages";
  }
  if (selected === "auto") {
    return "each image model picks per page";
  }
  return "applies to every page";
}

function visibleAvailableImageGeneratorsForCard(cardState) {
  const hasImage = cardState.images.length > 0;
  const explicitAr = effectiveShapeForCard(cardState) !== "auto";
  const listed = new Set(
    [...allGeneratorInputs()]
      .filter((input) => input.dataset.kind !== "describe")
      .map((input) => input.value));
  // Show every available listed image generator plus any already selected on this card.
  return generators.filter((g) => {
    if (g.kind === "describe") return false;
    if (!g.available) return false;
    if (g.requiresImage && !hasImage) return false;
    return listed.has(g.key) || cardState.selectedGens.has(g.key);
  }).map((g) => {
    const aspectIncompatible =
      hasImage &&
      g.imageCapable &&
      explicitAr &&
      !g.imageAspectOverride;
    return { gen: g, aspectIncompatible };
  });
}

function checkStage1CardPromptLimits(cardState) {
  const chosen = [...cardState.selectedGens];
  if (!chosen.length) return [];
  const hasImage = cardState.images.length > 0;
  const promptsToCheck = [];
  if (cardState.kind === "split" && cardState.plan?.imageCount >= 2) {
    cardState.plan.images.forEach((part, idx) => {
      promptsToCheck.push({ label: `Part ${idx + 1}`, text: part.prompt });
    });
  } else if (cardState.kind === "split" && cardState.plan?.imageCount === 1) {
    promptsToCheck.push({ label: "Prompt", text: cardState.sourcePrompt });
  } else if (cardState.kind === "rewrite") {
    promptsToCheck.push({ label: "Rewrite", text: cardState.rewriteText });
  }
  const issues = [];
  for (const key of chosen) {
    const gen = generators.find((g) => g.key === key);
    if (!gen) continue;
    const extraText = combinedEndpointExtraText(key);
    const imageSent = hasImage && gen.imageCapable;
    for (const item of promptsToCheck) {
      const check = MultiImagePromptLimits.check(gen, item.text, extraText, imageSent);
      if (!check) continue;
      issues.push({
        action: check.action,
        partLabel: item.label,
        genLabel: genLabel(key),
        excess: check.count - check.max,
        unit: check.unit,
        max: check.max,
      });
    }
  }
  return issues;
}

function refreshStage1CardPicker(cardState) {
  if (!cardState.pickerHost) return;
  const items = visibleAvailableImageGeneratorsForCard(cardState);
  for (const { gen, aspectIncompatible } of items) {
    if (aspectIncompatible) cardState.selectedGens.delete(gen.key);
  }
  // Remove keys that are no longer available at all.
  const availableKeys = new Set(items.filter((x) => !x.aspectIncompatible).map((x) => x.gen.key));
  for (const key of [...cardState.selectedGens]) {
    if (!availableKeys.has(key)) cardState.selectedGens.delete(key);
  }

  const headCount = cardState.pickerHost.querySelector(".stage1-child-count");
  if (headCount) {
    headCount.textContent = `Image models (${cardState.selectedGens.size} selected)`;
  }

  const grid = cardState.pickerHost.querySelector(".stage1-gen-list");
  if (grid) {
    grid.replaceChildren();
    for (const { gen, aspectIncompatible } of items) {
      const pill = document.createElement("label");
      pill.className = "stage1-gen-pill";
      const checked = !aspectIncompatible && cardState.selectedGens.has(gen.key);
      pill.classList.toggle("checked", checked);
      pill.classList.toggle("unavailable", aspectIncompatible);
      if (aspectIncompatible) {
        pill.title = `${genLabel(gen.key)} cannot override output AR with an input image; choose match input image to enable it`;
      }
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = gen.key;
      input.checked = checked;
      input.disabled = aspectIncompatible;
      input.addEventListener("change", () => {
        if (input.checked) cardState.selectedGens.add(gen.key);
        else cardState.selectedGens.delete(gen.key);
        pill.classList.toggle("checked", input.checked);
        refreshStage1CardRenderState(cardState);
      });
      const text = document.createElement("span");
      text.textContent = genLabel(gen.key);
      pill.append(input, text);
      grid.appendChild(pill);
    }
  }

  refreshStage1CardRenderState(cardState);
}

function refreshStage1CardRenderState(cardState) {
  if (!cardState.pickerHost) return;
  const headCount = cardState.pickerHost.querySelector(".stage1-child-count");
  if (headCount) {
    headCount.textContent = `Image models (${cardState.selectedGens.size} selected)`;
  }
  const limitsEl = cardState.el.querySelector(".stage1-card-limits");
  if (limitsEl) {
    const issues = checkStage1CardPromptLimits(cardState);
    limitsEl.replaceChildren();
    limitsEl.hidden = issues.length === 0;
    for (const issue of issues) {
      const line = document.createElement("p");
      line.className = issue.action === "fail" ? "stage1-limit-fail" : "stage1-limit-warn";
      line.textContent = issue.action === "fail"
        ? `${issue.partLabel} exceeds ${issue.genLabel}'s ${issue.max.toLocaleString()}-${issue.unit} limit by ${issue.excess.toLocaleString()}.`
        : `${issue.partLabel} exceeds ${issue.genLabel}'s ${issue.max.toLocaleString()}-${issue.unit} limit by ${issue.excess.toLocaleString()} and will truncate at send.`;
      limitsEl.appendChild(line);
    }
  }
  const renderBtn = cardState.el.querySelector(".stage1-card-render");
  if (renderBtn) {
    const count = cardState.selectedGens.size;
    const busy = cardState.rendering;
    renderBtn.disabled = busy || count === 0;
    if (busy) {
      renderBtn.textContent = cardState.kind === "split" && cardState.plan?.imageCount >= 2
        ? `Starting ${cardState.plan.imageCount} jobs…`
        : "Starting job…";
    } else if (cardState.kind === "split" && cardState.plan?.imageCount >= 2) {
      renderBtn.textContent = count > 0
        ? `Render ${cardState.plan.imageCount} parts × ${count} model${count === 1 ? "" : "s"}`
        : "Pick image models to render";
    } else if (cardState.kind === "split" && cardState.plan?.imageCount === 1) {
      renderBtn.textContent = count > 0
        ? `Render unsplit × ${count} model${count === 1 ? "" : "s"}`
        : "Pick image models to render";
    } else {
      renderBtn.textContent = count > 0
        ? `Render × ${count} model${count === 1 ? "" : "s"}`
        : "Pick image models to render";
    }
  }
}

function refreshAllStage1CardPickers() {
  for (const cardState of stage1Cards) {
    if (cardState.status === "ready") {
      refreshStage1CardPicker(cardState);
    }
  }
}

function buildStage1SeriesArRow(cardState) {
  const row = document.createElement("div");
  row.className = "stage1-series-ar";

  const label = document.createElement("label");
  label.className = "stage1-series-ar-label";

  const heading = document.createElement("strong");
  heading.textContent = "Series AR";

  const select = document.createElement("select");
  select.className = "stage1-series-ar-select";
  select.setAttribute("aria-label", `Series aspect ratio for ${cardState.modelLabel}`);

  const hasImage = cardState.images.length > 0;
  const suggested = isKnownShapeKey(cardState.plan?.suggestedShape)
    ? cardState.plan.suggestedShape
    : null;
  for (const entry of stage1AvailableShapes()) {
    const opt = document.createElement("option");
    opt.value = entry.key;
    const baseLabel = stage1ShapeLabel(entry.key, hasImage);
    opt.textContent = !hasImage && suggested && suggested !== "auto" && entry.key === suggested
      ? `${baseLabel} (suggested)`
      : baseLabel;
    select.appendChild(opt);
  }
  select.value = effectiveShapeForCard(cardState);

  const note = document.createElement("span");
  note.className = "stage1-series-ar-note";
  note.textContent = seriesShapeNoteText(cardState);

  select.addEventListener("change", () => {
    cardState.selectedShape = select.value;
    cardState.shapeCustomized = true;
    note.textContent = seriesShapeNoteText(cardState);
    refreshStage1CardPicker(cardState);
  });

  label.append(heading, select);
  row.append(label, note);
  return row;
}

function buildStage1ChildPicker(cardState) {
  const picker = document.createElement("div");
  picker.className = "stage1-child-picker";

  if (isMultiPartSplitCard(cardState)) {
    picker.appendChild(buildStage1SeriesArRow(cardState));
  }

  const head = document.createElement("div");
  head.className = "stage1-child-head";

  const countLabel = document.createElement("strong");
  countLabel.className = "stage1-child-count";
  head.appendChild(countLabel);

  const quick = document.createElement("div");
  quick.className = "stage1-child-quick";

  const syncBtn = document.createElement("button");
  syncBtn.type = "button";
  syncBtn.textContent = "sync from composer";
  syncBtn.addEventListener("click", () => {
    cardState.selectedGens = new Set(
      checkedGeneratorKeys().filter((key) => !isDescribeGenKey(key)));
    refreshStage1CardPicker(cardState);
  });

  const sotaBtn = document.createElement("button");
  sotaBtn.type = "button";
  sotaBtn.textContent = "SOTA";
  sotaBtn.addEventListener("click", () => {
    const sotaKeys = generators
      .filter((g) => g.kind !== "describe" && g.available && g.sota)
      .map((g) => g.key);
    cardState.selectedGens = new Set(sotaKeys);
    refreshStage1CardPicker(cardState);
  });

  const noneBtn = document.createElement("button");
  noneBtn.type = "button";
  noneBtn.textContent = "none";
  noneBtn.addEventListener("click", () => {
    cardState.selectedGens.clear();
    refreshStage1CardPicker(cardState);
  });

  quick.append(syncBtn, sotaBtn, noneBtn);
  head.appendChild(quick);

  const list = document.createElement("div");
  list.className = "stage1-gen-list";

  const limits = document.createElement("div");
  limits.className = "stage1-card-limits";
  limits.hidden = true;

  picker.append(head, list, limits);
  cardState.pickerHost = picker;
  return picker;
}

function currentOutputOptions(includeN) {
  const names = includeN
    ? ["shape", "detail", "quality", "moderation", "n"]
    : ["shape", "detail", "quality", "moderation"];
  return Object.fromEntries(names.map((name) => [name, el(`opt-${name}`).value]));
}

async function renderStage1Card(cardState, { auto = false } = {}) {
  if (cardState.rendering || cardState.status !== "ready") return;
  const gens = [...cardState.selectedGens];
  if (!gens.length) return;
  const user = currentUsername();
  if (!user) {
    setStage1CardActionStatus(cardState, "Choose a username first, at the top of the page.", "error");
    return;
  }
  cardState.rendering = true;
  setStage1CardActionStatus(
    cardState,
    auto ? `Auto-rendering to ${gens.map(genLabel).join(", ")}…` : "",
    auto ? "done" : "");
  refreshStage1CardRenderState(cardState);

  const optsFor = (includeN) => {
    if (auto && cardState.capturedOptions) {
      const names = includeN
        ? ["shape", "detail", "quality", "moderation", "n"]
        : ["shape", "detail", "quality", "moderation"];
      return Object.fromEntries(names.map((name) => [name, cardState.capturedOptions[name]]));
    }
    return currentOutputOptions(includeN);
  };

  try {
    if (cardState.kind === "split" && cardState.plan?.imageCount >= 2) {
      const splitOpts = { ...optsFor(false), shape: effectiveShapeForCard(cardState) };
      const form = new FormData();
      form.append("prompt", cardState.sourcePrompt);
      form.append("user", user);
      form.append("generators", gens.join(","));
      for (const [name, value] of Object.entries(splitOpts)) {
        form.append(name, value);
      }
      form.append("n", "1");
      form.append("generatorExtraTexts", JSON.stringify(generatorExtraTextsFor(gens)));
      form.append("splitExchangeId", cardState.plan.exchangeId);
      if (cardState.sourceJobId) {
        form.append("splitSourceJobId", cardState.sourceJobId);
      }
      appendInputImages(form, cardState.images);
      const response = await fetch(apiUrl("api/jobs"), { method: "POST", body: form });
      if (response.status === 401) { location.reload(); return; }
      const body = await response.json();
      if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
      if (user === currentUsername() && cardState.revision !== null) {
        promptRewrites.submitted(cardState.sourcePrompt, cardState.revision);
      }
      for (const job of body.jobs) {
        ownedJobIds.add(job.id);
        addJobCard(
          job.id,
          cardState.plan.images[job.split.index - 1].prompt,
          gens,
          cardState.images.length > 0,
          Date.now(),
          cardState.images.length,
          {
            user: body.user || user,
            originalUser: body.originalUser || body.user || user,
            ownerId: body.ownerId || "",
            canHide: authInfo.localVisibilityManagement === true || !!authInfo.user,
            split: job.split,
          });
      }
      const shapeTag = splitOpts.shape && splitOpts.shape !== "auto"
        ? ` · ${stage1ShapeLabel(splitOpts.shape, cardState.images.length > 0)}`
        : "";
      trackTelemetry("stage1.card_action", {
        mode: "split",
        action: auto ? "auto_render" : "render",
        genCount: gens.length,
        parts: body.jobs.length,
        shape: splitOpts.shape || "auto",
      });
      cardState.renderHistory.push(
        `${body.jobs.length} parts${shapeTag} × ${gens.map(genLabel).join(", ")}`);
      setStage1CardActionStatus(
        cardState,
        auto
          ? `Auto-started ${body.jobs.length} jobs${shapeTag} (${gens.map(genLabel).join(", ")}).`
          : `Started ${body.jobs.length} jobs${shapeTag} (${gens.map(genLabel).join(", ")}). Pick other image models to render again, or Bail.`,
        "done");
    } else {
      const promptText = cardState.kind === "rewrite"
        ? cardState.rewriteText.trim()
        : cardState.sourcePrompt.trim();
      if (!promptText) {
        throw new Error("Prompt is empty.");
      }
      const form = new FormData();
      form.append("prompt", promptText);
      form.append("user", user);
      form.append("generators", gens.join(","));
      for (const [name, value] of Object.entries(optsFor(true))) {
        form.append(name, value);
      }
      form.append("generatorExtraTexts", JSON.stringify(generatorExtraTextsFor(gens)));
      appendInputImages(form, cardState.images);
      const response = await fetch(apiUrl("api/jobs"), { method: "POST", body: form });
      if (response.status === 401) { location.reload(); return; }
      const body = await response.json();
      if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
      ownedJobIds.add(body.id);
      addJobCard(
        body.id,
        promptText,
        gens,
        cardState.images.length > 0,
        Date.now(),
        cardState.images.length,
        {
          user: body.user || user,
          originalUser: body.originalUser || body.user || user,
          ownerId: body.ownerId || "",
          canHide: authInfo.localVisibilityManagement === true || !!authInfo.user,
        });
      trackTelemetry("stage1.card_action", {
        mode: cardState.kind,
        action: auto ? "auto_render" : "render",
        genCount: gens.length,
        parts: 1,
      });
      cardState.renderHistory.push(`1 job × ${gens.map(genLabel).join(", ")}`);
      const unsplitNote = cardState.kind === "split" ? "Model recommended 1 image; auto-started unsplit job" : "Auto-started job";
      setStage1CardActionStatus(
        cardState,
        auto
          ? `${unsplitNote} (${gens.map(genLabel).join(", ")}).`
          : `Started job for ${gens.map(genLabel).join(", ")}. Pick other image models to render again, or Bail.`,
        "done");
    }
  } catch (error) {
    setStage1CardActionStatus(
      cardState,
      `Could not start jobs: ${error.message || error}`,
      "error");
  } finally {
    cardState.rendering = false;
    refreshStage1CardRenderState(cardState);
  }
}

function setStage1CardActionStatus(cardState, text, mode) {
  const statusEl = cardState.el?.querySelector(".stage1-card-action-status");
  if (!statusEl) return;
  statusEl.textContent = text;
  statusEl.dataset.mode = mode || "";
  statusEl.hidden = !text;
}

function paintStage1Card(cardState) {
  const card = cardState.el;
  card.dataset.status = cardState.status;
  card.dataset.kind = cardState.kind;
  cardState.pickerHost = null;
  card.replaceChildren();

  // Header row (single-line)
  const head = document.createElement("div");
  head.className = "stage1-card-head";

  const title = document.createElement("h4");
  title.className = "stage1-card-title";
  const prefix = cardState.kind === "split" ? "Split" : "Rewrite";
  title.textContent = `${prefix} · ${cardState.modelLabel}`;

  const badge = document.createElement("span");
  badge.className = "stage1-card-badge";
  if (cardState.status === "running") {
    const secs = Math.floor((Date.now() - cardState.startedAt) / 1000);
    badge.textContent = cardState.kind === "split" ? `Planning… ${secs} s` : `Writing… ${secs} s`;
  } else if (cardState.status === "ready") {
    if (cardState.kind === "split") {
      badge.textContent = cardState.plan.imageCount === 1
        ? "1 image (no split)"
        : `${cardState.plan.imageCount} parts`;
    } else {
      badge.textContent = `${cardState.rewriteText.length.toLocaleString()} chars`;
    }
  } else if (cardState.status === "cancelled") {
    badge.textContent = "Cancelled";
  } else {
    badge.textContent = "Failed";
  }

  const headRight = document.createElement("div");
  headRight.className = "stage1-card-head-actions";
  if (cardState.status === "running") {
    const cancelBtn = document.createElement("button");
    cancelBtn.type = "button";
    cancelBtn.className = "stage1-card-cancel";
    cancelBtn.textContent = "cancel";
    cancelBtn.addEventListener("click", () => cardState.controller?.abort());
    headRight.appendChild(cancelBtn);
  } else if (cardState.canRetry) {
    const retryBtn = document.createElement("button");
    retryBtn.type = "button";
    retryBtn.className = "stage1-card-retry";
    retryBtn.textContent = cardState.kind === "split" ? "retry split" : "retry rewrite";
    retryBtn.addEventListener("click", () => {
      trackTelemetry("stage1.card_action", { mode: cardState.kind, action: "retry" });
      runStage1Card(cardState);
    });
    headRight.appendChild(retryBtn);
  }
  const bailBtn = document.createElement("button");
  bailBtn.type = "button";
  bailBtn.className = "stage1-card-bail";
  bailBtn.textContent = "Bail";
  bailBtn.title = "Discard this text step result without generating images";
  bailBtn.addEventListener("click", () => {
    trackTelemetry("stage1.card_action", { mode: cardState.kind, action: "bail", rendered: cardState.renderHistory.length > 0 });
    removeStage1Card(cardState);
  });
  headRight.appendChild(bailBtn);

  head.append(title, badge, headRight);
  card.appendChild(head);

  // Compact source preview line
  const sourceLine = document.createElement("div");
  sourceLine.className = "stage1-card-source";
  const sourceLabel = document.createElement("strong");
  sourceLabel.textContent = "Source:";
  const sourceSnippet = document.createElement("span");
  sourceSnippet.className = "stage1-card-source-text";
  const singleLine = cardState.sourcePrompt.replace(/\s+/g, " ").trim();
  sourceSnippet.textContent = singleLine.length > 160 ? `${singleLine.slice(0, 160)}…` : singleLine;
  sourceSnippet.title = cardState.sourcePrompt;
  sourceLine.append(sourceLabel, sourceSnippet);
  if (cardState.images.length > 0) {
    const imgBadge = document.createElement("span");
    imgBadge.className = "stage1-card-img-badge";
    imgBadge.textContent = cardState.images.length === 1
      ? "1 input image"
      : `${cardState.images.length} input images`;
    sourceLine.appendChild(imgBadge);
  }
  card.appendChild(sourceLine);

  // Body
  const body = document.createElement("div");
  body.className = "stage1-card-body";

  if (cardState.status === "running") {
    const waiting = document.createElement("p");
    waiting.className = "stage1-card-waiting";
    if (cardState.autoRender && cardState.selectedGens.size > 0) {
      const targetNames = [...cardState.selectedGens].map(genLabel).join(", ");
      waiting.textContent = cardState.kind === "split"
        ? `${cardState.modelLabel} is splitting this prompt. Pages will auto-render to ${targetNames} as soon as ready.`
        : `${cardState.modelLabel} is rewriting this prompt. Output will auto-render to ${targetNames} as soon as ready.`;
    } else {
      waiting.textContent = cardState.kind === "split"
        ? `${cardState.modelLabel} is deciding how to split this prompt.`
        : `${cardState.modelLabel} is rewriting this prompt.`;
    }
    body.appendChild(waiting);
    card.appendChild(body);
    return;
  }

  if (cardState.status === "cancelled") {
    const note = document.createElement("p");
    note.className = "stage1-card-info";
    note.textContent = "Cancelled. Nothing was sent to image models.";
    body.appendChild(note);
    card.appendChild(body);
    return;
  }

  if (cardState.status === "error") {
    const err = document.createElement("p");
    err.className = "stage1-card-error";
    err.replaceChildren(...promptSplitErrorNodes(cardState.error));
    body.appendChild(err);
    card.appendChild(body);
    return;
  }

  // Ready state
  if (cardState.kind === "split") {
    const reasonEl = document.createElement("p");
    reasonEl.className = "stage1-card-reason";
    reasonEl.textContent = cardState.plan.reason;
    body.appendChild(reasonEl);

    if (cardState.plan.imageCount >= 2) {
      const partsWrap = document.createElement("div");
      partsWrap.className = "stage1-parts";
      cardState.plan.images.forEach((part, idx) => {
        const partBox = document.createElement("div");
        partBox.className = "stage1-part";
        const partHead = document.createElement("div");
        partHead.className = "stage1-part-head";
        partHead.textContent = `part ${idx + 1} of ${cardState.plan.imageCount} · ${part.title}`;
        const partPrompt = document.createElement("p");
        partPrompt.className = "stage1-part-prompt";
        partPrompt.textContent = part.prompt;
        partBox.append(partHead, partPrompt);
        partsWrap.appendChild(partBox);
      });
      body.appendChild(partsWrap);
    } else {
      const oneNote = document.createElement("p");
      oneNote.className = "stage1-card-info";
      oneNote.textContent = `${cardState.modelLabel} recommends one image instead of splitting. Choose image models below to render the original prompt, retry, or Bail.`;
      body.appendChild(oneNote);
    }
  } else {
    const textarea = document.createElement("textarea");
    textarea.className = "stage1-rewrite-text";
    textarea.rows = 4;
    textarea.value = cardState.rewriteText;
    textarea.setAttribute("aria-label", `Rewritten prompt from ${cardState.modelLabel}`);
    textarea.addEventListener("input", () => {
      cardState.rewriteText = textarea.value;
      badge.textContent = `${cardState.rewriteText.length.toLocaleString()} chars`;
      refreshStage1CardRenderState(cardState);
    });
    body.appendChild(textarea);
  }

  const picker = buildStage1ChildPicker(cardState);
  body.appendChild(picker);

  const actions = document.createElement("div");
  actions.className = "stage1-card-actions";

  const renderBtn = document.createElement("button");
  renderBtn.type = "button";
  renderBtn.className = "stage1-card-render primary";
  renderBtn.addEventListener("click", () => renderStage1Card(cardState));
  actions.appendChild(renderBtn);

  if (cardState.kind === "split" && cardState.plan?.imageCount >= 2) {
    const copyAllBtn = document.createElement("button");
    copyAllBtn.type = "button";
    copyAllBtn.className = "stage1-card-copy-all";
    copyAllBtn.textContent = "copy all pages";
    copyAllBtn.title = "Copy all split page prompts as one combined text block";
    copyAllBtn.addEventListener("click", async () => {
      const text = formatSplitPagesCopyBlock(
        cardState.plan.images.map((part, idx) => ({
          index: idx + 1,
          title: part.title,
          prompt: part.prompt,
        })));
      try {
        if (!navigator.clipboard?.writeText) throw new Error("Clipboard API unavailable");
        await navigator.clipboard.writeText(text);
        setStage1CardActionStatus(
          cardState,
          `Copied all ${cardState.plan.images.length} pages to the clipboard.`,
          "done");
      } catch {
        setStage1CardActionStatus(cardState, "Could not copy pages to the clipboard.", "error");
      }
    });
    actions.appendChild(copyAllBtn);
  }

  if (cardState.kind === "rewrite") {
    const useBtn = document.createElement("button");
    useBtn.type = "button";
    useBtn.className = "stage1-card-use-prompt";
    useBtn.textContent = "Use in prompt";
    useBtn.addEventListener("click", () => {
      trackTelemetry("stage1.card_action", { mode: "rewrite", action: "use_in_composer" });
      applyClaudeEdit(cardState.rewriteText, cardState.exchangeId);
      setStage1CardActionStatus(cardState, "Loaded into the prompt box. Use undo edit under the prompt to revert.", "done");
      promptBox.focus();
    });
    actions.appendChild(useBtn);
  }

  const actionStatus = document.createElement("span");
  actionStatus.className = "stage1-card-action-status";
  actionStatus.setAttribute("role", "status");
  actionStatus.hidden = true;
  actions.appendChild(actionStatus);

  body.appendChild(actions);
  card.appendChild(body);
  refreshStage1CardPicker(cardState);
}

async function runStage1Card(cardState) {
  if (cardState.controller) {
    try { cardState.controller.abort(); } catch {}
  }
  if (cardState.timer) {
    clearInterval(cardState.timer);
    cardState.timer = 0;
  }
  const controller = new AbortController();
  cardState.controller = controller;
  cardState.status = "running";
  cardState.error = null;
  cardState.plan = null;
  cardState.startedAt = Date.now();
  paintStage1Card(cardState);
  updateStage1TrayHeader();

  cardState.timer = setInterval(() => {
    if (cardState.status !== "running") return;
    const badge = cardState.el?.querySelector(".stage1-card-badge");
    if (badge) {
      const secs = Math.floor((Date.now() - cardState.startedAt) / 1000);
      badge.textContent = cardState.kind === "split" ? `Planning… ${secs} s` : `Writing… ${secs} s`;
    }
  }, 1000);

  try {
    if (cardState.kind === "split") {
      const initialGens = [...cardState.selectedGens];
      const form = new URLSearchParams({
        model: cardState.modelKey,
        prompt: cardState.sourcePrompt,
        instruction: cardState.instruction,
        styleNote: cardState.styleNote,
        user: cardState.user,
        generators: initialGens.join(","),
        generatorExtraTexts: JSON.stringify(generatorExtraTextsFor(initialGens)),
        imageAttached: String(cardState.images.length > 0),
        shape: cardState.requestedShape || "auto",
      });
      const response = await fetch(apiUrl("api/prompt/split"), {
        method: "POST",
        body: form,
        signal: controller.signal,
      });
      if (response.status === 401) { location.reload(); return; }
      const body = await response.json();
      if (!response.ok) {
        const failure = new Error(body.error || `HTTP ${response.status}`);
        failure.providerHint = body.errorHint || "";
        failure.providerHintUrl = body.errorHintUrl || "";
        throw failure;
      }
      cardState.plan = body;
      cardState.exchangeId = body.exchangeId;
      cardState.modelLabel = body.modelLabel || cardState.modelLabel;
      if (body.imageCount >= 2) {
        cardState.selectedShape = resolveInitialSeriesShape(
          cardState.requestedShape || "auto",
          cardState.images.length > 0,
          body.suggestedShape);
        cardState.shapeCustomized = false;
      } else {
        cardState.selectedShape = null;
        cardState.shapeCustomized = false;
      }
      cardState.status = "ready";
    } else {
      const form = new URLSearchParams({
        model: cardState.modelKey,
        instruction: cardState.instruction,
        prompt: cardState.sourcePrompt,
        user: cardState.user,
      });
      const response = await fetch(apiUrl("api/prompt/advice"), {
        method: "POST",
        body: form,
        signal: controller.signal,
      });
      if (response.status === 401) { location.reload(); return; }
      const body = await response.json();
      if (!response.ok) {
        const failure = new Error(body.error || `HTTP ${response.status}`);
        failure.providerHint = body.errorHint || "";
        failure.providerHintUrl = body.errorHintUrl || "";
        throw failure;
      }
      cardState.rewriteText = body.replacement || "";
      cardState.exchangeId = body.exchangeId || "";
      cardState.modelLabel = body.modelLabel || cardState.modelLabel;
      cardState.status = "ready";
    }
  } catch (error) {
    if (error?.name === "AbortError") {
      cardState.status = "cancelled";
    } else {
      cardState.status = "error";
      cardState.error = error;
    }
  } finally {
    if (cardState.timer) {
      clearInterval(cardState.timer);
      cardState.timer = 0;
    }
    cardState.controller = null;
    if (cardState.el?.isConnected) {
      paintStage1Card(cardState);
      updateStage1TrayHeader();
    }
    trackTelemetry("stage1.card_done", {
      mode: cardState.kind,
      model: cardState.modelKey || "",
      status: cardState.status,
      durMs: Math.max(0, Date.now() - cardState.startedAt),
      parts: cardState.plan?.imageCount ?? (cardState.status === "ready" ? 1 : 0),
    });
    promptRewrites.refreshHistory();
  }
  if (cardState.status === "ready"
      && cardState.autoRender
      && cardState.renderHistory.length === 0
      && cardState.el?.isConnected) {
    if (cardState.selectedGens.size === 0) {
      setStage1CardActionStatus(
        cardState,
        "Auto-render paused: no compatible image models remain selected. Pick image models below and click Render.",
        "error");
    } else if (checkStage1CardPromptLimits(cardState).some((issue) => issue.action === "fail")) {
      setStage1CardActionStatus(
        cardState,
        "Auto-render paused: a prompt exceeds a selected image model's limit. Adjust image models below and click Render.",
        "error");
    } else {
      void renderStage1Card(cardState, { auto: true });
    }
  }
}

function addStage1Card(cardState) {
  const elCard = document.createElement("article");
  elCard.className = "stage1-card";
  elCard.dataset.cardId = cardState.id;
  cardState.el = elCard;
  stage1Cards.unshift(cardState);
  stage1ResultsList.prepend(elCard);
  paintStage1Card(cardState);
  updateStage1TrayHeader();
}

function startStage1Run() {
  stage1Error.textContent = "";
  const mode = stage1Mode;
  if (mode !== "split" && mode !== "rewrite") return;

  const prompt = promptBox.value.trim();
  if (!prompt) {
    stage1Error.textContent = "Write a prompt first.";
    return;
  }
  const user = currentUsername();
  if (!user) {
    stage1Error.textContent = "Choose a username first, at the top of the page.";
    return;
  }
  if (mode === "split" && attachedSketchSubmission()) {
    stage1Error.textContent = "A composition sketch lays out one image. Remove the sketch to split the prompt.";
    return;
  }
  const instruction = mode === "split"
    ? stage1SplitInstruction.value.trim()
    : stage1RewriteInstruction.value.trim();
  if (mode === "split" && !instruction) {
    stage1Error.textContent = "Write a split instruction first.";
    return;
  }
  const styleNote = mode === "split" ? stage1SplitStyle.value.trim() : "";

  const writers = ensureStage1WritersForMode(mode)
    .map((key) => stage1FindWriter(mode, key))
    .filter((entry) => entry && entry.available);
  if (!writers.length) {
    stage1Error.textContent = "Select at least one available text model.";
    return;
  }

  const initialGens = checkedGeneratorKeys().filter((key) => !isDescribeGenKey(key));
  const autoRender = !!promptSplitSettings.stage1AutoRender;
  if (autoRender && !initialGens.length) {
    stage1Error.textContent = "Select at least one image model below to auto-render, or uncheck auto-render to review first.";
    return;
  }

  const capturedImages = [...inputImageItems];
  const capturedOptions = currentOutputOptions(true);
  const requestedShape = capturedOptions.shape || "auto";

  // Prevent accidental duplicate requests for models already running on this exact input.
  const alreadyRunning = [];
  const writersToStart = [];
  for (const writer of writers) {
    const modelKey = writer.key || writer.model;
    const duplicate = stage1Cards.some((card) =>
      card.status === "running"
      && card.kind === mode
      && card.modelKey === modelKey
      && card.sourcePrompt === prompt
      && card.instruction === instruction
      && card.styleNote === styleNote
      && (mode !== "split" || (card.requestedShape || "auto") === requestedShape));
    if (duplicate) alreadyRunning.push(writer);
    else writersToStart.push(writer);
  }
  if (!writersToStart.length) {
    stage1Error.textContent = alreadyRunning.length === 1
      ? `${alreadyRunning[0].label} is already running for this exact prompt.`
      : "All selected text models are already running for this exact prompt.";
    stage1ResultsSection.scrollIntoView({ behavior: "smooth", block: "nearest" });
    return;
  }
  if (alreadyRunning.length > 0) {
    stage1RunNote.textContent = `Skipped ${alreadyRunning.map((w) => w.label).join(", ")} (already running for this prompt).`;
  }

  const revision = promptRewrites.revision;

  // Add cards in reverse so Writer 1 appears first at the top-left of the tray.
  const created = [];
  for (let i = writersToStart.length - 1; i >= 0; i--) {
    const writer = writersToStart[i];
    const cardState = {
      id: `s1-${++stage1CardSeq}`,
      kind: mode,
      modelKey: writer.key || writer.model,
      modelLabel: writer.label,
      user,
      sourcePrompt: prompt,
      revision,
      instruction,
      styleNote,
      images: capturedImages,
      selectedGens: new Set(initialGens),
      autoRender,
      capturedOptions,
      requestedShape,
      selectedShape: null,
      shapeCustomized: false,
      status: "running",
      plan: null,
      rewriteText: "",
      exchangeId: "",
      sourceJobId: "",
      error: null,
      rendering: false,
      renderHistory: [],
      canRetry: true,
      startedAt: Date.now(),
      controller: null,
      timer: 0,
      el: null,
      pickerHost: null,
    };
    addStage1Card(cardState);
    created.unshift(cardState);
  }

  trackTelemetry("stage1.run", {
    mode,
    writerCount: created.length,
    firstModel: created[0]?.modelKey || "",
    autoRender,
    chars: prompt.length,
    hasImage: capturedImages.length > 0,
    genCount: initialGens.length,
  });
  stage1ResultsSection.scrollIntoView({ behavior: "smooth", block: "nearest" });
  for (const cardState of created) {
    runStage1Card(cardState);
  }
}

// ---------- Feed Split-Group Round-Trip Controls ----------

async function fetchSplitExchangeRecord(exchangeId, primaryJobId) {
  if (!exchangeId) throw new Error("This split group has no recorded exchange id.");
  const cached = splitGroupExchangeCache.get(exchangeId);
  if (cached) return cached;
  const query = primaryJobId ? `?jobId=${encodeURIComponent(primaryJobId)}` : "";
  const response = await fetch(apiUrl(`api/prompt/split/${encodeURIComponent(exchangeId)}${query}`));
  if (response.status === 401) { location.reload(); throw new Error("Reloading session…"); }
  const body = await response.json();
  if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
  splitGroupExchangeCache.set(exchangeId, body);
  return body;
}

async function fetchPrimaryJobInputBlobs(primaryJobId) {
  if (!primaryJobId) return [];
  const card = el(`job-${primaryJobId}`);
  if (!card) return [];
  const inputCount = Math.max(
    0,
    parseInt(card.dataset.inputCount || (card.querySelector(".job-input-thumb") ? "1" : "0"), 10) || 0);
  const blobs = [];
  for (let i = 0; i < inputCount; i++) {
    const resp = await fetch(apiUrl(`api/jobs/${encodeURIComponent(primaryJobId)}/images/input/${i}`));
    if (!resp.ok) throw new Error(`input image ${i} fetch returned HTTP ${resp.status}`);
    const blob = await resp.blob();
    if (!blob.type.startsWith("image/")) {
      throw new Error(`input image ${i} returned ${blob.type || "an unknown content type"}`);
    }
    blobs.push(blob);
  }
  return blobs;
}

function setComposerFromSplitSource(sourcePrompt, blobs) {
  if (blobs.length > 0) setImagesFromBlobs(blobs);
  else clearImage();
  MultiImageTextEntry.replace(promptBox, sourcePrompt);
  if (mcpheeCtl) mcpheeCtl.refresh();
  if (mcpheePanel && !mcpheePanelContainer.hidden) mcpheePanel.refresh();
}

function collectSplitSeriesPageVariants(group, gen) {
  const partsHost = group.querySelector(":scope > .split-group-parts");
  const expectedCount = Number(group.dataset.splitCount || partsHost?.dataset.splitCount || 0);
  if (!partsHost || expectedCount < 2) return null;
  const cards = [...partsHost.querySelectorAll(":scope > .job")]
    .sort((a, b) => Number(a.dataset.splitIndex || 0) - Number(b.dataset.splitIndex || 0));
  if (cards.length !== expectedCount) return null;
  const variants = [];
  for (let i = 0; i < expectedCount; i++) {
    const card = cards[i];
    if (Number(card.dataset.splitIndex) !== i + 1) return null;
    if (card.dataset.state !== "done" || card.dataset.canHide === "false") return null;
    const cell = card.querySelector(`.cell[data-gen="${CSS.escape(gen)}"]`);
    if (!cell || cell.dataset.state !== "done") return null;
    const anchors = [...cell.querySelectorAll('a[data-viewer-image="true"]:not([data-result-kind="text"])')]
      .sort((a, b) => Number(a.dataset.imageIndex || 0) - Number(b.dataset.imageIndex || 0));
    if (!anchors.length) return null;
    const thumbs = anchors.map((a) => a.querySelector("img")?.src || a.href);
    variants.push({
      index: i + 1,
      title: card.dataset.splitTitle || "",
      jobId: card.dataset.jobId || "",
      thumbs,
    });
  }
  return variants;
}

function refreshSplitGroupSheets(group) {
  if (!group) return;
  const groupId = group.dataset.splitGroup || "";
  const partsHost = group.querySelector(":scope > .split-group-parts");
  const expectedCount = Number(group.dataset.splitCount || partsHost?.dataset.splitCount || 0);
  const cards = partsHost ? [...partsHost.querySelectorAll(":scope > .job")] : [];
  const makers = partsHost ? [...partsHost.querySelectorAll(":scope > .split-maker")] : [];

  const allPresent = expectedCount >= 2 && cards.length === expectedCount;
  const allDone = allPresent && cards.every((c) => c.dataset.state === "done");
  const hasHidden = cards.some((c) =>
    isPromptHidden(c.dataset.jobId)
    || [...hiddenImageKeys].some((key) => key.startsWith(`${c.dataset.jobId}|`)));
  const ready = !!groupId && allDone && !hasHidden && makers.length > 0;

  for (const link of group.querySelectorAll(":scope > .split-group-head .split-group-sheet-link")) {
    link.hidden = !ready;
  }

  const showPerMakerSheets = ready && makers.length >= 2;
  for (const maker of makers) {
    const gen = maker.dataset.gen || "";
    const existingSheets = maker.querySelector(":scope > .split-maker-sheets");
    if (!showPerMakerSheets) {
      existingSheets?.remove();
    } else if (!existingSheets && gen) {
      const wrap = document.createElement("span");
      wrap.className = "split-maker-sheets";

      const fullLink = document.createElement("a");
      fullLink.className = "split-maker-sheet-full";
      fullLink.href = apiUrl(`api/split-groups/${encodeURIComponent(groupId)}/contact-sheet?text=full&gen=${encodeURIComponent(gen)}`);
      fullLink.target = "_blank";
      fullLink.rel = "noopener";
      fullLink.textContent = "sheet (full text)";
      fullLink.title = `Open one PNG with all pages from ${genLabel(gen)} plus full prompts`;

      const noTextLink = document.createElement("a");
      noTextLink.className = "split-maker-sheet-notext";
      noTextLink.href = apiUrl(`api/split-groups/${encodeURIComponent(groupId)}/contact-sheet?text=none&gen=${encodeURIComponent(gen)}`);
      noTextLink.target = "_blank";
      noTextLink.rel = "noopener";
      noTextLink.textContent = "sheet (no text)";
      noTextLink.title = `Open one PNG with all pages from ${genLabel(gen)} without full prompts`;

      wrap.append(fullLink, noTextLink);
      maker.appendChild(wrap);
    }

    const existingShare = maker.querySelector(":scope > .split-maker-series-share");
    const canShareSeries = ready && !!gen && vibecodersAvailable
      && !!collectSplitSeriesPageVariants(group, gen);
    if (!canShareSeries) {
      existingShare?.remove();
    } else if (!existingShare) {
      const shareBtn = document.createElement("button");
      shareBtn.type = "button";
      shareBtn.className = "split-maker-series-share";
      shareBtn.dataset.vibecodersSeriesSend = "true";
      shareBtn.dataset.splitGroup = groupId;
      shareBtn.dataset.generator = gen;
      shareBtn.addEventListener("click", () => {
        const variants = collectSplitSeriesPageVariants(group, gen);
        if (!variants) return;
        sendSeriesToVibecoders(groupId, gen, variants);
      });
      paintVibecodersSeriesButton(shareBtn);
      maker.appendChild(shareBtn);
    } else {
      paintVibecodersSeriesButton(existingShare);
    }
  }
}

function enhanceSplitGroupHeader(group, split, card) {
  if (split.exchangeId && !group.dataset.splitExchangeId) {
    group.dataset.splitExchangeId = split.exchangeId;
  }
  if (split.count && !group.dataset.splitCount) {
    group.dataset.splitCount = String(split.count);
  }
  if (!group.dataset.splitPrimaryJobId || split.index === 1) {
    group.dataset.splitPrimaryJobId = card.dataset.jobId;
  }
  if (group.dataset.splitControlsWired === "true") return;
  group.dataset.splitControlsWired = "true";

  const heading = group.querySelector(":scope > h3");
  if (!heading) return;

  const headRow = document.createElement("div");
  headRow.className = "split-group-head";
  heading.before(headRow);
  headRow.appendChild(heading);

  const actions = document.createElement("div");
  actions.className = "split-group-actions";

  const groupId = group.dataset.splitGroup || split.groupId || "";

  const sheetFullLink = document.createElement("a");
  sheetFullLink.className = "split-group-sheet-link split-group-sheet-full";
  sheetFullLink.href = apiUrl(`api/split-groups/${encodeURIComponent(groupId)}/contact-sheet?text=full`);
  sheetFullLink.target = "_blank";
  sheetFullLink.rel = "noopener";
  sheetFullLink.textContent = "contact sheet (full text)";
  sheetFullLink.title = "Open one combined PNG with every page from every model plus all full prompts";
  sheetFullLink.hidden = true;

  const sheetNoTextLink = document.createElement("a");
  sheetNoTextLink.className = "split-group-sheet-link split-group-sheet-notext";
  sheetNoTextLink.href = apiUrl(`api/split-groups/${encodeURIComponent(groupId)}/contact-sheet?text=none`);
  sheetNoTextLink.target = "_blank";
  sheetNoTextLink.rel = "noopener";
  sheetNoTextLink.textContent = "contact sheet (no text)";
  sheetNoTextLink.title = "Open one combined PNG with every page from every model without the full prompt text";
  sheetNoTextLink.hidden = true;

  const copyAllBtn = document.createElement("button");
  copyAllBtn.type = "button";
  copyAllBtn.className = "split-group-copy-all";
  copyAllBtn.textContent = "copy all pages";
  copyAllBtn.title = "Copy the text of all pages in this series as one combined block";

  const copyNote = document.createElement("span");
  copyNote.className = "split-group-copy-note";
  copyNote.setAttribute("role", "status");
  copyNote.hidden = true;

  const sourceBtn = document.createElement("button");
  sourceBtn.type = "button";
  sourceBtn.className = "split-group-source-btn";
  sourceBtn.textContent = "source prompt";
  sourceBtn.setAttribute("aria-expanded", "false");

  const resplitBtn = document.createElement("button");
  resplitBtn.type = "button";
  resplitBtn.className = "split-group-resplit";
  resplitBtn.textContent = "re-split";
  resplitBtn.title = "Load the original prompt, inputs, and split instructions into Stage 1 to split again";

  const rerenderBtn = document.createElement("button");
  rerenderBtn.type = "button";
  rerenderBtn.className = "split-group-rerender";
  rerenderBtn.textContent = "re-render plan";
  rerenderBtn.title = "Open this saved split plan in the Stage-1 review tray to pick new image models without re-calling the text model";

  const setSourceBtn = document.createElement("button");
  setSourceBtn.type = "button";
  setSourceBtn.className = "split-group-set-source";
  setSourceBtn.textContent = "set source active";
  setSourceBtn.title = "Load the original unsplit prompt and input images into the composer";

  actions.append(
    sheetFullLink,
    sheetNoTextLink,
    copyAllBtn,
    copyNote,
    sourceBtn,
    resplitBtn,
    rerenderBtn,
    setSourceBtn);
  headRow.appendChild(actions);

  const actionError = document.createElement("p");
  actionError.className = "split-group-action-error";
  actionError.setAttribute("role", "alert");
  actionError.hidden = true;

  const sourcePanel = document.createElement("div");
  sourcePanel.className = "split-group-source-panel";
  sourcePanel.hidden = true;

  const reason = group.querySelector(":scope > .split-group-reason");
  if (reason) reason.after(actionError, sourcePanel);
  else headRow.after(actionError, sourcePanel);

  const showGroupError = (text) => {
    actionError.textContent = text;
    actionError.hidden = !text;
  };

  let copyNoteTimer = 0;
  copyAllBtn.addEventListener("click", async () => {
    showGroupError("");
    const partCards = [...group.querySelectorAll(".split-group-parts > .job")]
      .sort((a, b) => Number(a.dataset.splitIndex || 0) - Number(b.dataset.splitIndex || 0));
    const pages = partCards.map((partCard, idx) => ({
      index: Number(partCard.dataset.splitIndex || idx + 1),
      title: partCard.dataset.splitTitle || "",
      prompt: partCard.querySelector(".job-prompt")?.textContent || "",
    })).filter((p) => p.prompt.length > 0);
    if (!pages.length) {
      showGroupError("No page prompts are loaded for this series.");
      return;
    }
    const block = formatSplitPagesCopyBlock(pages);
    try {
      if (!navigator.clipboard?.writeText) throw new Error("Clipboard API unavailable");
      await navigator.clipboard.writeText(block);
      copyNote.textContent = `copied ${pages.length} pages`;
      copyNote.hidden = false;
      if (copyNoteTimer) clearTimeout(copyNoteTimer);
      copyNoteTimer = setTimeout(() => {
        copyNote.hidden = true;
        copyNoteTimer = 0;
      }, 2000);
    } catch {
      showGroupError("Could not copy all pages to the clipboard.");
    }
  });

  sourceBtn.addEventListener("click", async () => {
    if (!sourcePanel.hidden) {
      sourcePanel.hidden = true;
      sourceBtn.setAttribute("aria-expanded", "false");
      return;
    }
    showGroupError("");
    sourceBtn.disabled = true;
    try {
      const record = await fetchSplitExchangeRecord(
        group.dataset.splitExchangeId,
        group.dataset.splitPrimaryJobId);
      sourcePanel.replaceChildren();
      const fields = [
        ["Original source prompt", record.sourcePrompt],
        ["Split instruction", record.instruction],
        ["Style note", record.styleNote],
        ["Suggested series AR", record.suggestedShape ? stage1ShapeLabel(record.suggestedShape, false) : ""],
      ];
      for (const [label, value] of fields) {
        if (!value) continue;
        const block = document.createElement("div");
        block.className = "split-source-block";
        const headingEl = document.createElement("strong");
        headingEl.textContent = label;
        const textEl = document.createElement("pre");
        textEl.className = "split-source-text";
        textEl.textContent = value;
        block.append(headingEl, textEl);
        sourcePanel.appendChild(block);
      }
      sourcePanel.hidden = false;
      sourceBtn.setAttribute("aria-expanded", "true");
    } catch (error) {
      showGroupError(`Could not load source prompt: ${error.message || error}`);
    } finally {
      sourceBtn.disabled = false;
    }
  });

  setSourceBtn.addEventListener("click", async () => {
    showGroupError("");
    setSourceBtn.disabled = true;
    try {
      const primaryJobId = group.dataset.splitPrimaryJobId;
      const [record, blobs] = await Promise.all([
        fetchSplitExchangeRecord(group.dataset.splitExchangeId, primaryJobId),
        fetchPrimaryJobInputBlobs(primaryJobId),
      ]);
      setComposerFromSplitSource(record.sourcePrompt, blobs);
      window.scrollTo({ top: 0, behavior: "smooth" });
      promptBox.focus({ preventScroll: true });
    } catch (error) {
      showGroupError(`Could not load source prompt: ${error.message || error}`);
    } finally {
      setSourceBtn.disabled = false;
    }
  });

  resplitBtn.addEventListener("click", async () => {
    showGroupError("");
    resplitBtn.disabled = true;
    try {
      const primaryJobId = group.dataset.splitPrimaryJobId;
      const [record, blobs] = await Promise.all([
        fetchSplitExchangeRecord(group.dataset.splitExchangeId, primaryJobId),
        fetchPrimaryJobInputBlobs(primaryJobId),
      ]);
      setComposerFromSplitSource(record.sourcePrompt, blobs);
      if (record.modelKey) {
        const found = stage1FindWriter("split", record.modelKey);
        if (found?.available) {
          stage1WritersByMode.split = [found.key];
          stage1WritersCustomized.split = true;
        }
      }
      setStage1Mode("split");
      const nextInstruction = record.instruction || promptSplitConfig?.defaultInstruction || "";
      stage1SyncingSplitInputs = true;
      try {
        MultiImageTextEntry.replace(stage1SplitInstruction, nextInstruction);
        MultiImageTextEntry.replace(stage1SplitStyle, record.styleNote || "");
      } finally {
        stage1SyncingSplitInputs = false;
      }
      stage1Bar.scrollIntoView({ behavior: "smooth", block: "nearest" });
      stage1SplitInstruction.focus();
    } catch (error) {
      showGroupError(`Could not prepare re-split: ${error.message || error}`);
    } finally {
      resplitBtn.disabled = false;
    }
  });

  rerenderBtn.addEventListener("click", async () => {
    showGroupError("");
    rerenderBtn.disabled = true;
    try {
      const primaryJobId = group.dataset.splitPrimaryJobId;
      const [record, blobs] = await Promise.all([
        fetchSplitExchangeRecord(group.dataset.splitExchangeId, primaryJobId),
        fetchPrimaryJobInputBlobs(primaryJobId),
      ]);
      if (record.status !== "succeeded" || !Array.isArray(record.images) || record.images.length < 2) {
        throw new Error("This saved split has no multi-part plan to re-render.");
      }
      const composerGens = checkedGeneratorKeys().filter((key) => !isDescribeGenKey(key));
      const groupGens = [...group.querySelectorAll(".split-maker")]
        .map((node) => node.dataset.gen)
        .filter((key) => key && generators.some((g) => g.key === key && g.available));
      const initialGens = composerGens.length ? composerGens : groupGens;
      const composerShape = el("opt-shape")?.value || "auto";
      const cardState = {
        id: `s1-${++stage1CardSeq}`,
        kind: "split",
        modelKey: record.modelKey || record.model || "",
        modelLabel: record.modelLabel || split.modelLabel,
        user: currentUsername(),
        sourcePrompt: record.sourcePrompt,
        revision: null,
        instruction: record.instruction || "",
        styleNote: record.styleNote || "",
        images: blobs.map((blob, idx) => ({ file: blob, name: `input${idx}.png` })),
        selectedGens: new Set(initialGens),
        requestedShape: composerShape,
        selectedShape: resolveInitialSeriesShape(composerShape, blobs.length > 0, record.suggestedShape),
        shapeCustomized: false,
        status: "ready",
        plan: {
          exchangeId: record.exchangeId,
          model: record.model,
          modelLabel: record.modelLabel || split.modelLabel,
          imageCount: record.imageCount,
          reason: record.reason || split.reason,
          suggestedShape: record.suggestedShape || null,
          images: record.images,
        },
        rewriteText: "",
        exchangeId: record.exchangeId,
        sourceJobId: primaryJobId || "",
        error: null,
        rendering: false,
        renderHistory: [],
        canRetry: false,
        startedAt: Date.now(),
        controller: null,
        timer: 0,
        el: null,
        pickerHost: null,
      };
      addStage1Card(cardState);
      stage1ResultsSection.scrollIntoView({ behavior: "smooth", block: "nearest" });
    } catch (error) {
      showGroupError(`Could not open saved split plan: ${error.message || error}`);
    } finally {
      rerenderBtn.disabled = false;
    }
  });
}

// ---------- Wire DOM Listeners ----------

for (const tab of stage1Bar.querySelectorAll("[data-stage1-mode]")) {
  tab.addEventListener("click", () => setStage1Mode(tab.dataset.stage1Mode));
}

if (promptSplitBtn) {
  promptSplitBtn.addEventListener("click", () => {
    setStage1Mode("split", { focus: true });
  });
}

stage1AddWriterBtn.addEventListener("click", () => {
  if (stage1Mode !== "rewrite" && stage1Mode !== "split") return;
  const writers = ensureStage1WritersForMode(stage1Mode);
  if (writers.length >= Stage1MaxWriters) return;
  const next = stage1AvailableWriterOptions(stage1Mode)
    .find((entry) => !writers.includes(entry.key));
  if (!next) return;
  writers.push(next.key);
  stage1WritersCustomized[stage1Mode] = true;
  renderStage1Writers();
  renderStage1ActionRow();
});

stage1MakeDefaultBtn.addEventListener("click", () => {
  if (stage1Mode !== "rewrite" && stage1Mode !== "split") return;
  const writers = ensureStage1WritersForMode(stage1Mode);
  const firstKey = writers[0];
  const entry = stage1FindWriter(stage1Mode, firstKey);
  if (!entry?.available) return;
  const slot = stage1SlotForMode(stage1Mode);
  promptToolModels[slot] = entry.key;
  if (writers.length === 1) stage1WritersCustomized[stage1Mode] = false;
  renderPromptAssistantModelSelectors();
  applyPromptAssistantModels();
  try {
    persistPersonalConfigurationSnapshot();
    stage1MakeDefaultBtn.hidden = true;
    stage1RunNote.textContent = `${entry.label} saved as default.`;
  } catch (error) {
    stage1Error.textContent = `Could not save default model: ${error.message || error}`;
  }
});

stage1RewriteInstruction.addEventListener("input", () => {
  stage1RewriteClearBtn.hidden = stage1RewriteInstruction.value.length === 0;
  renderStage1PresetSelector("rewrite");
});
stage1RewriteClearBtn.addEventListener("click", () => {
  MultiImageTextEntry.replace(stage1RewriteInstruction, "");
  stage1RewriteClearBtn.hidden = true;
  renderStage1PresetSelector("rewrite");
  stage1RewriteInstruction.focus();
});

stage1SplitInstruction.addEventListener("input", () => {
  saveStage1SplitSettings();
  renderStage1PresetSelector("split");
});
stage1SplitStyle.addEventListener("input", saveStage1SplitSettings);
stage1SplitDefaultBtn.addEventListener("click", () => {
  const def = promptSplitConfig?.defaultInstruction || "";
  MultiImageTextEntry.replace(stage1SplitInstruction, def);
  saveStage1SplitSettings();
  renderStage1PresetSelector("split");
  stage1SplitInstruction.focus();
});

for (const mode of ["split", "rewrite"]) {
  const dom = stage1PresetDom(mode);
  dom.select?.addEventListener("change", () => applyStage1PresetSelection(mode));
  dom.saveBtn?.addEventListener("click", () => openStage1PresetSaveBar(mode));
  dom.deleteBtn?.addEventListener("click", () => deleteSelectedStage1PersonalPreset(mode));
  dom.confirmBtn?.addEventListener("click", () => saveStage1PersonalPreset(mode));
  dom.cancelBtn?.addEventListener("click", () => {
    if (dom.bar) dom.bar.hidden = true;
    stage1Error.textContent = "";
  });
  dom.nameInput?.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      saveStage1PersonalPreset(mode);
    } else if (event.key === "Escape") {
      event.preventDefault();
      if (dom.bar) dom.bar.hidden = true;
      stage1Error.textContent = "";
    }
  });
}
refreshStage1PresetSelectors();

stage1RunBtn.addEventListener("click", startStage1Run);
if (stage1AutoRenderInput) {
  stage1AutoRenderInput.addEventListener("change", saveStage1AutoRenderSetting);
}

if (stage1ResultsCancelRunningBtn) {
  stage1ResultsCancelRunningBtn.addEventListener("click", () => {
    for (const card of [...stage1Cards]) {
      if (card.status === "running") card.controller?.abort();
    }
  });
}

stage1ResultsClearBtn.addEventListener("click", () => {
  for (const card of [...stage1Cards]) {
    if (card.status !== "running") removeStage1Card(card);
  }
});

el("opt-shape").addEventListener("change", refreshAllStage1CardPickers);
if (stage1AutoRenderInput) {
  stage1AutoRenderInput.checked = !!promptSplitSettings.stage1AutoRender;
}
