"use strict";
// Shared composer and goal-loop chooser. Page adapters supply catalog, preferences,
// eligible input rows, count updates, and persistence.

function defaultGeneratorPreferences() {
  return {
    showImageSection: true,
    showDescribeSection: true,
    hiddenGeneratorKeys: [],
    defaultSelectedKeys: generators.filter((g) => g.defaultOn).map((g) => g.key),
    presets: [],
    endpointConfigurations: [],
  };
}

function normalizeStandardGeneratorGroups(rawGroups) {
  if (!Array.isArray(rawGroups)) {
    throw new Error("standard generator groups are missing");
  }
  const groupIds = new Set();
  const groupNames = new Set();
  const groups = rawGroups.map((group) => {
    if (!group || typeof group !== "object" || Array.isArray(group) ||
        typeof group.id !== "string" || !/^[a-z0-9-]{1,64}$/.test(group.id) ||
        typeof group.name !== "string" || !group.name.trim() ||
        group.name.trim().length > 30) {
      throw new Error("a standard generator group is malformed");
    }
    const name = group.name.trim();
    if (groupIds.has(group.id) || groupNames.has(name.toLocaleLowerCase())) {
      throw new Error("standard generator group names and ids must be unique");
    }
    groupIds.add(group.id);
    groupNames.add(name.toLocaleLowerCase());
    return { id: group.id, name, generatorKeys: [] };
  });
  const groupsById = new Map(groups.map((group) => [group.id, group]));
  for (const generator of generators) {
    if (!Array.isArray(generator.standardGroupIds)) {
      throw new Error(`standard group memberships are missing for ${generator.key}`);
    }
    const endpointGroupIds = new Set();
    for (const groupId of generator.standardGroupIds) {
      if (typeof groupId !== "string" || !groupsById.has(groupId) ||
          endpointGroupIds.has(groupId)) {
        throw new Error(`standard group memberships are malformed for ${generator.key}`);
      }
      if (generator.kind === "describe") {
        throw new Error(`analysis endpoint ${generator.key} belongs to a generator group`);
      }
      endpointGroupIds.add(groupId);
      groupsById.get(groupId).generatorKeys.push(generator.key);
    }
  }
  for (const group of groups) {
    if (group.generatorKeys.length === 0) {
      throw new Error(`standard generator group ${group.name} has no endpoints`);
    }
  }
  return groups;
}

function normalizeGeneratorPreferences(raw) {
  const known = new Set(generators.map((g) => g.key));
  const imageKeys = new Set(generators.filter((g) => g.kind !== "describe").map((g) => g.key));
  if (!raw || typeof raw !== "object") throw new Error("generator preferences are malformed");
  if (typeof raw.showImageSection !== "boolean" ||
      typeof raw.showDescribeSection !== "boolean" ||
      !Array.isArray(raw.hiddenGeneratorKeys) ||
      !Array.isArray(raw.defaultSelectedKeys) ||
      !Array.isArray(raw.presets) ||
      (raw.endpointConfigurations !== undefined && !Array.isArray(raw.endpointConfigurations))) {
    throw new Error("generator preferences are incomplete");
  }
  const exactKeys = (values, field) => {
    const result = [];
    for (const value of values) {
      if (typeof value !== "string" || !known.has(value)) {
        throw new Error(`${field} contains unknown generator ${String(value)}`);
      }
      if (!result.includes(value)) result.push(value);
    }
    return result;
  };
  const hiddenGeneratorKeys = exactKeys(raw.hiddenGeneratorKeys, "hiddenGeneratorKeys");
  const hidden = new Set(hiddenGeneratorKeys);
  const defaultSelectedKeys = exactKeys(raw.defaultSelectedKeys, "defaultSelectedKeys");
  for (const key of defaultSelectedKeys) {
    if (hidden.has(key)) throw new Error(`hidden generator ${key} is selected by default`);
  }
  if (raw.presets.length > 20) throw new Error("too many personal generator buttons");
  const ids = new Set();
  const names = new Set();
  const presets = raw.presets.map((preset) => {
    if (!preset || typeof preset.id !== "string" ||
        !/^[A-Za-z0-9_-]{1,64}$/.test(preset.id) ||
        typeof preset.name !== "string" ||
        !preset.name.trim() || preset.name.trim().length > 30 ||
        !Array.isArray(preset.generatorKeys)) {
      throw new Error("a personal generator button is malformed");
    }
    const name = preset.name.trim();
    if (ids.has(preset.id) || names.has(name.toLocaleLowerCase())) {
      throw new Error("personal generator button names and ids must be unique");
    }
    ids.add(preset.id);
    names.add(name.toLocaleLowerCase());
    const generatorKeys = exactKeys(preset.generatorKeys, `preset ${name}`);
    for (const key of generatorKeys) {
      if (!imageKeys.has(key)) throw new Error(`preset ${name} contains a describe target`);
      if (hidden.has(key)) throw new Error(`preset ${name} contains hidden generator ${key}`);
    }
    return { id: preset.id, name, generatorKeys };
  });
  const endpointConfigurations = [];
  const configuredKeys = new Set();
  let configurationChars = 0;
  for (const configuration of raw.endpointConfigurations || []) {
    if (!configuration || typeof configuration !== "object" || Array.isArray(configuration) ||
        typeof configuration.key !== "string" || !known.has(configuration.key) ||
        configuredKeys.has(configuration.key)) {
      throw new Error("a per-endpoint generator configuration is malformed");
    }
    const extraText = configuration.extraText === null || configuration.extraText === undefined
      ? null
      : configuration.extraText;
    const notes = configuration.notes === null || configuration.notes === undefined
      ? null
      : configuration.notes;
    if ((extraText !== null && typeof extraText !== "string") ||
        (notes !== null && typeof notes !== "string") ||
        (extraText === null && notes === null)) {
      throw new Error(`per-endpoint configuration for ${configuration.key} is malformed`);
    }
    if (extraText !== null && extraText.length > generatorEndpointConfiguration.maxExtraTextChars) {
      throw new Error(`extra text for ${configuration.key} is too long`);
    }
    if (notes !== null && notes.length > generatorEndpointConfiguration.maxNotesChars) {
      throw new Error(`private notes for ${configuration.key} are too long`);
    }
    configurationChars += (extraText?.length || 0) + (notes?.length || 0);
    if (configurationChars > generatorEndpointConfiguration.maxConfigurationTotalChars) {
      throw new Error("per-endpoint generator configuration is too large");
    }
    configuredKeys.add(configuration.key);
    endpointConfigurations.push({ key: configuration.key, extraText, notes });
  }
  return {
    showImageSection: raw.showImageSection,
    showDescribeSection: raw.showDescribeSection,
    hiddenGeneratorKeys,
    defaultSelectedKeys,
    presets,
    endpointConfigurations,
  };
}

function endpointGenerator(key) {
  return generators.find((generator) => generator.key === key) || null;
}

function endpointConfigurationOverride(key, preferences = generatorPreferences) {
  return preferences?.endpointConfigurations?.find((configuration) => configuration.key === key) || null;
}

function effectiveEndpointField(key, field, preferences = generatorPreferences) {
  const generator = endpointGenerator(key);
  if (!generator) return "";
  const configuration = endpointConfigurationOverride(key, preferences);
  const override = configuration?.[field];
  if (override !== null && override !== undefined) return override;
  return field === "extraText"
    ? (generator.defaultExtraText || "")
    : (generator.defaultNotes || "");
}

function setEndpointFieldOverride(preferences, key, field, value) {
  const generator = endpointGenerator(key);
  if (!generator) throw new Error(`unknown generator ${key}`);
  const defaultValue = field === "extraText"
    ? (generator.defaultExtraText || "")
    : (generator.defaultNotes || "");
  let configuration = endpointConfigurationOverride(key, preferences);
  if (!configuration && value !== defaultValue) {
    configuration = { key, extraText: null, notes: null };
    preferences.endpointConfigurations.push(configuration);
  }
  if (configuration) {
    configuration[field] = value === defaultValue ? null : value;
    if (configuration.extraText === null && configuration.notes === null) {
      preferences.endpointConfigurations =
        preferences.endpointConfigurations.filter((candidate) => candidate !== configuration);
    }
  }
}


function loadGeneratorPreferences(cfg) {
  if (cfg.generatorPreferences) {
    return normalizeGeneratorPreferences(cfg.generatorPreferences);
  }
  if (!cfg.auth?.enabled) {
    const canonical = storedPersonalField("generatorPreferences");
    if (canonical !== undefined) return normalizeGeneratorPreferences(canonical);
    const stored = localStorage.getItem(GeneratorPreferencesLocalKey);
    if (stored !== null) return normalizeGeneratorPreferences(JSON.parse(stored));
  }
  return defaultGeneratorPreferences();
}


function generatorChooserTitle(key, baseTitle) {
  const notes = effectiveEndpointField(key, "notes").trim();
  return notes ? `${baseTitle}\n\nYour private notes:\n${notes}` : baseTitle;
}


function buildGenChip(g) {
    const label = document.createElement("label");
    label.className = "gen-toggle" + (g.available ? "" : " unavailable");
    const baseTitle = g.available
      ? g.detail
      : `${g.detail} — NOT AVAILABLE: ${g.availabilityProblem || "missing configuration"}`;
    label.title = generatorChooserTitle(g.key, baseTitle);

    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.value = g.key;
    cb.dataset.available = String(g.available);
    cb.dataset.imageCapable = String(!!g.imageCapable);
    cb.dataset.imageAspectOverride = String(!!g.imageAspectOverride);
    cb.dataset.kind = g.kind || "image";
    cb.dataset.requiresImage = String(!!g.requiresImage);
    cb.dataset.sketchCapable = String(!!g.sketchCapable);
    cb.disabled = !g.available;
    cb.checked = g.available && generatorPreferences.defaultSelectedKeys.includes(g.key);
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
  }

function applyGeneratorPreset(preset, { includeDescribe = false } = {}) {
  const wanted = new Set(preset.generatorKeys);
  const rows = [gensRow];
  if (includeDescribe && describeSectionIsOpen()) rows.push(describeRow);
  for (const row of rows) {
    for (const cb of row.querySelectorAll("input")) {
      cb.checked = !cb.disabled && wanted.has(cb.value);
      cb.closest(".gen-toggle").classList.toggle("checked", cb.checked);
    }
  }
  updateGeneratorCount();
}

function renderGeneratorPresetButtons() {
  const host = el("gen-personal-presets");
  host.replaceChildren();
  const standardSignatures = new Set();
  const appendButton = (preset, standard) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "generator-personal-preset";
    button.textContent = preset.name;
    button.title = standard
      ? `Set the complete image-generator selection to standard group “${preset.name}”`
      : `Set the complete image-generator selection to personal group “${preset.name}”`;
    button.addEventListener("click", () => applyGeneratorPreset(preset));
    host.appendChild(button);
  };
  const signature = (preset) =>
    `${preset.name.toLocaleLowerCase()}\n${[...preset.generatorKeys].sort().join("\n")}`;
  for (const preset of standardGeneratorGroups) {
    standardSignatures.add(signature(preset));
    appendButton(preset, true);
  }
  for (const preset of generatorPreferences?.presets || []) {
    // Production originally stored these three groups as one user's personal
    // presets. Once the catalog supplies an identical standard group, suppress
    // only that exact duplicate button; the personal record remains editable
    // and portable until its owner deletes it.
    if (standardSignatures.has(signature(preset))) continue;
    appendButton(preset, false);
  }
}


function initializeGeneratorConfig() {
  document.body.insertAdjacentHTML("beforeend", `<dialog id="generator-config-dialog">
  <form id="generator-config-form">
    <div class="compact-dialog-head">
      <h2>Generator configuration</h2>
      <button id="generator-config-close" type="button" aria-label="Close">&times;</button>
    </div>
    <p class="compact-dialog-note">
      Choose what appears in your generator pickers. Choose defaults and named groups.
      Configure extra text and notes for image and describe endpoints.
    </p>
    <div id="generator-config-tabs" role="tablist" aria-label="Generator chooser settings">
      <button type="button" role="tab" data-generator-config-view="shown" aria-selected="true">shown</button>
      <button type="button" role="tab" data-generator-config-view="defaults" aria-selected="false">defaults</button>
      <button type="button" role="tab" data-generator-config-view="groups" aria-selected="false">groups</button>
      <button type="button" role="tab" data-generator-config-view="endpoint" aria-selected="false">per endpoint</button>
    </div>
    <section id="generator-config-shown-panel" class="generator-config-panel" role="tabpanel">
      <p>Toggle which targets appear in your generator pickers. Hidden targets can only be restored here.</p>
      <div class="generator-config-sections">
        <label><input id="generator-config-show-image" type="checkbox"> show image-generation section</label>
        <label><input id="generator-config-show-describe" type="checkbox"> show describe section when images are attached</label>
      </div>
      <div id="generator-config-shown"></div>
    </section>
    <section id="generator-config-defaults-panel" class="generator-config-panel" role="tabpanel" hidden>
      <p>Toggle which visible targets start selected when you open a fresh page.</p>
      <div id="generator-config-defaults"></div>
    </section>
    <section id="generator-config-groups-panel" class="generator-config-panel" role="tabpanel" hidden>
      <div class="generator-config-subhead">
        <div id="generator-config-group-tabs" aria-label="Personal generator groups"></div>
        <button id="generator-config-add-preset" type="button">+ new group</button>
      </div>
      <div id="generator-config-preset-list"></div>
    </section>
    <section id="generator-config-endpoint-panel" class="generator-config-panel" role="tabpanel" hidden>
      <p>
        Extra text is appended only to the selected endpoint's copy of each prompt.
        Describe endpoints use the same extra-text and notes fields.
        Notes are private configuration: they are never sent to a provider or attached to a job.
      </p>
      <div class="generator-endpoint-layout">
        <div id="generator-config-endpoint-list" aria-label="Image and describe endpoints"></div>
        <div id="generator-config-endpoint-editor"></div>
      </div>
    </section>
    <div class="compact-dialog-actions">
      <span id="generator-config-status"></span>
      <button id="generator-config-cancel" type="button">cancel</button>
      <button id="generator-config-save" type="submit">save</button>
    </div>
  </form>
</dialog>`);
const generatorConfigDialog = el("generator-config-dialog");
const generatorConfigForm = el("generator-config-form");
const generatorConfigShown = el("generator-config-shown");
const generatorConfigDefaults = el("generator-config-defaults");
const generatorConfigGroupTabs = el("generator-config-group-tabs");
const generatorConfigPresetList = el("generator-config-preset-list");
const generatorConfigEndpointList = el("generator-config-endpoint-list");
const generatorConfigEndpointEditor = el("generator-config-endpoint-editor");
const generatorConfigStatus = el("generator-config-status");
let generatorConfigDraft = null;
let generatorConfigView = "shown";
let generatorConfigActivePresetId = null;
let generatorConfigActiveEndpointKey = null;

function copyGeneratorPreferences(source) {
  return {
    showImageSection: source.showImageSection,
    showDescribeSection: source.showDescribeSection,
    hiddenGeneratorKeys: [...source.hiddenGeneratorKeys],
    defaultSelectedKeys: [...source.defaultSelectedKeys],
    presets: source.presets.map((preset) => ({
      id: preset.id,
      name: preset.name,
      generatorKeys: [...preset.generatorKeys],
    })),
    endpointConfigurations: source.endpointConfigurations.map((configuration) => ({
      key: configuration.key,
      extraText: configuration.extraText,
      notes: configuration.notes,
    })),
  };
}

function setDraftKey(listName, key, enabled) {
  const values = new Set(generatorConfigDraft[listName]);
  if (enabled) values.add(key);
  else values.delete(key);
  generatorConfigDraft[listName] = [...values];
}

function renderGeneratorConfigChoices(host, mode) {
  host.replaceChildren();
  const hidden = new Set(generatorConfigDraft.hiddenGeneratorKeys);
  const selected = new Set(generatorConfigDraft.defaultSelectedKeys);
  for (const [kind, title] of [["image", "make image"], ["describe", "describe image"]]) {
    const section = document.createElement("section");
    section.className = "generator-config-target-section";
    const heading = document.createElement("h3");
    heading.textContent = title;
    section.appendChild(heading);
    const grid = document.createElement("div");
    grid.className = "generator-config-choice-grid";
    const choices = generators.filter((generator) =>
      (generator.kind === "describe" ? "describe" : "image") === kind
      && (mode === "shown" || !hidden.has(generator.key)));
    for (const generator of choices) {
      const choice = document.createElement("label");
      choice.className = "generator-config-choice";
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.checked = mode === "shown"
        ? !hidden.has(generator.key)
        : selected.has(generator.key);
      checkbox.disabled = mode === "defaults" && !generator.available;
      choice.classList.toggle("selected", checkbox.checked);
      choice.classList.toggle("unavailable", checkbox.disabled);
      if (!generator.available) {
        choice.title = `Unavailable now: ${generator.availabilityProblem || "not configured"}`;
      }
      const text = document.createElement("span");
      text.textContent = generator.label;
      checkbox.addEventListener("change", () => {
        choice.classList.toggle("selected", checkbox.checked);
        if (mode === "shown") {
          setDraftKey("hiddenGeneratorKeys", generator.key, !checkbox.checked);
          if (!checkbox.checked) {
            setDraftKey("defaultSelectedKeys", generator.key, false);
            for (const preset of generatorConfigDraft.presets) {
              preset.generatorKeys = preset.generatorKeys.filter((key) => key !== generator.key);
            }
          }
          renderGeneratorConfig();
        } else {
          setDraftKey("defaultSelectedKeys", generator.key, checkbox.checked);
        }
      });
      choice.append(checkbox, text);
      grid.appendChild(choice);
    }
    if (choices.length === 0) {
      const empty = document.createElement("p");
      empty.className = "generator-config-empty";
      empty.textContent = mode === "defaults"
        ? "No visible targets in this section."
        : "No targets in this section.";
      grid.appendChild(empty);
    }
    section.appendChild(grid);
    host.appendChild(section);
  }
}

function renderGeneratorConfigGroupTabs() {
  generatorConfigGroupTabs.replaceChildren();
  for (const preset of generatorConfigDraft.presets) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = preset.name.trim() || "unnamed group";
    button.classList.toggle("selected", preset.id === generatorConfigActivePresetId);
    button.addEventListener("click", () => {
      generatorConfigActivePresetId = preset.id;
      renderGeneratorConfigPresets();
    });
    generatorConfigGroupTabs.appendChild(button);
  }
}

function renderGeneratorConfigPresets() {
  if (generatorConfigActivePresetId &&
      !generatorConfigDraft.presets.some((preset) => preset.id === generatorConfigActivePresetId)) {
    generatorConfigActivePresetId = null;
  }
  if (!generatorConfigActivePresetId && generatorConfigDraft.presets.length > 0) {
    generatorConfigActivePresetId = generatorConfigDraft.presets[0].id;
  }
  renderGeneratorConfigGroupTabs();
  generatorConfigPresetList.replaceChildren();
  const preset = generatorConfigDraft.presets.find(
    (candidate) => candidate.id === generatorConfigActivePresetId);
  if (!preset) {
    const empty = document.createElement("p");
    empty.className = "generator-config-empty";
    empty.textContent = "Standard groups are supplied by the server. No personal groups yet.";
    generatorConfigPresetList.appendChild(empty);
    return;
  }

  const card = document.createElement("fieldset");
  card.className = "generator-config-preset";
  const top = document.createElement("div");
  top.className = "generator-config-preset-head";
  const name = document.createElement("input");
  name.type = "text";
  name.maxLength = 30;
  name.value = preset.name;
  name.placeholder = "group name";
  name.setAttribute("aria-label", "Personal generator group name");
  name.addEventListener("input", () => {
    preset.name = name.value;
    renderGeneratorConfigGroupTabs();
  });
  const remove = document.createElement("button");
  remove.type = "button";
  remove.textContent = "delete group";
  remove.addEventListener("click", () => {
    generatorConfigDraft.presets =
      generatorConfigDraft.presets.filter((candidate) => candidate.id !== preset.id);
    generatorConfigActivePresetId = generatorConfigDraft.presets[0]?.id || null;
    renderGeneratorConfigPresets();
  });
  top.append(name, remove);
  card.appendChild(top);

  const section = document.createElement("section");
  section.className = "generator-config-target-section";
  const heading = document.createElement("h3");
  heading.textContent = "image generators in this group";
  section.appendChild(heading);
  const grid = document.createElement("div");
  grid.className = "generator-config-choice-grid";
  const visibleImageGenerators = generators.filter((generator) =>
    generator.kind !== "describe"
    && !generatorConfigDraft.hiddenGeneratorKeys.includes(generator.key));
  for (const generator of visibleImageGenerators) {
    const choice = document.createElement("label");
    choice.className = "generator-config-choice";
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = preset.generatorKeys.includes(generator.key);
    checkbox.disabled = !generator.available;
    choice.classList.toggle("selected", checkbox.checked);
    choice.classList.toggle("unavailable", checkbox.disabled);
    if (!generator.available) {
      choice.title = `Unavailable now: ${generator.availabilityProblem || "not configured"}`;
    }
    const text = document.createElement("span");
    text.textContent = generator.label;
    checkbox.addEventListener("change", () => {
      const keys = new Set(preset.generatorKeys);
      if (checkbox.checked) keys.add(generator.key);
      else keys.delete(generator.key);
      preset.generatorKeys = [...keys];
      choice.classList.toggle("selected", checkbox.checked);
    });
    choice.append(checkbox, text);
    grid.appendChild(choice);
  }
  if (visibleImageGenerators.length === 0) {
    const empty = document.createElement("p");
    empty.className = "generator-config-empty";
    empty.textContent = "Show at least one image generator before configuring this group.";
    grid.appendChild(empty);
  }
  section.appendChild(grid);
  card.appendChild(section);
  generatorConfigPresetList.appendChild(card);
}

function refreshGeneratorEndpointListMarkers() {
  for (const button of generatorConfigEndpointList.querySelectorAll("button[data-generator-key]")) {
    const key = button.dataset.generatorKey;
    const extraText = effectiveEndpointField(key, "extraText", generatorConfigDraft).trim();
    const notes = effectiveEndpointField(key, "notes", generatorConfigDraft).trim();
    const badges = button.querySelector(".generator-endpoint-badges");
    badges.replaceChildren();
    for (const [text, present] of [["extra text", extraText.length > 0], ["notes", notes.length > 0]]) {
      if (!present) continue;
      const badge = document.createElement("span");
      badge.textContent = text;
      badges.appendChild(badge);
    }
    button.classList.toggle("selected", key === generatorConfigActiveEndpointKey);
    button.classList.toggle("customized", !!endpointConfigurationOverride(key, generatorConfigDraft));
  }
}

function renderGeneratorConfigEndpointEditor() {
  generatorConfigEndpointEditor.replaceChildren();
  const generator = endpointGenerator(generatorConfigActiveEndpointKey);
  if (!generator) {
    const empty = document.createElement("p");
    empty.className = "generator-config-empty";
    empty.textContent = "No endpoint is available to configure.";
    generatorConfigEndpointEditor.appendChild(empty);
    return;
  }

  const heading = document.createElement("h3");
  heading.textContent = generator.label;
  const detail = document.createElement("p");
  detail.className = "generator-endpoint-detail";
  detail.textContent = generator.detail;
  generatorConfigEndpointEditor.append(heading, detail);

  const extraTextDescription = generator.key === "describe-ideogram"
    ? "Ideogram describe does not accept an instruction. Extra text is stored with the job, but it is not sent."
    : generator.kind === "describe"
      ? "Appended only to this endpoint's instruction after a blank line. Leave blank to send the composer prompt unchanged."
      : "Appended only to this endpoint's prompt after a blank line. Leave blank to send the composer prompt unchanged.";

  const buildField = (field, title, description, maxLength) => {
    const wrapper = document.createElement("section");
    wrapper.className = "generator-endpoint-field";
    const fieldHead = document.createElement("div");
    const label = document.createElement("label");
    const textareaId = `generator-endpoint-${field}`;
    label.htmlFor = textareaId;
    label.textContent = title;
    const reset = document.createElement("button");
    reset.type = "button";
    reset.textContent = "reset to default";
    reset.title = `Restore the server-provided default ${title.toLowerCase()} for ${generator.label}`;
    fieldHead.append(label, reset);
    const explanation = document.createElement("p");
    explanation.textContent = description;
    const textarea = document.createElement("textarea");
    textarea.id = textareaId;
    textarea.rows = field === "extraText" ? 7 : 6;
    textarea.maxLength = maxLength;
    textarea.spellcheck = field === "notes";
    textarea.value = effectiveEndpointField(generator.key, field, generatorConfigDraft);
    textarea.addEventListener("input", () => {
      setEndpointFieldOverride(generatorConfigDraft, generator.key, field, textarea.value);
      refreshGeneratorEndpointListMarkers();
    });
    reset.addEventListener("click", () => {
      const defaultValue = field === "extraText"
        ? (generator.defaultExtraText || "")
        : (generator.defaultNotes || "");
      textarea.value = defaultValue;
      setEndpointFieldOverride(generatorConfigDraft, generator.key, field, defaultValue);
      refreshGeneratorEndpointListMarkers();
      textarea.focus();
    });
    wrapper.append(fieldHead, explanation, textarea);
    return wrapper;
  };

  generatorConfigEndpointEditor.append(
    buildField(
      "extraText",
      "Append extra text",
      extraTextDescription,
      generatorEndpointConfiguration.maxExtraTextChars),
    buildField(
      "notes",
      "Private notes",
      "Visible only in your generator configuration and chooser tooltip. Never sent to providers or stored with jobs.",
      generatorEndpointConfiguration.maxNotesChars));
}

function renderGeneratorConfigEndpoints() {
  if (!generatorConfigActiveEndpointKey ||
      !generators.some((generator) => generator.key === generatorConfigActiveEndpointKey)) {
    generatorConfigActiveEndpointKey = generators[0]?.key || null;
  }
  generatorConfigEndpointList.replaceChildren();
  for (const [kind, title] of [["image", "make image"], ["describe", "describe image"]]) {
    const heading = document.createElement("h3");
    heading.className = "generator-endpoint-group";
    heading.textContent = title;
    generatorConfigEndpointList.appendChild(heading);
    const group = generators.filter((generator) =>
      (generator.kind === "describe" ? "describe" : "image") === kind);
    for (const generator of group) {
      const button = document.createElement("button");
      button.type = "button";
      button.dataset.generatorKey = generator.key;
      button.title = generator.detail;
      const name = document.createElement("span");
      name.className = "generator-endpoint-name";
      name.textContent = generator.label;
      const badges = document.createElement("span");
      badges.className = "generator-endpoint-badges";
      button.append(name, badges);
      button.addEventListener("click", () => {
        generatorConfigActiveEndpointKey = generator.key;
        refreshGeneratorEndpointListMarkers();
        renderGeneratorConfigEndpointEditor();
      });
      generatorConfigEndpointList.appendChild(button);
    }
    if (group.length === 0) {
      const empty = document.createElement("p");
      empty.className = "generator-config-empty";
      empty.textContent = "No targets in this section.";
      generatorConfigEndpointList.appendChild(empty);
    }
  }
  refreshGeneratorEndpointListMarkers();
  renderGeneratorConfigEndpointEditor();
}

function setGeneratorConfigView(view) {
  generatorConfigView = view;
  for (const button of el("generator-config-tabs").querySelectorAll("[data-generator-config-view]")) {
    const active = button.dataset.generatorConfigView === view;
    button.setAttribute("aria-selected", String(active));
    button.classList.toggle("selected", active);
  }
  for (const candidate of ["shown", "defaults", "groups", "endpoint"]) {
    el(`generator-config-${candidate}-panel`).hidden = candidate !== view;
  }
}

function renderGeneratorConfig() {
  el("generator-config-show-image").checked = generatorConfigDraft.showImageSection;
  el("generator-config-show-describe").checked = generatorConfigDraft.showDescribeSection;
  renderGeneratorConfigChoices(generatorConfigShown, "shown");
  renderGeneratorConfigChoices(generatorConfigDefaults, "defaults");
  renderGeneratorConfigPresets();
  renderGeneratorConfigEndpoints();
  setGeneratorConfigView(generatorConfigView);
}

function openGeneratorConfig() {
  generatorConfigDraft = copyGeneratorPreferences(generatorPreferences);
  generatorConfigStatus.textContent = "";
  generatorConfigActivePresetId = generatorConfigDraft.presets[0]?.id || null;
  generatorConfigActiveEndpointKey = generators[0]?.key || null;
  generatorConfigView = "shown";
  renderGeneratorConfig();
  generatorConfigDialog.showModal();
}

for (const button of el("generator-config-tabs").querySelectorAll("[data-generator-config-view]")) {
  button.addEventListener("click", () => setGeneratorConfigView(button.dataset.generatorConfigView));
}

el("generator-config-toggle").addEventListener("click", openGeneratorConfig);
el("generator-config-close").addEventListener("click", () => generatorConfigDialog.close());
el("generator-config-cancel").addEventListener("click", () => generatorConfigDialog.close());
el("generator-config-show-image").addEventListener("change", (event) => {
  generatorConfigDraft.showImageSection = event.target.checked;
});
el("generator-config-show-describe").addEventListener("change", (event) => {
  generatorConfigDraft.showDescribeSection = event.target.checked;
});
el("generator-config-add-preset").addEventListener("click", () => {
  const id = typeof crypto.randomUUID === "function"
    ? crypto.randomUUID().replaceAll("-", "")
    : `${Date.now()}_${Math.random().toString(16).slice(2)}`;
  generatorConfigDraft.presets.push({ id, name: "", generatorKeys: [] });
  generatorConfigActivePresetId = id;
  renderGeneratorConfigPresets();
  setGeneratorConfigView("groups");
  generatorConfigPresetList.querySelector("input[type=text]")?.focus();
});
generatorConfigForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  generatorConfigStatus.className = "";
  let normalized;
  try {
    normalized = normalizeGeneratorPreferences(generatorConfigDraft);
  } catch (error) {
    generatorConfigStatus.textContent = String(error);
    generatorConfigStatus.className = "error";
    return;
  }
  const save = el("generator-config-save");
  save.disabled = true;
  generatorConfigStatus.textContent = "saving…";
  try {
    if (authInfo.enabled) {
      const response = await fetch(apiUrl("api/generator-preferences"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(normalized),
      });
      if (response.status === 401) { location.reload(); return; }
      const body = await response.json();
      if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
    }
    await generatorPreferencesSaved(normalized);
    generatorConfigDialog.close();
  } catch (error) {
    generatorConfigStatus.textContent = String(error);
    generatorConfigStatus.className = "error";
  } finally {
    save.disabled = false;
  }
});

}

function setAllGenerators(mode) {
  for (const cb of bulkGeneratorInputs()) {
    cb.checked = mode === "enable" ? true : mode === "disable" ? false : !cb.checked;
    cb.closest(".gen-toggle").classList.toggle("checked", cb.checked);
  }
  updateGeneratorCount();
}

function initializeGeneratorBulkControls() {
el("gens-enable-all").addEventListener("click", () => setAllGenerators("enable"));
el("gens-disable-all").addEventListener("click", () => setAllGenerators("disable"));
el("gens-toggle-all").addEventListener("click", () => setAllGenerators("toggle"));
el("gens-default").addEventListener("click", () => applyGeneratorPreset({
  generatorKeys: generatorPreferences.defaultSelectedKeys,
}, { includeDescribe: true }));
}

function initializeGeneratorControls() {
  el("generator-controls-host").outerHTML = `<div id="gen-controls">
        <button id="gens-enable-all" class="generator-main-control" type="button"
          title="Enable every available target in open sections. A closed describe section is not changed.">Enable all</button>
        <button id="gens-disable-all" class="generator-main-control" type="button"
          title="Disable every available target in open sections. A closed describe section is not changed.">Disable all</button>
        <button id="gens-toggle-all" class="generator-main-control" type="button"
          title="Toggle every available target in open sections. A closed describe section is not changed.">Toggle all</button>
        <button id="gens-default" class="generator-main-control" type="button"
          title="Restore your configured defaults in open sections. A closed describe section is not changed.">Default</button>
        <span id="gen-personal-presets" class="generator-main-control"></span>
        <button id="gens-enable-image-capable" type="button"
          class="image-only-action generator-main-control" hidden
          title="Enable every available image-capable target in open sections. A closed describe section is not changed.">Enable image-capable</button>
        <button id="gens-disable-text-only" type="button"
          class="image-only-action generator-main-control" hidden
          title="Disable endpoints that can't accept the attached image (they'd run from the prompt text only)">Disable text-only</button>
        <button id="generator-config-toggle" type="button" aria-label="Configure generator chooser"
          title="Configure visible targets, defaults, groups, and per-endpoint text">⚙</button>
      </div>`;
}
