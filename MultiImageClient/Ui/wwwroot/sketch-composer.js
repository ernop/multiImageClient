// Composition sketch editor modal (extracted from app.js).

// ---------- composition sketch composer ----------

// Layout sketches are spatial maps made from colored freehand marks, fills,
// and shapes, with one meaning per color ("red = the planet"). The flattened
// PNG is attached as a normal input image and the prompt receives an
// affirmative legend describing placement, coverage, and flexible blank space.

// Fixed, visually distinct drawing palette. Names appear verbatim in the
// generated legend paragraph, so they stay plain color words; hex values are
// exact-matched when detecting which colors a sketch actually uses (stroke
// cores keep the exact fill even though edges antialias).
const SketchPalette = [
  { name: "red", hex: "#e53935" },
  { name: "blue", hex: "#1e88e5" },
  { name: "green", hex: "#43a047" },
  { name: "orange", hex: "#fb8c00" },
  { name: "purple", hex: "#8e24aa" },
  { name: "cyan", hex: "#00acc1" },
  { name: "yellow", hex: "#fdd835" },
  { name: "brown", hex: "#6d4c41" },
];
function cloneSketchPalette(palette) {
  return palette.map((color) => ({ name: color.name, hex: color.hex.toLowerCase() }));
}
let sketchWorkingPalette = cloneSketchPalette(SketchPalette);
// Internal canvas resolution per shape; the sketch attaches at exactly this
// size, so match input image AR defaults follow the chosen shape.
const SketchCanvasSizes = {
  square: [1024, 1024],
  landscape: [1152, 768],
  portrait: [768, 1152],
  wide: [1280, 720],
  tall: [720, 1280],
};
const SketchScaleMinEdge = 1;
const SketchScaleMaxEdge = 4096;
const SketchScaleMaxPixels = 4096 * 4096;
// The legend paragraph starts with this exact marker so re-attaching a
// revised sketch replaces the previous paragraph instead of stacking copies.
const SketchLegendMarker = "Composition sketch legend:";
const SketchHistoryDepth = 50;
const SketchActionLimit = 200;
const SketchFillActionLimit = 32;
const SketchStrokePointLimit = 4096;
const SketchReplayCheckpointGap = 8;
const SketchReplayMaxBytes = 24 * 1024 * 1024;
const SketchToolNames = {
  "select-rect": "rect select",
  "select-free": "free select",
  pencil: "pencil",
  brush: "brush",
  "soft-brush": "soft brush",
  spray: "spray",
  eraser: "eraser",
  fill: "tolerance fill",
  line: "line",
  rectangle: "rectangle",
  "rounded-rectangle": "rounded rectangle",
  ellipse: "ellipse",
  triangle: "triangle",
  diamond: "diamond",
  pentagon: "pentagon",
  hexagon: "hexagon",
  octagon: "octagon",
  star: "star",
  spread: "spread",
  mix: "mix",
  lighten: "lighten",
  fuzz: "fuzz",
  eyedropper: "eyedropper",
  recolor: "recolor",
};
const SketchShapeTools = new Set([
  "rectangle",
  "rounded-rectangle",
  "ellipse",
  "triangle",
  "diamond",
  "pentagon",
  "hexagon",
  "octagon",
  "star",
]);
const SketchHardStrokeTools = new Set(["pencil", "brush", "eraser", "line"]);
const SketchPaintStampTools = new Set(["soft-brush", "spray"]);
const SketchBlendTools = new Set(["spread", "mix", "lighten", "fuzz"]);
const SketchSelectionTools = new Set(["select-rect", "select-free"]);
const SketchEdgeStyles = new Set(["hard", "soft", "dashed"]);
const SketchFillRules = new Set(["outline", "solid", "soft"]);
const SketchSelectionModes = new Set(["move", "duplicate", "stretch", "rotate"]);
// "canvas" resizes the canvas itself to the pasted image's aspect ratio; the
// other three place the image onto the current canvas unchanged.
const SketchPasteFitModes = new Set(["canvas", "cover", "contain", "stretch"]);
// A "match image" paste upscales small sources so the canvas long edge is at
// least this many pixels; drawing tools stay usable at published-size scale.
const SketchMatchImageMinLongEdge = 1024;
const SketchDitherMethods = new Set(["nearest", "floyd", "bayer", "group"]);
const SketchClickTools = new Set(["fill", "eyedropper", "recolor"]);
const SketchBayer8 = [
  [0, 32, 8, 40, 2, 34, 10, 42],
  [48, 16, 56, 24, 50, 18, 58, 26],
  [12, 44, 4, 36, 14, 46, 6, 38],
  [60, 28, 52, 20, 62, 30, 54, 22],
  [3, 35, 11, 43, 1, 33, 9, 41],
  [51, 19, 59, 27, 49, 17, 57, 25],
  [15, 47, 7, 39, 13, 45, 5, 37],
  [63, 31, 55, 23, 61, 29, 53, 21],
];
const SketchAspectBySize = new Map(
  Object.entries(SketchCanvasSizes).map(([name, size]) => [`${size[0]}x${size[1]}`, name]));

const sketchDialog = el("sketch-dialog");
const sketchCanvas = el("sketch-canvas");
// Live strokes are the hot path. Pixel reads snapshot once per gesture.
const sketchCtx = sketchCanvas.getContext("2d", { desynchronized: true });
const sketchOverlay = el("sketch-overlay");
const sketchOverlayCtx = sketchOverlay.getContext("2d");
const sketchStatus = el("sketch-status");
const sketchAspectSelect = el("sketch-aspect");
const sketchBrushInput = el("sketch-brush");
const sketchFillToleranceInput = el("sketch-fill-tolerance");
const sketchFillToleranceControl = el("sketch-fill-tolerance-control");
const sketchBlendStrengthInput = el("sketch-blend-strength");
const sketchBlendStrengthControl = el("sketch-blend-strength-control");
const sketchEdgeStyleSelect = el("sketch-edge-style");
const sketchFillRuleSelect = el("sketch-fill-rule");
const sketchShapeConfig = el("sketch-shape-config");
const sketchPasteFitSelect = el("sketch-paste-fit");
const sketchDitherMethodSelect = el("sketch-dither-method");
const sketchDitherWhiteInput = el("sketch-dither-white");
const sketchDitherApplyButton = el("sketch-dither-apply");
const sketchImportFileInput = el("sketch-import-file");
const sketchCursor = el("sketch-brush-cursor");
const sketchActionList = el("sketch-action-list");
const sketchMeaningInputs = [];
let sketchActiveColor = 0;
let sketchActiveTool = "brush";
let sketchActions = [];
let sketchHistoryPast = [];
let sketchHistoryFuture = [];
let sketchGesture = null;
let sketchActionSequence = 0;
let sketchFillVisited = new Uint32Array(0);
let sketchFillQueue = new Int32Array(0);
let sketchFillVisitGeneration = 0;
let sketchAspect = "square";    // last applied canvas shape, for revert
let sketchAttachedFile = null;  // exact File last attached, replaced on re-attach
let sketchAttachedState = null; // metadata captured with those exact PNG bytes
let sketchEditorNotice = "";
let sketchSelection = null;     // { type, points, bounds } logical selection
let sketchSelectionOp = null;   // null | move | duplicate | stretch | rotate
let sketchOverlayFrame = 0;
let sketchOverlayRaf = 0;
let sketchGestureRaf = 0;
let sketchCanvasRect = null;
let sketchScratchCanvas = null;
let sketchScratchCtx = null;
let sketchReplayCheckpoints = [];

function sketchBlank() {
  sketchCtx.fillStyle = "#ffffff";
  sketchCtx.fillRect(0, 0, sketchCanvas.width, sketchCanvas.height);
}
sketchBlank();

// Whether the composer currently holds the exact sketch attachment represented
// by the editor. New drawings use File identity; set-active restorations bind
// the explicitly recorded input index to its fetched Blob identity.
function sketchIsAttached() {
  return sketchAttachedFile != null
    && sketchAttachedState != null
    && inputImageItems.some((item) => item.file === sketchAttachedFile);
}

function resetSketchEditor(notice = "") {
  if (sketchDialog.open) sketchDialog.close();
  sketchAspect = "square";
  resizeSketchCanvasToSize(...SketchCanvasSizes[sketchAspect]);
  syncSketchAspectSelectToCanvas();
  clearSketchSelection();
  resetSketchActions();
  setSketchWorkingPalette(SketchPalette, { meanings: SketchPalette.map(() => "") });
  setSketchColor(0);
  setSketchTool("brush");
  sketchAttachedFile = null;
  sketchAttachedState = null;
  sketchEditorNotice = notice;
  sketchStatus.textContent = "";
  sketchStatus.classList.remove("error");
}

function normalizeRecordedSketchState(value, inputCount) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("recorded sketch identity is not an object");
  }
  const keys = Object.keys(value).sort();
  if (value.version === 1) {
    const expectedKeys = ["aspect", "inputIndex", "meanings", "version"];
    if (keys.length !== expectedKeys.length
        || keys.some((key, index) => key !== expectedKeys[index])) {
      throw new Error("recorded sketch identity has missing or unknown fields");
    }
    if (!Number.isInteger(value.inputIndex)
        || value.inputIndex < 0
        || value.inputIndex >= inputCount) {
      throw new Error("recorded sketch attachment index is outside this job's inputs");
    }
    if (!Object.hasOwn(SketchCanvasSizes, value.aspect)) {
      throw new Error(`recorded sketch has unknown aspect ${value.aspect}`);
    }
    if (!Array.isArray(value.meanings)
        || value.meanings.length !== SketchPalette.length
        || value.meanings.some((meaning) =>
          typeof meaning !== "string"
          || meaning.length > 120
          || /[\u0000-\u001f\u007f]/.test(meaning))) {
      throw new Error("recorded sketch meanings do not match the editor contract");
    }
    return {
      version: 1,
      inputIndex: value.inputIndex,
      aspect: value.aspect,
      palette: cloneSketchPalette(SketchPalette),
      meanings: value.meanings.map((meaning) => meaning.trim()),
    };
  }
  if (value.version !== 2) {
    throw new Error(`unsupported recorded sketch identity version ${value.version}`);
  }
  const expectedKeys = ["aspect", "inputIndex", "meanings", "palette", "version"];
  if (keys.length !== expectedKeys.length
      || keys.some((key, index) => key !== expectedKeys[index])) {
    throw new Error("recorded sketch identity has missing or unknown fields");
  }
  if (!Number.isInteger(value.inputIndex)
      || value.inputIndex < 0
      || value.inputIndex >= inputCount) {
    throw new Error("recorded sketch attachment index is outside this job's inputs");
  }
  if (!Object.hasOwn(SketchCanvasSizes, value.aspect)) {
    throw new Error(`recorded sketch has unknown aspect ${value.aspect}`);
  }
  const palette = normalizeRecordedSketchPalette(value.palette);
  if (!Array.isArray(value.meanings)
      || value.meanings.length !== palette.length
      || value.meanings.some((meaning) =>
        typeof meaning !== "string"
        || meaning.length > 120
        || /[\u0000-\u001f\u007f]/.test(meaning))) {
    throw new Error("recorded sketch meanings do not match the editor contract");
  }
  return {
    version: 2,
    inputIndex: value.inputIndex,
    aspect: value.aspect,
    palette,
    meanings: value.meanings.map((meaning) => meaning.trim()),
  };
}

function normalizeRecordedSketchPalette(raw) {
  if (!Array.isArray(raw) || raw.length < 1 || raw.length > SketchPalette.length) {
    throw new Error("recorded sketch palette does not match the editor contract");
  }
  const names = new Set();
  const hexes = new Set();
  return raw.map((entry, index) => {
    if (!entry || typeof entry !== "object" || Array.isArray(entry)) {
      throw new Error(`recorded sketch palette ${index} is not an object`);
    }
    const keys = Object.keys(entry).sort();
    if (keys.length !== 2 || keys[0] !== "hex" || keys[1] !== "name") {
      throw new Error(`recorded sketch palette ${index} has missing or unknown fields`);
    }
    if (typeof entry.name !== "string" || typeof entry.hex !== "string") {
      throw new Error(`recorded sketch palette ${index} name and hex must be strings`);
    }
    const name = entry.name.trim();
    const hex = entry.hex.trim().toLowerCase();
    if (!name || name.length > 40 || /[\u0000-\u001f\u007f]/.test(name)) {
      throw new Error(`recorded sketch palette ${index} name is invalid`);
    }
    if (!/^#[0-9a-f]{6}$/.test(hex) || hex === "#ffffff") {
      throw new Error(`recorded sketch palette ${index} hex is invalid`);
    }
    const nameKey = name.toLocaleLowerCase();
    if (names.has(nameKey) || hexes.has(hex)) {
      throw new Error(`recorded sketch palette ${index} duplicates a color`);
    }
    names.add(nameKey);
    hexes.add(hex);
    return { name, hex };
  });
}

async function prepareRestoredSketch(blob, state) {
  const bitmap = await createImageBitmap(blob);
  if (bitmap.width < SketchScaleMinEdge || bitmap.height < SketchScaleMinEdge) {
    bitmap.close();
    throw new Error("recorded sketch has no pixels");
  }
  return { bitmap, state };
}

function applyRestoredSketchEditor(nextItems, prepared) {
  const { bitmap, state } = prepared;
  try {
    const item = nextItems[state.inputIndex];
    if (!item || item.file.type !== "image/png") {
      throw new Error("recorded sketch attachment is missing or is not PNG");
    }
    sketchAspect = state.aspect;
    sketchAspectSelect.value = sketchAspect;
    resizeSketchCanvasToSize(bitmap.width, bitmap.height);
    sketchCtx.clearRect(0, 0, bitmap.width, bitmap.height);
    sketchCtx.drawImage(bitmap, 0, 0);
    const restoredPixels = sketchCtx.getImageData(0, 0, bitmap.width, bitmap.height);
    sketchActions = [{
      id: ++sketchActionSequence,
      kind: "base",
      visible: true,
      imageData: restoredPixels,
    }];
    sketchHistoryPast = [];
    sketchHistoryFuture = [];
    sketchGesture = null;
    clearSketchReplayCheckpoints();
    clearSketchSelection();
    renderSketchActions();
    renderSketchActionList();
    setSketchWorkingPalette(state.palette, { meanings: state.meanings });
    sketchAttachedFile = item.file;
    sketchAttachedState = currentSketchAttachedState();
    sketchEditorNotice = "";
    setSketchColor(0);
    setSketchTool("brush");
  } finally {
    bitmap.close();
  }
}

function promptHasSketchLegend(value) {
  return value
    .split(/\n{2,}/)
    .some((paragraph) => paragraph.trimStart().startsWith(SketchLegendMarker));
}

function currentSketchAttachedState() {
  return {
    version: 2,
    aspect: sketchAspectNameFromSize(sketchCanvas.width, sketchCanvas.height)
      || sketchNearestPublishedAspect(sketchCanvas.width, sketchCanvas.height),
    palette: cloneSketchPalette(sketchWorkingPalette),
    meanings: sketchMeaningInputs.map((meaning) => meaning.value.trim()),
  };
}

function attachedSketchSubmission() {
  if (!sketchIsAttached()) return null;
  const inputIndex = inputImageItems.findIndex((item) => item.file === sketchAttachedFile);
  if (inputIndex < 0) return null;
  return {
    ...currentSketchAttachedState(),
    inputIndex,
  };
}

function rebuildSketchLegendFields(meanings) {
  const fields = el("sketch-legend-fields");
  fields.replaceChildren();
  sketchMeaningInputs.length = 0;
  for (const [index, color] of sketchWorkingPalette.entries()) {
    const row = document.createElement("div");
    row.className = "sketch-color-row";
    const swatch = document.createElement("button");
    swatch.type = "button";
    swatch.className = "sketch-swatch";
    swatch.style.background = color.hex;
    swatch.title = `Draw with ${color.name}`;
    swatch.setAttribute("aria-label", `Draw with ${color.name}`);
    swatch.addEventListener("click", () => setSketchColor(index));
    const meaning = document.createElement("input");
    meaning.type = "text";
    meaning.maxLength = 120;
    meaning.autocomplete = "off";
    meaning.placeholder = `${color.name} = …`;
    meaning.setAttribute("aria-label", `What the ${color.name} area means`);
    meaning.value = meanings?.[index] ?? "";
    meaning.addEventListener("focus", () => setSketchColor(index));
    sketchMeaningInputs.push(meaning);
    row.appendChild(swatch);
    row.appendChild(meaning);
    fields.appendChild(row);
  }
  if (sketchActiveColor >= sketchWorkingPalette.length) sketchActiveColor = 0;
  setSketchColor(sketchActiveColor);
}

function setSketchWorkingPalette(palette, { meanings } = {}) {
  if (!Array.isArray(palette) || palette.length < 1) {
    throw new Error("sketch palette must contain at least one color");
  }
  sketchWorkingPalette = cloneSketchPalette(palette);
  rebuildSketchLegendFields(meanings ?? sketchWorkingPalette.map(() => ""));
}

rebuildSketchLegendFields(SketchPalette.map(() => ""));

function setSketchColor(index) {
  if (!Number.isInteger(index) || index < 0 || index >= sketchWorkingPalette.length) return;
  sketchActiveColor = index;
  if (sketchActiveTool === "eraser") setSketchTool("brush");
  document.querySelectorAll("#sketch-legend-fields .sketch-swatch").forEach((swatch, i) => {
    swatch.classList.toggle("active", i === index);
  });
  updateSketchToolPresentation();
}

function setSketchTool(tool) {
  if (!Object.hasOwn(SketchToolNames, tool)) return;
  sketchActiveTool = tool;
  if (!SketchSelectionTools.has(tool) && !SketchSelectionModes.has(sketchSelectionOp)) {
    sketchSelectionOp = null;
  }
  if (!SketchSelectionTools.has(tool)) {
    // Choosing a paint/shape/blend tool keeps the selection for ops, but ends draw-select.
  }
  document.querySelectorAll("#sketch-tool-rack [data-sketch-tool]").forEach((button) => {
    const active = button.dataset.sketchTool === tool;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", String(active));
  });
  const isShape = SketchShapeTools.has(tool);
  const isLine = tool === "line";
  const isBlend = SketchBlendTools.has(tool);
  const usesTolerance = tool === "fill" || tool === "recolor";
  sketchShapeConfig.hidden = !(isShape || isLine);
  sketchEdgeStyleSelect.disabled = !(isShape || isLine);
  sketchFillRuleSelect.disabled = !isShape;
  el("sketch-fill-rule").closest("label").hidden = !isShape;
  sketchFillToleranceControl.hidden = !usesTolerance;
  sketchFillToleranceInput.disabled = !usesTolerance;
  sketchBlendStrengthControl.hidden = !isBlend;
  sketchBlendStrengthInput.disabled = !isBlend;
  updateSketchDitherApplyLabel();
  updateSketchSelectionOpButtons();
  updateSketchToolPresentation();
}

function sketchCurrentWidth() {
  return sketchActiveTool === "pencil" ? 3 : Number(sketchBrushInput.value);
}

function sketchCurrentColor() {
  return sketchActiveTool === "eraser" ? "#ffffff" : sketchWorkingPalette[sketchActiveColor].hex;
}

function sketchCurrentEdgeStyle() {
  const value = sketchEdgeStyleSelect.value;
  return SketchEdgeStyles.has(value) ? value : "hard";
}

function sketchCurrentFillRule() {
  const value = sketchFillRuleSelect.value;
  return SketchFillRules.has(value) ? value : "outline";
}

function updateSketchToolPresentation() {
  const width = sketchCurrentWidth();
  const color = sketchCurrentColor();
  let extra = "";
  if (sketchActiveTool === "fill") {
    extra = ` · tolerance ${sketchFillToleranceInput.value}`;
  } else if (sketchActiveTool === "recolor") {
    extra = ` · tolerance ${sketchFillToleranceInput.value} · ${sketchWorkingPalette[sketchActiveColor].name}`;
  } else if (sketchActiveTool === "eyedropper") {
    extra = " · click a pixel";
  } else if (SketchBlendTools.has(sketchActiveTool)) {
    extra = ` · ${width} px · strength ${sketchBlendStrengthInput.value}`;
  } else if (SketchShapeTools.has(sketchActiveTool) || sketchActiveTool === "line") {
    extra = sketchActiveTool === "line"
      ? ` · ${width} px · ${sketchCurrentEdgeStyle()}`
      : ` · ${width} px · ${sketchCurrentEdgeStyle()} · ${sketchCurrentFillRule()}`;
  } else if (SketchSelectionTools.has(sketchActiveTool)) {
    extra = sketchSelection ? " · selection active" : "";
  } else if (SketchHardStrokeTools.has(sketchActiveTool)
      || SketchPaintStampTools.has(sketchActiveTool)
      || sketchActiveTool === "line") {
    extra = ` · ${width} px`;
  }
  if (sketchSelectionOp) extra += ` · ${sketchSelectionOp}`;
  el("sketch-active-label").textContent = `${SketchToolNames[sketchActiveTool]}${extra}`;
  el("sketch-active-color").style.background = color;
  sketchCursor.style.background = color === "#ffffff" ? "#ffffffaa" : `${color}66`;
}

function invalidateSketchCanvasRect() {
  sketchCanvasRect = null;
}

function sketchCanvasClientRect() {
  if (!sketchCanvasRect) {
    sketchCanvasRect = sketchCanvas.getBoundingClientRect();
  }
  return sketchCanvasRect;
}

function updateSketchCursor(event) {
  const rect = sketchCanvasClientRect();
  const scale = rect.width / sketchCanvas.width;
  const targetCursor = sketchActiveTool === "fill"
    || SketchShapeTools.has(sketchActiveTool)
    || sketchActiveTool === "line"
    || SketchSelectionTools.has(sketchActiveTool)
    || SketchClickTools.has(sketchActiveTool)
    || !!sketchSelectionOp;
  const diameter = Math.max(5, Math.min(96, sketchCurrentWidth() * scale));
  const x = event.clientX - rect.left;
  const y = event.clientY - rect.top;
  sketchCursor.style.transform = `translate(${x}px, ${y}px) translate(-50%, -50%)`;
  if (sketchCursor.dataset.diameter !== String(diameter)) {
    sketchCursor.dataset.diameter = String(diameter);
    sketchCursor.style.width = `${diameter}px`;
    sketchCursor.style.height = `${diameter}px`;
  }
  sketchCursor.classList.toggle("target", targetCursor);
  sketchCursor.classList.add("visible");
}

function resetSketchActions() {
  sketchActions = [];
  sketchHistoryPast = [];
  sketchHistoryFuture = [];
  sketchGesture = null;
  clearSketchReplayCheckpoints();
  sketchBlank();
  renderSketchActionList();
}

function rememberSketchState() {
  sketchHistoryPast.push(snapshotSketchEditor());
  if (sketchHistoryPast.length > SketchHistoryDepth) sketchHistoryPast.shift();
  sketchHistoryFuture = [];
}

function snapshotSketchEditor() {
  return {
    actions: sketchActions.slice(),
    palette: cloneSketchPalette(sketchWorkingPalette),
    meanings: sketchMeaningInputs.map((input) => input.value),
    activeColor: sketchActiveColor,
  };
}

function restoreSketchEditorSnapshot(snapshot) {
  sketchActions = snapshot.actions;
  setSketchWorkingPalette(snapshot.palette, { meanings: snapshot.meanings });
  sketchActiveColor = Math.max(0, Math.min(snapshot.activeColor, sketchWorkingPalette.length - 1));
  setSketchColor(sketchActiveColor);
  renderSketchActions();
  renderSketchActionList();
  setSketchActionStatus();
}

function setSketchActionStatus(message = "", isError = false) {
  sketchStatus.textContent = message;
  sketchStatus.classList.toggle("error", isError);
}

function commitSketchAction(action, baseImage = null, keepPreview = false) {
  if (sketchActions.length >= SketchActionLimit) {
    setSketchActionStatus(
      `This sketch already has ${SketchActionLimit} actions. Remove or combine actions before drawing more.`,
      true);
    renderSketchActions();
    return false;
  }
  if (action.kind === "fill"
      && sketchActions.filter((candidate) => candidate.kind === "fill").length >= SketchFillActionLimit) {
    setSketchActionStatus(
      `This sketch already has ${SketchFillActionLimit} fill actions. Remove one before adding another.`,
      true);
    renderSketchActions();
    return false;
  }
  rememberSketchState();
  const recorded = {
    ...action,
    id: ++sketchActionSequence,
    visible: true,
  };
  sketchActions = [...sketchActions, recorded];
  if (!keepPreview) {
    if (baseImage
        && baseImage.width === sketchCanvas.width
        && baseImage.height === sketchCanvas.height) {
      sketchCtx.putImageData(baseImage, 0, 0);
    }
    applySketchAction(recorded);
  }
  syncSketchAspectSelectToCanvas();
  updateSketchResolutionReadout();
  drawSketchSelectionOverlay();
  recordSketchReplayAfterRender();
  renderSketchActionList();
  setSketchActionStatus();
  return true;
}

function mutateSketchActions(nextActions) {
  rememberSketchState();
  sketchActions = nextActions;
  renderSketchActions();
  renderSketchActionList();
  setSketchActionStatus();
}

function undoSketchActionChange() {
  const previous = sketchHistoryPast.pop();
  if (!previous) return;
  sketchHistoryFuture.push(snapshotSketchEditor());
  restoreSketchEditorSnapshot(previous);
}

function redoSketchActionChange() {
  const next = sketchHistoryFuture.pop();
  if (!next) return;
  sketchHistoryPast.push(snapshotSketchEditor());
  restoreSketchEditorSnapshot(next);
}

function sketchActionColorName(action) {
  if (action.kind === "base") return "";
  if (action.color === "#ffffff") return "erase";
  return sketchWorkingPalette.find((entry) => entry.hex === action.color)?.name
    || SketchPalette.find((entry) => entry.hex === action.color)?.name
    || action.color
    || "";
}

function sketchActionDescription(action) {
  if (action.kind === "base") return "restored sketch base";
  if (action.kind === "clear") return "clear canvas";
  if (action.kind === "paste") {
    return action.fit === "canvas"
      ? `paste image · match image (${action.imageData.width} × ${action.imageData.height})`
      : `paste image · ${action.fit || "cover"}`;
  }
  if (action.kind === "palette-filter") {
    const names = {
      nearest: "snap to palette",
      floyd: "Floyd–Steinberg dither",
      bayer: "ordered Bayer dither",
      group: "group similar colors",
    };
    const extra = action.mode === "group" ? ` · tolerance ${action.tolerance}` : "";
    return `${names[action.mode] || action.mode}${extra}`;
  }
  if (action.kind === "template-map") {
    return `template color map · ${action.palette?.length || 0} colors`;
  }
  if (action.kind === "recolor") {
    const toName = sketchWorkingPalette.find((entry) => entry.hex === action.color)?.name
      || action.color;
    return `recolor · ${toName} · tolerance ${action.tolerance}`;
  }
  if (action.kind === "canvas-rotate") {
    const turns = ((action.quarterTurns % 4) + 4) % 4;
    if (turns === 1) return "rotate canvas 90° clockwise";
    if (turns === 2) return "rotate canvas 180°";
    if (turns === 3) return "rotate canvas 90° counter-clockwise";
    return "rotate canvas";
  }
  if (action.kind === "canvas-flip") {
    return action.axis === "v" ? "flip canvas vertical" : "flip canvas horizontal";
  }
  if (action.kind === "canvas-scale") {
    return `scale canvas · ${action.width} × ${action.height}`;
  }
  if (action.kind === "crop-outside") return "crop to selection";
  if (action.kind === "crop-fill") {
    return action.fit === "canvas"
      ? "crop to canvas · match selection size"
      : `crop to canvas · ${action.fit || "cover"}`;
  }
  if (action.kind === "region-edit") {
    const parts = [];
    if (action.clearMask) parts.push("clear selection");
    if (action.stamp) parts.push(action.stampLabel || "stamp");
    return parts.join(" + ") || "region edit";
  }
  const color = sketchActionColorName(action);
  if (action.kind === "fill") {
    return `fill · ${color} · tolerance ${action.tolerance}`;
  }
  const width = `${action.width} px`;
  if (SketchBlendTools.has(action.kind)) {
    return `${SketchToolNames[action.kind]} · ${width} · strength ${action.strength}`;
  }
  if (SketchShapeTools.has(action.kind)) {
    return `${SketchToolNames[action.kind]} · ${color} · ${action.edgeStyle || "hard"} · ${action.fillRule || action.style || "outline"} · ${width}`;
  }
  if (action.kind === "line") {
    return `line · ${color} · ${action.edgeStyle || "hard"} · ${width}`;
  }
  return `${SketchToolNames[action.kind] || action.kind} · ${color} · ${width}`;
}

function sketchActionIndexById(id) {
  return sketchActions.findIndex((action) => action.id === id);
}

function moveSketchActionLayer(id, direction) {
  const index = sketchActionIndexById(id);
  const target = index + direction;
  if (index < 0 || target < 0 || target >= sketchActions.length) return;
  const next = sketchActions.slice();
  [next[index], next[target]] = [next[target], next[index]];
  mutateSketchActions(next);
}

function setSketchActionVisibility(id, visible) {
  const index = sketchActionIndexById(id);
  if (index < 0 || sketchActions[index].visible === visible) return;
  const next = sketchActions.slice();
  next[index] = { ...next[index], visible };
  mutateSketchActions(next);
}

function removeSketchActionById(id) {
  if (sketchActionIndexById(id) < 0) return;
  mutateSketchActions(sketchActions.filter((action) => action.id !== id));
}

function sketchActionSwatchBackground(action) {
  if (action.kind === "base" || action.kind === "region-edit"
      || action.kind === "paste" || action.kind === "palette-filter"
      || action.kind === "template-map"
      || action.kind === "canvas-rotate" || action.kind === "canvas-flip"
      || action.kind === "canvas-scale"
      || action.kind === "crop-outside" || action.kind === "crop-fill") {
    return "linear-gradient(135deg, #e53935, #1e88e5, #43a047)";
  }
  if (action.kind === "clear") return "#ffffff";
  if (action.kind === "recolor") return action.color || "#9aa3b5";
  if (SketchBlendTools.has(action.kind)) return "#9aa3b5";
  return action.color;
}

function buildSketchActionItem(action) {
  const item = document.createElement("li");
  item.className = "sketch-action";
  item.dataset.actionId = String(action.id);

  const visible = document.createElement("input");
  visible.type = "checkbox";
  visible.className = "sketch-action-visibility";
  visible.addEventListener("change", () => {
    setSketchActionVisibility(action.id, visible.checked);
  });

  const swatch = document.createElement("span");
  swatch.className = "sketch-action-swatch";

  const label = document.createElement("span");
  label.className = "sketch-action-label";

  const back = document.createElement("button");
  back.type = "button";
  back.textContent = "↑";
  back.title = "Move one layer toward the back";
  back.setAttribute("aria-label", back.title);
  back.dataset.sketchLayer = "back";
  back.addEventListener("click", () => moveSketchActionLayer(action.id, -1));

  const front = document.createElement("button");
  front.type = "button";
  front.textContent = "↓";
  front.title = "Move one layer toward the front";
  front.setAttribute("aria-label", front.title);
  front.dataset.sketchLayer = "front";
  front.addEventListener("click", () => moveSketchActionLayer(action.id, 1));

  const remove = document.createElement("button");
  remove.type = "button";
  remove.textContent = "×";
  remove.className = "sketch-action-remove";
  remove.title = "Remove this action";
  remove.setAttribute("aria-label", remove.title);
  remove.addEventListener("click", () => removeSketchActionById(action.id));

  item.append(visible, swatch, label, back, front, remove);
  return item;
}

function syncSketchActionListChrome() {
  const last = sketchActions.length - 1;
  for (let index = 0; index < sketchActions.length; index++) {
    const action = sketchActions[index];
    const item = sketchActionList.children[index];
    if (!item || Number(item.dataset.actionId) !== action.id) continue;
    item.classList.toggle("hidden-action", !action.visible);
    const checkbox = item.querySelector(".sketch-action-visibility");
    checkbox.checked = action.visible;
    checkbox.title = action.visible ? "Hide this action" : "Show this action";
    checkbox.setAttribute("aria-label", checkbox.title);
    const swatch = item.querySelector(".sketch-action-swatch");
    swatch.style.background = sketchActionSwatchBackground(action);
    const label = item.querySelector(".sketch-action-label");
    const text = `${index + 1}. ${sketchActionDescription(action)}`;
    if (label.textContent !== text) {
      label.textContent = text;
      label.title = text;
    }
    item.querySelector("[data-sketch-layer='back']").disabled = index === 0;
    item.querySelector("[data-sketch-layer='front']").disabled = index === last;
  }
  el("sketch-action-count").textContent = `${sketchActions.length}/${SketchActionLimit}`;
  el("sketch-undo").disabled = sketchHistoryPast.length === 0;
  el("sketch-redo").disabled = sketchHistoryFuture.length === 0;
}

function renderSketchActionList() {
  const byId = new Map();
  for (const item of sketchActionList.children) {
    byId.set(Number(item.dataset.actionId), item);
  }
  const next = document.createDocumentFragment();
  for (const action of sketchActions) {
    next.append(byId.get(action.id) || buildSketchActionItem(action));
  }
  sketchActionList.replaceChildren(next);
  syncSketchActionListChrome();
}

function drawSketchStroke(action, context = sketchCtx) {
  if (!action.points.length) return;
  if (SketchPaintStampTools.has(action.kind)) {
    drawSketchPaintStamp(action, context);
    return;
  }
  if (SketchBlendTools.has(action.kind)) {
    applySketchBlendStroke(action, context);
    return;
  }
  context.save();
  context.strokeStyle = action.color;
  context.lineWidth = action.width;
  context.lineCap = "round";
  context.lineJoin = "round";
  if (action.edgeStyle === "dashed") context.setLineDash([Math.max(4, action.width * 2), Math.max(3, action.width)]);
  if (action.edgeStyle === "soft") {
    context.shadowColor = action.color;
    context.shadowBlur = Math.max(2, action.width * 0.7);
    context.globalAlpha = 0.85;
  }
  context.beginPath();
  context.moveTo(action.points[0].x, action.points[0].y);
  if (action.points.length === 1) {
    context.lineTo(action.points[0].x, action.points[0].y);
  } else {
    for (let i = 1; i < action.points.length; i++) {
      context.lineTo(action.points[i].x, action.points[i].y);
    }
  }
  context.stroke();
  context.restore();
}

function sketchSeededRandom(seed) {
  let value = (seed >>> 0) || 1;
  return () => {
    value = (value * 1664525 + 1013904223) >>> 0;
    return value / 0x100000000;
  };
}

function drawSketchPaintStamp(action, context = sketchCtx) {
  const points = action.points;
  if (!points.length) return;
  context.save();
  const spacing = Math.max(1, Math.floor(action.width * (action.kind === "spray" ? 0.28 : 0.22)));
  const start = action.stampFrom || 0;
  let lastX = action.stampLastX ?? -1e9;
  let lastY = action.stampLastY ?? -1e9;
  let visitedCount = action.stampVisitedCount || 0;
  const visited = [];
  for (let i = start; i < points.length; i++) {
    const point = points[i];
    if ((point.x - lastX) * (point.x - lastX) + (point.y - lastY) * (point.y - lastY) < spacing * spacing) {
      continue;
    }
    lastX = point.x;
    lastY = point.y;
    visited.push({ point, stampIndex: visitedCount });
    visitedCount++;
  }
  if (action.kind === "soft-brush") {
    const rgb = sketchHexRgb(action.color);
    for (const entry of visited) {
      const point = entry.point;
      const radius = Math.max(1, action.width / 2);
      const gradient = context.createRadialGradient(point.x, point.y, 0, point.x, point.y, radius);
      gradient.addColorStop(0, `rgba(${rgb[0]},${rgb[1]},${rgb[2]},0.55)`);
      gradient.addColorStop(0.55, `rgba(${rgb[0]},${rgb[1]},${rgb[2]},0.22)`);
      gradient.addColorStop(1, `rgba(${rgb[0]},${rgb[1]},${rgb[2]},0)`);
      context.fillStyle = gradient;
      context.beginPath();
      context.arc(point.x, point.y, radius, 0, Math.PI * 2);
      context.fill();
    }
  } else {
    // spray — deterministic dots from stroke geometry so replay matches preview
    const radius = Math.max(2, action.width / 2);
    const rgb = sketchHexRgb(action.color);
    for (const entry of visited) {
      const point = entry.point;
      const rand = sketchSeededRandom(
        (Math.round(point.x) * 73856093)
        ^ (Math.round(point.y) * 19349663)
        ^ (entry.stampIndex * 83492791)
        ^ Math.round(action.width * 100));
      const dots = Math.max(4, Math.round(radius * 0.9));
      for (let d = 0; d < dots; d++) {
        const angle = rand() * Math.PI * 2;
        const dist = Math.sqrt(rand()) * radius;
        const x = point.x + Math.cos(angle) * dist;
        const y = point.y + Math.sin(angle) * dist;
        const alpha = 0.25 + rand() * 0.55;
        context.fillStyle = `rgba(${rgb[0]},${rgb[1]},${rgb[2]},${alpha.toFixed(3)})`;
        context.fillRect(Math.round(x), Math.round(y), 1 + (rand() > 0.7 ? 1 : 0), 1);
      }
    }
  }
  context.restore();
  if (action.stampState) {
    action.stampState.lastX = lastX;
    action.stampState.lastY = lastY;
    action.stampState.nextIndex = points.length;
    action.stampState.visitedCount = visitedCount;
  }
}

function applySketchBlendStroke(action, context = sketchCtx) {
  if (!action.points.length) return;
  const width = sketchCanvas.width;
  const height = sketchCanvas.height;
  const state = action.blendState;
  const image = state?.image || context.getImageData(0, 0, width, height);
  const source = state?.source || new Uint8ClampedArray(image.data);
  const data = image.data;
  const radius = Math.max(1, Math.round(action.width / 2));
  const strength = Math.max(0.05, Math.min(1, (action.strength || 45) / 100));
  const step = Math.max(1, Math.floor(radius * 0.35));
  let lastX = state?.lastX ?? -1e9;
  let lastY = state?.lastY ?? -1e9;
  const start = state?.nextIndex || 0;
  let dirty = null;
  for (let i = start; i < action.points.length; i++) {
    const point = action.points[i];
    if ((point.x - lastX) * (point.x - lastX) + (point.y - lastY) * (point.y - lastY) < step * step) {
      continue;
    }
    lastX = point.x;
    lastY = point.y;
    applySketchBlendStamp(source, data, width, height, point.x, point.y, radius, strength, action.kind);
    const minX = Math.max(0, Math.floor(point.x - radius));
    const minY = Math.max(0, Math.floor(point.y - radius));
    const maxX = Math.min(width, Math.ceil(point.x + radius + 1));
    const maxY = Math.min(height, Math.ceil(point.y + radius + 1));
    if (!dirty) {
      dirty = { minX, minY, maxX, maxY };
    } else {
      dirty.minX = Math.min(dirty.minX, minX);
      dirty.minY = Math.min(dirty.minY, minY);
      dirty.maxX = Math.max(dirty.maxX, maxX);
      dirty.maxY = Math.max(dirty.maxY, maxY);
    }
  }
  if (state) {
    state.lastX = lastX;
    state.lastY = lastY;
    state.nextIndex = action.points.length;
    if (dirty && dirty.maxX > dirty.minX && dirty.maxY > dirty.minY) {
      context.putImageData(
        image, 0, 0, dirty.minX, dirty.minY, dirty.maxX - dirty.minX, dirty.maxY - dirty.minY);
    }
    return;
  }
  context.putImageData(image, 0, 0);
}

function applySketchBlendStamp(source, data, width, height, cx, cy, radius, strength, kind) {
  const minX = Math.max(0, Math.floor(cx - radius));
  const maxX = Math.min(width - 1, Math.ceil(cx + radius));
  const minY = Math.max(0, Math.floor(cy - radius));
  const maxY = Math.min(height - 1, Math.ceil(cy + radius));
  const radius2 = radius * radius;
  for (let y = minY; y <= maxY; y++) {
    for (let x = minX; x <= maxX; x++) {
      const dx = x - cx;
      const dy = y - cy;
      const dist2 = dx * dx + dy * dy;
      if (dist2 > radius2) continue;
      const falloff = (1 - Math.sqrt(dist2) / radius) * strength;
      if (falloff <= 0.001) continue;
      const offset = (y * width + x) * 4;
      if (kind === "lighten") {
        const luminance = 0.299 * source[offset] + 0.587 * source[offset + 1] + 0.114 * source[offset + 2];
        // Dense near-black/mid inks lift toward white; already-light pixels move less.
        const density = 1 - (luminance / 255);
        const amount = falloff * (0.35 + density * 0.65);
        data[offset] = Math.round(source[offset] + (255 - source[offset]) * amount);
        data[offset + 1] = Math.round(source[offset + 1] + (255 - source[offset + 1]) * amount);
        data[offset + 2] = Math.round(source[offset + 2] + (255 - source[offset + 2]) * amount);
        continue;
      }
      if (kind === "spread") {
        const push = radius * 0.45;
        const len = Math.sqrt(dist2) || 1;
        const sx = Math.max(0, Math.min(width - 1, Math.round(x - (dx / len) * push)));
        const sy = Math.max(0, Math.min(height - 1, Math.round(y - (dy / len) * push)));
        const sample = (sy * width + sx) * 4;
        data[offset] = Math.round(source[offset] + (source[sample] - source[offset]) * falloff);
        data[offset + 1] = Math.round(source[offset + 1] + (source[sample + 1] - source[offset + 1]) * falloff);
        data[offset + 2] = Math.round(source[offset + 2] + (source[sample + 2] - source[offset + 2]) * falloff);
        continue;
      }
      // mix + fuzz sample a small neighborhood
      let r = 0;
      let g = 0;
      let b = 0;
      let count = 0;
      const sampleRadius = kind === "fuzz" ? Math.max(1, Math.round(radius * 0.55)) : Math.max(1, Math.round(radius * 0.35));
      for (let sy = y - sampleRadius; sy <= y + sampleRadius; sy++) {
        if (sy < 0 || sy >= height) continue;
        for (let sx = x - sampleRadius; sx <= x + sampleRadius; sx++) {
          if (sx < 0 || sx >= width) continue;
          const sample = (sy * width + sx) * 4;
          r += source[sample];
          g += source[sample + 1];
          b += source[sample + 2];
          count++;
        }
      }
      if (!count) continue;
      r /= count;
      g /= count;
      b /= count;
      const amount = kind === "fuzz" ? falloff * 0.9 : falloff;
      data[offset] = Math.round(source[offset] + (r - source[offset]) * amount);
      data[offset + 1] = Math.round(source[offset + 1] + (g - source[offset + 1]) * amount);
      data[offset + 2] = Math.round(source[offset + 2] + (b - source[offset + 2]) * amount);
    }
  }
}

function regularSketchPolygon(start, end, sides, rotation = -Math.PI / 2) {
  const cx = (start.x + end.x) / 2;
  const cy = (start.y + end.y) / 2;
  const rx = Math.abs(end.x - start.x) / 2;
  const ry = Math.abs(end.y - start.y) / 2;
  return Array.from({ length: sides }, (_, index) => {
    const angle = rotation + (index * Math.PI * 2 / sides);
    return { x: cx + Math.cos(angle) * rx, y: cy + Math.sin(angle) * ry };
  });
}

function starSketchPolygon(start, end) {
  const cx = (start.x + end.x) / 2;
  const cy = (start.y + end.y) / 2;
  const rx = Math.abs(end.x - start.x) / 2;
  const ry = Math.abs(end.y - start.y) / 2;
  return Array.from({ length: 10 }, (_, index) => {
    const outer = index % 2 === 0;
    const angle = -Math.PI / 2 + (index * Math.PI / 5);
    return {
      x: cx + Math.cos(angle) * rx * (outer ? 1 : 0.45),
      y: cy + Math.sin(angle) * ry * (outer ? 1 : 0.45),
    };
  });
}

function drawSketchShape(action, context = sketchCtx) {
  const left = Math.min(action.start.x, action.end.x);
  const top = Math.min(action.start.y, action.end.y);
  const width = Math.abs(action.end.x - action.start.x);
  const height = Math.abs(action.end.y - action.start.y);
  const edgeStyle = action.edgeStyle || "hard";
  const fillRule = action.fillRule || action.style || "outline";
  context.save();
  context.strokeStyle = action.color;
  context.fillStyle = action.color;
  context.lineWidth = action.width;
  context.lineCap = "round";
  context.lineJoin = "round";
  if (edgeStyle === "dashed") {
    context.setLineDash([Math.max(4, action.width * 2.2), Math.max(3, action.width * 1.2)]);
  }
  if (edgeStyle === "soft") {
    context.shadowColor = action.color;
    context.shadowBlur = Math.max(3, action.width * 1.1);
  }
  context.beginPath();
  if (action.kind === "line") {
    context.moveTo(action.start.x, action.start.y);
    context.lineTo(action.end.x, action.end.y);
  } else if (action.kind === "rectangle") {
    context.rect(left, top, width, height);
  } else if (action.kind === "rounded-rectangle") {
    const radius = Math.min(width, height) * 0.18;
    context.moveTo(left + radius, top);
    context.lineTo(left + width - radius, top);
    context.arcTo(left + width, top, left + width, top + radius, radius);
    context.lineTo(left + width, top + height - radius);
    context.arcTo(left + width, top + height, left + width - radius, top + height, radius);
    context.lineTo(left + radius, top + height);
    context.arcTo(left, top + height, left, top + height - radius, radius);
    context.lineTo(left, top + radius);
    context.arcTo(left, top, left + radius, top, radius);
    context.closePath();
  } else if (action.kind === "ellipse") {
    context.ellipse(left + width / 2, top + height / 2, width / 2, height / 2, 0, 0, Math.PI * 2);
  } else {
    const sides = { triangle: 3, diamond: 4, pentagon: 5, hexagon: 6, octagon: 8 }[action.kind];
    const points = action.kind === "star"
      ? starSketchPolygon(action.start, action.end)
      : regularSketchPolygon(action.start, action.end, sides);
    if (points.length) {
      context.moveTo(points[0].x, points[0].y);
      for (let i = 1; i < points.length; i++) context.lineTo(points[i].x, points[i].y);
      context.closePath();
    }
  }
  if (action.kind === "line") {
    context.stroke();
  } else if (fillRule === "solid") {
    context.fill();
    if (edgeStyle !== "hard") context.stroke();
  } else if (fillRule === "soft") {
    context.save();
    context.globalAlpha = 0.45;
    context.shadowBlur = 0;
    context.fill();
    context.restore();
    context.stroke();
  } else {
    context.stroke();
  }
  context.restore();
}

function sketchHexRgb(hex) {
  return [
    parseInt(hex.slice(1, 3), 16),
    parseInt(hex.slice(3, 5), 16),
    parseInt(hex.slice(5, 7), 16),
  ];
}

function applySketchFloodFill(action) {
  const width = sketchCanvas.width;
  const height = sketchCanvas.height;
  const x = Math.max(0, Math.min(width - 1, Math.round(action.x)));
  const y = Math.max(0, Math.min(height - 1, Math.round(action.y)));
  const image = sketchCtx.getImageData(0, 0, width, height);
  const data = image.data;
  const startOffset = (y * width + x) * 4;
  const target = [data[startOffset], data[startOffset + 1], data[startOffset + 2]];
  const fill = sketchHexRgb(action.color);
  if (target.every((channel, index) => channel === fill[index])) return;

  const total = width * height;
  if (sketchFillVisited.length !== total) {
    sketchFillVisited = new Uint32Array(total);
    sketchFillQueue = new Int32Array(total);
    sketchFillVisitGeneration = 0;
  }
  sketchFillVisitGeneration++;
  if (sketchFillVisitGeneration === 0xffffffff) {
    sketchFillVisited.fill(0);
    sketchFillVisitGeneration = 1;
  }
  const visitGeneration = sketchFillVisitGeneration;
  let read = 0;
  let write = 1;
  const startIndex = y * width + x;
  sketchFillQueue[0] = startIndex;
  sketchFillVisited[startIndex] = visitGeneration;
  const enqueue = (neighbor) => {
    if (sketchFillVisited[neighbor] !== visitGeneration) {
      sketchFillVisited[neighbor] = visitGeneration;
      sketchFillQueue[write++] = neighbor;
    }
  };

  while (read < write) {
    const pixelIndex = sketchFillQueue[read++];
    const offset = pixelIndex * 4;
    if (Math.abs(data[offset] - target[0]) > action.tolerance
        || Math.abs(data[offset + 1] - target[1]) > action.tolerance
        || Math.abs(data[offset + 2] - target[2]) > action.tolerance) {
      continue;
    }
    data[offset] = fill[0];
    data[offset + 1] = fill[1];
    data[offset + 2] = fill[2];
    data[offset + 3] = 255;

    const px = pixelIndex % width;
    const py = Math.floor(pixelIndex / width);
    if (px > 0) enqueue(pixelIndex - 1);
    if (px + 1 < width) enqueue(pixelIndex + 1);
    if (py > 0) enqueue(pixelIndex - width);
    if (py + 1 < height) enqueue(pixelIndex + width);
  }
  sketchCtx.putImageData(image, 0, 0);
}

function visibleSketchActionIds(actions = sketchActions) {
  const ids = [];
  for (const action of actions) {
    if (action.visible) ids.push(action.id);
  }
  return ids;
}

function sketchActionIdsEqual(left, right) {
  if (left.length !== right.length) return false;
  for (let i = 0; i < left.length; i++) {
    if (left[i] !== right[i]) return false;
  }
  return true;
}

function sketchActionIdsArePrefix(prefix, ids) {
  if (prefix.length > ids.length) return false;
  for (let i = 0; i < prefix.length; i++) {
    if (prefix[i] !== ids[i]) return false;
  }
  return true;
}

function clearSketchReplayCheckpoints() {
  sketchReplayCheckpoints = [];
}

function sketchReplayMaxCheckpoints() {
  const bytes = Math.max(1, sketchCanvas.width * sketchCanvas.height * 4);
  return Math.max(1, Math.min(6, Math.floor(SketchReplayMaxBytes / bytes)));
}

function pruneSketchReplayCheckpoints() {
  const budget = sketchReplayMaxCheckpoints();
  if (sketchReplayCheckpoints.length <= budget) return;
  const lastIndex = sketchReplayCheckpoints.length - 1;
  const keep = new Set([lastIndex]);
  if (lastIndex > 0) keep.add(lastIndex - 1);
  if (lastIndex > 1) keep.add(lastIndex - 2);
  for (let i = 0; i < sketchReplayCheckpoints.length; i++) {
    const count = sketchReplayCheckpoints[i].ids.length;
    if (count === 0 || count % SketchReplayCheckpointGap === 0) keep.add(i);
  }
  const kept = [];
  for (const index of [...keep].sort((a, b) => a - b)) {
    kept.push(sketchReplayCheckpoints[index]);
  }
  if (kept.length > budget) {
    const tip = kept.slice(-Math.min(3, budget));
    const marks = kept.filter((checkpoint) => !tip.includes(checkpoint));
    const markSlots = budget - tip.length;
    sketchReplayCheckpoints = markSlots > 0
      ? [...marks.slice(-markSlots), ...tip]
      : tip;
    return;
  }
  sketchReplayCheckpoints = kept;
}

function findSketchReplayCheckpoint(ids) {
  let best = null;
  for (const checkpoint of sketchReplayCheckpoints) {
    if (!sketchActionIdsArePrefix(checkpoint.ids, ids)) continue;
    if (!best || checkpoint.ids.length > best.ids.length) best = checkpoint;
  }
  return best;
}

function recordSketchReplayAfterRender() {
  const ids = visibleSketchActionIds();
  const last = sketchReplayCheckpoints[sketchReplayCheckpoints.length - 1];
  if (last
      && sketchActionIdsEqual(last.ids, ids)
      && last.image.width === sketchCanvas.width
      && last.image.height === sketchCanvas.height) {
    return;
  }
  sketchReplayCheckpoints.push({
    ids: ids.slice(),
    image: sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height),
  });
  pruneSketchReplayCheckpoints();
}

function applySketchAction(action) {
  if (action.kind === "base") {
    resizeSketchCanvasToSize(action.imageData.width, action.imageData.height);
    sketchCtx.putImageData(action.imageData, 0, 0);
  } else if (action.kind === "clear") {
    sketchBlank();
  } else if (action.kind === "fill") {
    applySketchFloodFill(action);
  } else if (action.kind === "paste") {
    applySketchPaste(action);
  } else if (action.kind === "palette-filter") {
    applySketchPaletteFilter(action);
  } else if (action.kind === "template-map") {
    if (!Array.isArray(action.palette) || action.palette.length < 1) {
      throw new Error("template color map action is missing its palette");
    }
    applySketchPaletteFilter({
      mode: "nearest",
      includeWhite: action.includeWhite !== false,
      palette: action.palette,
      mask: action.mask || null,
    });
  } else if (action.kind === "recolor") {
    applySketchRecolor(action);
  } else if (action.kind === "canvas-rotate") {
    applySketchCanvasRotate(action.quarterTurns);
  } else if (action.kind === "canvas-flip") {
    applySketchCanvasFlip(action.axis);
  } else if (action.kind === "canvas-scale") {
    applySketchCanvasScale(action.width, action.height);
  } else if (action.kind === "crop-outside") {
    applySketchCropOutside(action);
  } else if (action.kind === "crop-fill") {
    applySketchCropFill(action);
  } else if (action.kind === "region-edit") {
    applySketchRegionEdit(action);
  } else if (action.kind === "line" || SketchShapeTools.has(action.kind)) {
    drawSketchShape(action);
  } else {
    drawSketchStroke(action);
  }
}

function renderSketchActions() {
  const visible = [];
  for (const action of sketchActions) {
    if (action.visible) visible.push(action);
  }
  const ids = visible.map((action) => action.id);
  const checkpoint = findSketchReplayCheckpoint(ids);
  if (checkpoint && checkpoint.ids.length === ids.length) {
    resizeSketchCanvasToSize(checkpoint.image.width, checkpoint.image.height);
    sketchCtx.putImageData(checkpoint.image, 0, 0);
  } else if (checkpoint && checkpoint.ids.length > 0) {
    resizeSketchCanvasToSize(checkpoint.image.width, checkpoint.image.height);
    sketchCtx.putImageData(checkpoint.image, 0, 0);
    for (let i = checkpoint.ids.length; i < visible.length; i++) {
      applySketchAction(visible[i]);
    }
  } else {
    const base = visible.find((action) => action.kind === "base");
    if (base?.imageData) {
      resizeSketchCanvasToSize(base.imageData.width, base.imageData.height);
    } else {
      resizeSketchCanvasToAspect(sketchAspect);
    }
    sketchBlank();
    for (const action of visible) {
      applySketchAction(action);
    }
  }
  syncSketchAspectSelectToCanvas();
  updateSketchResolutionReadout();
  drawSketchSelectionOverlay();
  recordSketchReplayAfterRender();
}

function applySketchRegionEdit(action) {
  if (action.clearMask) {
    eraseSketchMask(action.clearMask);
  }
  if (action.stamp?.imageData) {
    stampSketchImageData(action.stamp.x, action.stamp.y, action.stamp.imageData);
  }
}

function sketchScratchForSize(width, height) {
  if (!sketchScratchCanvas) {
    sketchScratchCanvas = document.createElement("canvas");
    sketchScratchCtx = sketchScratchCanvas.getContext("2d");
  }
  if (sketchScratchCanvas.width !== width || sketchScratchCanvas.height !== height) {
    sketchScratchCanvas.width = width;
    sketchScratchCanvas.height = height;
  }
  return sketchScratchCtx;
}

function stampSketchImageData(x, y, imageData) {
  const ctx = sketchScratchForSize(imageData.width, imageData.height);
  ctx.putImageData(imageData, 0, 0);
  sketchCtx.drawImage(ctx.canvas, Math.round(x), Math.round(y));
}

function canvasFromSketchImageData(imageData) {
  const canvas = document.createElement("canvas");
  canvas.width = imageData.width;
  canvas.height = imageData.height;
  canvas.getContext("2d").putImageData(imageData, 0, 0);
  return canvas;
}

function clampSketchDirtyRect(rect, width, height) {
  const x = Math.max(0, Math.floor(rect.x));
  const y = Math.max(0, Math.floor(rect.y));
  const right = Math.min(width, Math.ceil(rect.x + rect.w));
  const bottom = Math.min(height, Math.ceil(rect.y + rect.h));
  if (right <= x || bottom <= y) return null;
  return { x, y, w: right - x, h: bottom - y };
}

function restoreSketchDirtyRect(base, rect) {
  const clipped = clampSketchDirtyRect(rect, base.width, base.height);
  if (!clipped) return;
  sketchCtx.putImageData(base, 0, 0, clipped.x, clipped.y, clipped.w, clipped.h);
}

function sketchBoundsFromPoints(points) {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const point of points) {
    minX = Math.min(minX, point.x);
    minY = Math.min(minY, point.y);
    maxX = Math.max(maxX, point.x);
    maxY = Math.max(maxY, point.y);
  }
  return {
    x: minX,
    y: minY,
    w: Math.max(0, maxX - minX),
    h: Math.max(0, maxY - minY),
  };
}

function sketchPointInPolygon(point, points) {
  let inside = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const xi = points[i].x;
    const yi = points[i].y;
    const xj = points[j].x;
    const yj = points[j].y;
    const intersect = ((yi > point.y) !== (yj > point.y))
      && (point.x < ((xj - xi) * (point.y - yi) / ((yj - yi) || 1e-9) + xi));
    if (intersect) inside = !inside;
  }
  return inside;
}

function sketchSelectionContains(selection, point) {
  if (!selection) return false;
  if (selection.type === "rect") {
    const b = selection.bounds;
    return point.x >= b.x && point.x <= b.x + b.w && point.y >= b.y && point.y <= b.y + b.h;
  }
  return sketchPointInPolygon(point, selection.points);
}

function cloneSketchMask(mask) {
  if (!mask) return null;
  if (mask.type === "rect") {
    return { type: "rect", bounds: { ...mask.bounds } };
  }
  return {
    type: "poly",
    points: mask.points.map((point) => ({ x: point.x, y: point.y })),
    bounds: { ...mask.bounds },
  };
}

function selectionToMask(selection) {
  if (!selection) return null;
  if (selection.type === "rect") {
    return { type: "rect", bounds: { ...selection.bounds } };
  }
  return {
    type: "poly",
    points: selection.points.map((point) => ({ x: point.x, y: point.y })),
    bounds: { ...selection.bounds },
  };
}

function eraseSketchMask(mask) {
  sketchCtx.save();
  sketchCtx.fillStyle = "#ffffff";
  sketchCtx.beginPath();
  if (mask.type === "rect") {
    const b = mask.bounds;
    sketchCtx.rect(b.x, b.y, b.w, b.h);
  } else {
    const points = mask.points;
    if (!points.length) {
      sketchCtx.restore();
      return;
    }
    sketchCtx.moveTo(points[0].x, points[0].y);
    for (let i = 1; i < points.length; i++) sketchCtx.lineTo(points[i].x, points[i].y);
    sketchCtx.closePath();
  }
  sketchCtx.fill();
  sketchCtx.restore();
}

function extractSketchSelectionBitmap(selection) {
  const bounds = selection.bounds;
  const x = Math.max(0, Math.floor(bounds.x));
  const y = Math.max(0, Math.floor(bounds.y));
  const w = Math.max(1, Math.min(sketchCanvas.width - x, Math.ceil(bounds.w)));
  const h = Math.max(1, Math.min(sketchCanvas.height - y, Math.ceil(bounds.h)));
  const imageData = sketchCtx.getImageData(x, y, w, h);
  if (selection.type === "poly") {
    const data = imageData.data;
    for (let py = 0; py < h; py++) {
      for (let px = 0; px < w; px++) {
        if (!sketchPointInPolygon({ x: x + px + 0.5, y: y + py + 0.5 }, selection.points)) {
          const offset = (py * w + px) * 4;
          data[offset] = 0;
          data[offset + 1] = 0;
          data[offset + 2] = 0;
          data[offset + 3] = 0;
        }
      }
    }
  }
  return { x, y, imageData };
}

function clipboardImageFile(clipboardData) {
  if (!clipboardData) return null;
  if (clipboardData.files) {
    for (const file of clipboardData.files) {
      if (file.type.startsWith("image/")) return file;
    }
  }
  if (clipboardData.items) {
    for (const item of clipboardData.items) {
      if (item.type.startsWith("image/")) return item.getAsFile();
    }
  }
  return null;
}

function cloneSketchImageData(imageData) {
  return new ImageData(new Uint8ClampedArray(imageData.data), imageData.width, imageData.height);
}

function sketchAspectNameFromSize(width, height) {
  return SketchAspectBySize.get(`${width}x${height}`) || "";
}

function resizeSketchCanvasToAspect(aspect) {
  const size = SketchCanvasSizes[aspect];
  if (!size) throw new Error(`unknown sketch aspect ${aspect}`);
  resizeSketchCanvasToSize(size[0], size[1]);
}

function resizeSketchCanvasToSize(width, height) {
  if (!Number.isInteger(width) || !Number.isInteger(height)
      || width < SketchScaleMinEdge || height < SketchScaleMinEdge) {
    throw new Error(`invalid sketch size ${width}×${height}`);
  }
  if (sketchCanvas.width !== width || sketchCanvas.height !== height) {
    sketchCanvas.width = width;
    sketchCanvas.height = height;
    invalidateSketchCanvasRect();
  }
  if (sketchOverlay.width !== width || sketchOverlay.height !== height) {
    sketchOverlay.width = width;
    sketchOverlay.height = height;
  }
  updateSketchResolutionReadout();
  fitSketchCanvasDisplay();
}

function updateSketchResolutionReadout() {
  const readout = el("sketch-resolution");
  if (!readout) return;
  readout.textContent = `${sketchCanvas.width} × ${sketchCanvas.height}`;
}

function sketchScaleTargetSize(width, height, factor) {
  if (factor === 0.5) {
    return {
      width: Math.max(SketchScaleMinEdge, Math.floor(width / 2)),
      height: Math.max(SketchScaleMinEdge, Math.floor(height / 2)),
    };
  }
  const exactW = width * 2;
  const exactH = height * 2;
  if (exactW <= SketchScaleMaxEdge
      && exactH <= SketchScaleMaxEdge
      && exactW * exactH <= SketchScaleMaxPixels) {
    return { width: exactW, height: exactH };
  }
  const scale = Math.min(
    2,
    SketchScaleMaxEdge / width,
    SketchScaleMaxEdge / height,
    Math.sqrt(SketchScaleMaxPixels / (width * height)));
  const nextW = Math.max(width, Math.min(SketchScaleMaxEdge, Math.round(width * scale)));
  const nextH = Math.max(height, Math.min(SketchScaleMaxEdge, Math.round(height * scale)));
  return { width: nextW, height: nextH };
}

function scaleSketchImageDataNearest(src, destW, destH) {
  const dst = new ImageData(destW, destH);
  const s = src.data;
  const d = dst.data;
  const srcW = src.width;
  const srcH = src.height;
  for (let y = 0; y < destH; y++) {
    const sy = Math.min(srcH - 1, Math.floor((y * srcH) / destH));
    for (let x = 0; x < destW; x++) {
      const sx = Math.min(srcW - 1, Math.floor((x * srcW) / destW));
      const si = (sy * srcW + sx) * 4;
      const di = (y * destW + x) * 4;
      d[di] = s[si];
      d[di + 1] = s[si + 1];
      d[di + 2] = s[si + 2];
      d[di + 3] = s[si + 3];
    }
  }
  return dst;
}

function sketchAspectRatioClose(a, b) {
  return Math.abs(Math.log(a / b)) < 0.01;
}

// Published shape whose ratio is nearest the given size, by log-ratio
// distance. Used for the attach record when the canvas size is unpublished.
function sketchNearestPublishedAspect(width, height) {
  const ratio = width / height;
  let bestName = "square";
  let bestDelta = Infinity;
  for (const [name, [w, h]] of Object.entries(SketchCanvasSizes)) {
    const delta = Math.abs(Math.log(ratio / (w / h)));
    if (delta < bestDelta) {
      bestDelta = delta;
      bestName = name;
    }
  }
  return bestName;
}

function sketchGcd(a, b) {
  while (b) [a, b] = [b, a % b];
  return a;
}

function sketchAspectCustomLabel(width, height) {
  const divisor = sketchGcd(width, height);
  const rw = width / divisor;
  const rh = height / divisor;
  if (rw <= 99 && rh <= 99) return `custom ${rw}:${rh}`;
  return `custom ${(width / height).toFixed(2)}:1`;
}

// Keeps the shape picker truthful without ever mutating sketchAspect (which
// anchors deterministic action replay). Exact published sizes select their
// name; a matching published RATIO (raw halve/double, restored sketches)
// keeps that name; anything else shows a disabled custom-ratio readout.
function syncSketchAspectSelectToCanvas() {
  const width = sketchCanvas.width;
  const height = sketchCanvas.height;
  const customOption = sketchAspectSelect.querySelector('option[value="custom"]');
  const exact = sketchAspectNameFromSize(width, height);
  let name = exact;
  if (!name) {
    const ratio = width / height;
    for (const [candidate, [w, h]] of Object.entries(SketchCanvasSizes)) {
      if (sketchAspectRatioClose(ratio, w / h)) {
        name = candidate;
        break;
      }
    }
  }
  if (name) {
    customOption?.remove();
    if (sketchAspectSelect.value !== name) sketchAspectSelect.value = name;
    return;
  }
  const label = sketchAspectCustomLabel(width, height);
  if (customOption) {
    customOption.textContent = label;
  } else {
    const option = document.createElement("option");
    option.value = "custom";
    option.disabled = true;
    option.textContent = label;
    sketchAspectSelect.appendChild(option);
  }
  sketchAspectSelect.value = "custom";
}

function sketchCurrentPasteFit() {
  const value = sketchPasteFitSelect.value;
  return SketchPasteFitModes.has(value) ? value : "canvas";
}

// Canvas size for a "match image" paste: the source's exact aspect ratio at
// its own resolution, upscaled so the long edge reaches
// SketchMatchImageMinLongEdge, and clamped inside the raw-scale caps.
function sketchCanvasSizeForImage(width, height) {
  let scale = Math.max(1, SketchMatchImageMinLongEdge / Math.max(width, height));
  scale = Math.min(
    scale,
    SketchScaleMaxEdge / width,
    SketchScaleMaxEdge / height,
    Math.sqrt(SketchScaleMaxPixels / (width * height)));
  return {
    width: Math.min(SketchScaleMaxEdge, Math.max(SketchScaleMinEdge, Math.round(width * scale))),
    height: Math.min(SketchScaleMaxEdge, Math.max(SketchScaleMinEdge, Math.round(height * scale))),
  };
}

function sketchCurrentDitherMethod() {
  const value = sketchDitherMethodSelect.value;
  return SketchDitherMethods.has(value) ? value : "floyd";
}

function updateSketchDitherApplyLabel() {
  const method = sketchCurrentDitherMethod();
  const target = sketchSelection ? "selection" : "canvas";
  const labels = {
    nearest: `snap ${target} to palette`,
    floyd: `dither ${target}`,
    bayer: `dither ${target}`,
    group: `group ${target} colors`,
  };
  sketchDitherApplyButton.textContent = labels[method] || `apply to ${target}`;
  const usesTolerance = sketchActiveTool === "fill"
    || sketchActiveTool === "recolor"
    || method === "group";
  sketchFillToleranceControl.hidden = !usesTolerance;
  sketchFillToleranceInput.disabled = !usesTolerance;
}

function sketchFitRect(srcW, srcH, canvasW, canvasH, fit) {
  if (fit === "stretch") {
    return { dx: 0, dy: 0, dw: canvasW, dh: canvasH };
  }
  const scale = fit === "contain"
    ? Math.min(canvasW / srcW, canvasH / srcH)
    : Math.max(canvasW / srcW, canvasH / srcH);
  const dw = Math.max(1, Math.round(srcW * scale));
  const dh = Math.max(1, Math.round(srcH * scale));
  return {
    dx: Math.round((canvasW - dw) / 2),
    dy: Math.round((canvasH - dh) / 2),
    dw,
    dh,
  };
}

function rasterizeSketchBitmap(bitmap, dw, dh) {
  const layer = document.createElement("canvas");
  layer.width = dw;
  layer.height = dh;
  const ctx = layer.getContext("2d");
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = "high";
  ctx.drawImage(bitmap, 0, 0, dw, dh);
  return ctx.getImageData(0, 0, dw, dh);
}

function applySketchPaste(action) {
  if (action.fit === "canvas") {
    const width = action.imageData.width;
    const height = action.imageData.height;
    if (sketchCanvas.width !== width || sketchCanvas.height !== height) {
      // Deterministic replay: prior content scales exactly like canvas-scale,
      // then the pasted image stamps over the whole retargeted canvas.
      const src = sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height);
      const scaled = scaleSketchImageDataNearest(src, width, height);
      resizeSketchCanvasToSize(width, height);
      sketchCtx.putImageData(scaled, 0, 0);
    }
  }
  stampSketchImageData(action.dx, action.dy, action.imageData);
}

function sketchMaskContains(mask, x, y) {
  if (!mask) return true;
  if (mask.type === "rect") {
    const b = mask.bounds;
    return x >= b.x && x < b.x + b.w && y >= b.y && y < b.y + b.h;
  }
  return sketchPointInPolygon({ x: x + 0.5, y: y + 0.5 }, mask.points);
}

function sketchPaletteRgbList(includeWhite, paletteColors = sketchWorkingPalette) {
  const list = paletteColors.map((color) => sketchHexRgb(typeof color === "string" ? color : color.hex));
  if (includeWhite) list.push([255, 255, 255]);
  return list;
}

function nearestSketchPaletteRgb(r, g, b, palette) {
  let best = palette[0];
  let bestDist = Infinity;
  for (const color of palette) {
    const dr = r - color[0];
    const dg = g - color[1];
    const db = b - color[2];
    const dist = dr * dr + dg * dg + db * db;
    if (dist < bestDist) {
      bestDist = dist;
      best = color;
    }
  }
  return best;
}

function nearestSketchPaletteIndex(r, g, b) {
  let best = 0;
  let bestDist = Infinity;
  for (let i = 0; i < sketchWorkingPalette.length; i++) {
    const color = sketchHexRgb(sketchWorkingPalette[i].hex);
    const dr = r - color[0];
    const dg = g - color[1];
    const db = b - color[2];
    const dist = dr * dr + dg * dg + db * db;
    if (dist < bestDist) {
      bestDist = dist;
      best = i;
    }
  }
  return best;
}

function applySketchPaletteFilter(action) {
  const width = sketchCanvas.width;
  const height = sketchCanvas.height;
  const image = sketchCtx.getImageData(0, 0, width, height);
  const data = image.data;
  const sourcePalette = Array.isArray(action.palette) && action.palette.length > 0
    ? action.palette
    : sketchWorkingPalette;
  const palette = sketchPaletteRgbList(!!action.includeWhite, sourcePalette);
  const mask = action.mask || null;
  const mode = action.mode;
  const work = mode === "floyd" ? new Float32Array(data.length) : null;
  if (work) {
    for (let i = 0; i < data.length; i++) work[i] = data[i];
  }
  const groupStep = Math.max(1, action.tolerance || 24);
  const clampByte = (value) => Math.max(0, Math.min(255, value));

  const writePixel = (offset, rgb) => {
    data[offset] = rgb[0];
    data[offset + 1] = rgb[1];
    data[offset + 2] = rgb[2];
    data[offset + 3] = 255;
  };

  for (let y = 0; y < height; y++) {
    const xStart = mode === "floyd" && (y % 2) ? width - 1 : 0;
    const xEnd = mode === "floyd" && (y % 2) ? -1 : width;
    const xStep = mode === "floyd" && (y % 2) ? -1 : 1;
    for (let x = xStart; x !== xEnd; x += xStep) {
      if (!sketchMaskContains(mask, x, y)) continue;
      const offset = (y * width + x) * 4;
      let r = work ? work[offset] : data[offset];
      let g = work ? work[offset + 1] : data[offset + 1];
      let b = work ? work[offset + 2] : data[offset + 2];
      if (mode === "group") {
        r = Math.round(r / groupStep) * groupStep;
        g = Math.round(g / groupStep) * groupStep;
        b = Math.round(b / groupStep) * groupStep;
      } else if (mode === "bayer") {
        const threshold = (SketchBayer8[y & 7][x & 7] / 64 - 0.5) * 48;
        r += threshold;
        g += threshold;
        b += threshold;
      }
      const chosen = nearestSketchPaletteRgb(clampByte(r), clampByte(g), clampByte(b), palette);
      writePixel(offset, chosen);
      if (work) {
        const errR = r - chosen[0];
        const errG = g - chosen[1];
        const errB = b - chosen[2];
        const diffuse = (nx, ny, factor) => {
          if (nx < 0 || nx >= width || ny < 0 || ny >= height) return;
          if (!sketchMaskContains(mask, nx, ny)) return;
          const n = (ny * width + nx) * 4;
          work[n] += errR * factor;
          work[n + 1] += errG * factor;
          work[n + 2] += errB * factor;
        };
        if (xStep > 0) {
          diffuse(x + 1, y, 7 / 16);
          diffuse(x - 1, y + 1, 3 / 16);
          diffuse(x, y + 1, 5 / 16);
          diffuse(x + 1, y + 1, 1 / 16);
        } else {
          diffuse(x - 1, y, 7 / 16);
          diffuse(x + 1, y + 1, 3 / 16);
          diffuse(x, y + 1, 5 / 16);
          diffuse(x - 1, y + 1, 1 / 16);
        }
      }
    }
  }
  sketchCtx.putImageData(image, 0, 0);
}

function applySketchRecolor(action) {
  const width = sketchCanvas.width;
  const height = sketchCanvas.height;
  const image = sketchCtx.getImageData(0, 0, width, height);
  const data = image.data;
  const fill = sketchHexRgb(action.color);
  const from = action.fromRgb;
  const tolerance = action.tolerance;
  const mask = action.mask || null;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      if (!sketchMaskContains(mask, x, y)) continue;
      const offset = (y * width + x) * 4;
      if (Math.abs(data[offset] - from[0]) > tolerance
          || Math.abs(data[offset + 1] - from[1]) > tolerance
          || Math.abs(data[offset + 2] - from[2]) > tolerance) {
        continue;
      }
      data[offset] = fill[0];
      data[offset + 1] = fill[1];
      data[offset + 2] = fill[2];
      data[offset + 3] = 255;
    }
  }
  sketchCtx.putImageData(image, 0, 0);
}

function rotateSketchImageData90Cw(src) {
  const w = src.width;
  const h = src.height;
  const dst = new ImageData(h, w);
  const s = src.data;
  const d = dst.data;
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const si = (y * w + x) * 4;
      const dx = h - 1 - y;
      const dy = x;
      const di = (dy * h + dx) * 4;
      d[di] = s[si];
      d[di + 1] = s[si + 1];
      d[di + 2] = s[si + 2];
      d[di + 3] = s[si + 3];
    }
  }
  return dst;
}

function applySketchCanvasRotate(quarterTurns) {
  const turns = ((quarterTurns % 4) + 4) % 4;
  if (!turns) return;
  let image = sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height);
  for (let i = 0; i < turns; i++) image = rotateSketchImageData90Cw(image);
  const aspect = sketchAspectNameFromSize(image.width, image.height);
  if (aspect) sketchAspect = aspect;
  resizeSketchCanvasToSize(image.width, image.height);
  sketchCtx.putImageData(image, 0, 0);
}

function applySketchCanvasScale(width, height) {
  if (!Number.isInteger(width) || !Number.isInteger(height)
      || width < SketchScaleMinEdge || height < SketchScaleMinEdge
      || width > SketchScaleMaxEdge || height > SketchScaleMaxEdge
      || width * height > SketchScaleMaxPixels) {
    throw new Error(`canvas-scale action has invalid size ${width}×${height}`);
  }
  if (sketchCanvas.width === width && sketchCanvas.height === height) return;
  const src = sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height);
  const scaled = scaleSketchImageDataNearest(src, width, height);
  resizeSketchCanvasToSize(width, height);
  sketchCtx.putImageData(scaled, 0, 0);
}

function applySketchCanvasFlip(axis) {
  const width = sketchCanvas.width;
  const height = sketchCanvas.height;
  const src = sketchCtx.getImageData(0, 0, width, height);
  const dst = new ImageData(width, height);
  const s = src.data;
  const d = dst.data;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const nx = axis === "h" ? width - 1 - x : x;
      const ny = axis === "v" ? height - 1 - y : y;
      const si = (y * width + x) * 4;
      const di = (ny * width + nx) * 4;
      d[di] = s[si];
      d[di + 1] = s[si + 1];
      d[di + 2] = s[si + 2];
      d[di + 3] = s[si + 3];
    }
  }
  sketchCtx.putImageData(dst, 0, 0);
}

function applySketchCropOutside(action) {
  const kept = extractSketchSelectionBitmap({
    type: action.mask.type,
    points: action.mask.points,
    bounds: action.mask.bounds,
  });
  sketchBlank();
  stampSketchImageData(kept.x, kept.y, kept.imageData);
}

function applySketchCropFill(action) {
  const kept = extractSketchSelectionBitmap({
    type: action.mask.type,
    points: action.mask.points,
    bounds: action.mask.bounds,
  });
  if (action.fit === "canvas") {
    // Crop the canvas itself to the selection's own size and aspect ratio.
    resizeSketchCanvasToSize(kept.imageData.width, kept.imageData.height);
    sketchBlank();
    stampSketchImageData(0, 0, kept.imageData);
    return;
  }
  const fit = sketchFitRect(
    kept.imageData.width,
    kept.imageData.height,
    sketchCanvas.width,
    sketchCanvas.height,
    action.fit || "cover");
  const src = document.createElement("canvas");
  src.width = kept.imageData.width;
  src.height = kept.imageData.height;
  src.getContext("2d").putImageData(kept.imageData, 0, 0);
  sketchBlank();
  sketchCtx.imageSmoothingEnabled = true;
  sketchCtx.imageSmoothingQuality = "high";
  sketchCtx.drawImage(src, fit.dx, fit.dy, fit.dw, fit.dh);
}

async function importImageIntoSketch(file) {
  if (!file || !file.type.startsWith("image/")) {
    setSketchActionStatus("Clipboard or file is not an image.", true);
    return;
  }
  if (sketchActions.length >= SketchActionLimit) {
    setSketchActionStatus(
      `This sketch already has ${SketchActionLimit} actions. Remove or combine actions before pasting.`,
      true);
    return;
  }
  let bitmap;
  try {
    bitmap = await createImageBitmap(file);
  } catch (err) {
    setSketchActionStatus(`Could not decode that image: ${err.message || err}`, true);
    return;
  }
  try {
    if (bitmap.width < 1 || bitmap.height < 1) {
      setSketchActionStatus("That image has no pixels.", true);
      return;
    }
    const fitMode = sketchCurrentPasteFit();
    const dest = fitMode === "canvas"
      ? (() => {
        const target = sketchCanvasSizeForImage(bitmap.width, bitmap.height);
        return { dx: 0, dy: 0, dw: target.width, dh: target.height };
      })()
      : sketchFitRect(
        bitmap.width, bitmap.height, sketchCanvas.width, sketchCanvas.height, fitMode);
    const imageData = rasterizeSketchBitmap(bitmap, dest.dw, dest.dh);
    const ok = commitSketchAction({
      kind: "paste",
      fit: fitMode,
      dx: dest.dx,
      dy: dest.dy,
      imageData,
    });
    if (!ok) return;
    const selX = Math.max(0, dest.dx);
    const selY = Math.max(0, dest.dy);
    const selW = Math.min(sketchCanvas.width - selX, dest.dw + Math.min(0, dest.dx));
    const selH = Math.min(sketchCanvas.height - selY, dest.dh + Math.min(0, dest.dy));
    if (selW > 0 && selH > 0) {
      setSketchSelection({
        type: "rect",
        points: [
          { x: selX, y: selY },
          { x: selX + selW, y: selY },
          { x: selX + selW, y: selY + selH },
          { x: selX, y: selY + selH },
        ],
        bounds: { x: selX, y: selY, w: selW, h: selH },
      });
    }
    setSketchActionStatus(fitMode === "canvas"
      ? `Pasted ${bitmap.width}×${bitmap.height}; canvas is now ${sketchCanvas.width} × ${sketchCanvas.height} to match the image. `
        + "Edit as usual; snap, dither, or regen template from image colors before attaching."
      : `Pasted ${bitmap.width}×${bitmap.height} with ${fitMode}. `
        + "Edit as usual; snap, dither, or regen template from image colors before attaching.");
  } finally {
    bitmap.close();
  }
}

function exportSketchCanvasPngBlob() {
  return new Promise((resolve, reject) => {
    sketchCanvas.toBlob((blob) => {
      if (blob) resolve(blob);
      else reject(new Error("could not encode the canvas as PNG"));
    }, "image/png");
  });
}

async function copySketchCanvasToClipboard() {
  if (!navigator.clipboard?.write || typeof ClipboardItem === "undefined") {
    setSketchActionStatus("This browser cannot write an image to the clipboard.", true);
    return;
  }
  try {
    const blobPromise = exportSketchCanvasPngBlob();
    await navigator.clipboard.write([new ClipboardItem({ "image/png": blobPromise })]);
    setSketchActionStatus(
      `Copied ${sketchCanvas.width}×${sketchCanvas.height} PNG to the clipboard.`);
  } catch (err) {
    setSketchActionStatus(`Could not copy the canvas (${err.message || err}).`, true);
  }
}

async function pasteSketchFromClipboard() {
  if (navigator.clipboard?.read) {
    try {
      const items = await navigator.clipboard.read();
      for (const item of items) {
        const type = item.types.find((candidate) => candidate.startsWith("image/"));
        if (!type) continue;
        const blob = await item.getType(type);
        await importImageIntoSketch(new File([blob], "clipboard.png", { type: blob.type || type }));
        return;
      }
    } catch (err) {
      setSketchActionStatus(
        `Could not read the clipboard (${err.message || err}). Use Ctrl+V or load an image file.`,
        true);
      return;
    }
  }
  setSketchActionStatus("No image on the clipboard. Copy an image, then press Ctrl+V.", true);
}

function commitSketchPaletteFilter() {
  const mode = sketchCurrentDitherMethod();
  const mask = sketchSelection ? selectionToMask(sketchSelection) : null;
  commitSketchAction({
    kind: "palette-filter",
    mode,
    includeWhite: sketchDitherWhiteInput.checked,
    tolerance: Number(sketchFillToleranceInput.value),
    palette: sketchWorkingPalette.map((color) => color.hex),
    mask,
  });
}

function sketchRgbToHex(r, g, b) {
  return `#${[r, g, b].map((channel) => channel.toString(16).padStart(2, "0")).join("")}`;
}

function sketchIsNearWhite(r, g, b) {
  return r >= 245 && g >= 245 && b >= 245;
}

function sketchRgbToHsl(r, g, b) {
  const rn = r / 255;
  const gn = g / 255;
  const bn = b / 255;
  const max = Math.max(rn, gn, bn);
  const min = Math.min(rn, gn, bn);
  const lightness = (max + min) / 2;
  let hue = 0;
  let saturation = 0;
  if (max !== min) {
    const delta = max - min;
    saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
    if (max === rn) hue = (gn - bn) / delta + (gn < bn ? 6 : 0);
    else if (max === gn) hue = (bn - rn) / delta + 2;
    else hue = (rn - gn) / delta + 4;
    hue /= 6;
  }
  return { h: hue * 360, s: saturation * 100, l: lightness * 100 };
}

function sketchHueFamilyName(hue) {
  if (hue < 15) return "red";
  if (hue < 45) return "orange";
  if (hue < 70) return "yellow";
  if (hue < 160) return "green";
  if (hue < 200) return "cyan";
  if (hue < 255) return "blue";
  if (hue < 290) return "purple";
  if (hue < 340) return "magenta";
  return "red";
}

function sketchColorDisplayName(r, g, b) {
  const { h, s, l } = sketchRgbToHsl(r, g, b);
  if (s < 12) {
    if (l < 18) return "black";
    if (l < 40) return "dark gray";
    if (l < 70) return "gray";
    return "light gray";
  }
  const hue = sketchHueFamilyName(h);
  if (hue === "blue" && s < 45 && l >= 28 && l <= 65) return "steel blue";
  if (hue === "green" && l < 40) return "dark green";
  const light = l < 28 ? "dark " : l > 72 ? "light " : "";
  const muted = s < 35 && !light ? "muted " : "";
  return `${light}${muted}${hue}`.replace(/\s+/g, " ").trim();
}

function uniqueSketchColorNames(colors) {
  const used = new Set();
  return colors.map((color) => {
    const base = sketchColorDisplayName(color.r, color.g, color.b);
    let name = base;
    let n = 2;
    while (used.has(name.toLocaleLowerCase())) {
      name = `${base} ${n}`;
      n += 1;
    }
    used.add(name.toLocaleLowerCase());
    return name;
  });
}

function extractDominantSketchColors() {
  const width = sketchCanvas.width;
  const height = sketchCanvas.height;
  const data = sketchCtx.getImageData(0, 0, width, height).data;
  const buckets = new Map();
  let ink = 0;
  for (let i = 0; i < data.length; i += 4) {
    const r = data[i];
    const g = data[i + 1];
    const b = data[i + 2];
    if (sketchIsNearWhite(r, g, b)) continue;
    ink += 1;
    const key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
    let bucket = buckets.get(key);
    if (!bucket) {
      bucket = { sumR: 0, sumG: 0, sumB: 0, count: 0 };
      buckets.set(key, bucket);
    }
    bucket.sumR += r;
    bucket.sumG += g;
    bucket.sumB += b;
    bucket.count += 1;
  }
  if (ink === 0) return { ink: false, colors: [] };

  const ranked = [...buckets.values()]
    .map((bucket) => {
      const r = Math.round(bucket.sumR / bucket.count);
      const g = Math.round(bucket.sumG / bucket.count);
      const b = Math.round(bucket.sumB / bucket.count);
      return {
        r,
        g,
        b,
        count: bucket.count,
        packed: (r << 16) | (g << 8) | b,
      };
    })
    .sort((a, b) => b.count - a.count || a.packed - b.packed);

  const minCount = Math.max(1, Math.round(ink * 0.008));
  const minDistSq = 40 * 40;
  const picked = [];
  const seenHex = new Set();
  for (const candidate of ranked) {
    if (picked.length >= SketchPalette.length) break;
    if (picked.length > 0 && candidate.count < minCount) continue;
    if (sketchIsNearWhite(candidate.r, candidate.g, candidate.b)) continue;
    const hex = sketchRgbToHex(candidate.r, candidate.g, candidate.b);
    if (hex === "#ffffff" || seenHex.has(hex)) continue;
    const tooClose = picked.some((existing) => {
      const dr = candidate.r - existing.r;
      const dg = candidate.g - existing.g;
      const db = candidate.b - existing.b;
      return dr * dr + dg * dg + db * db <= minDistSq;
    });
    if (tooClose) continue;
    seenHex.add(hex);
    picked.push(candidate);
  }
  if (picked.length === 0) {
    const fallback = ranked.find((candidate) => {
      const hex = sketchRgbToHex(candidate.r, candidate.g, candidate.b);
      return hex !== "#ffffff" && !sketchIsNearWhite(candidate.r, candidate.g, candidate.b);
    });
    if (fallback) picked.push(fallback);
  }
  if (picked.length === 0) return { ink: true, colors: [] };
  const names = uniqueSketchColorNames(picked);
  return {
    ink: true,
    colors: picked.map((color, index) => ({
      name: names[index],
      hex: sketchRgbToHex(color.r, color.g, color.b),
    })),
  };
}

function commitSketchRegenTemplateMap() {
  if (sketchActions.length >= SketchActionLimit) {
    setSketchActionStatus(
      `This sketch already has ${SketchActionLimit} actions. Remove or combine actions before drawing more.`,
      true);
    return;
  }
  const extracted = extractDominantSketchColors();
  if (!extracted.ink) {
    setSketchActionStatus("The canvas is blank — paste or draw first.", true);
    return;
  }
  if (extracted.colors.length < 1) {
    setSketchActionStatus("No distinct non-white colors to map.", true);
    return;
  }
  rememberSketchState();
  setSketchWorkingPalette(extracted.colors, { meanings: extracted.colors.map(() => "") });
  const recorded = {
    kind: "template-map",
    palette: cloneSketchPalette(extracted.colors),
    includeWhite: true,
    id: ++sketchActionSequence,
    visible: true,
  };
  sketchActions = [...sketchActions, recorded];
  applySketchAction(recorded);
  recordSketchReplayAfterRender();
  renderSketchActionList();
  setSketchActionStatus(
    `Template from ${extracted.colors.length} image color`
    + `${extracted.colors.length === 1 ? "" : "s"}. `
    + "Write what each color means; those labels go into the composition legend.");
}

function pickSketchEyedropper(point) {
  const x = Math.max(0, Math.min(sketchCanvas.width - 1, Math.round(point.x)));
  const y = Math.max(0, Math.min(sketchCanvas.height - 1, Math.round(point.y)));
  const pixel = sketchCtx.getImageData(x, y, 1, 1).data;
  const index = nearestSketchPaletteIndex(pixel[0], pixel[1], pixel[2]);
  setSketchColor(index);
  setSketchActionStatus(`Eyedropper: ${sketchWorkingPalette[index].name}`);
}

function commitSketchRecolorAt(point) {
  const x = Math.max(0, Math.min(sketchCanvas.width - 1, Math.round(point.x)));
  const y = Math.max(0, Math.min(sketchCanvas.height - 1, Math.round(point.y)));
  const pixel = sketchCtx.getImageData(x, y, 1, 1).data;
  commitSketchAction({
    kind: "recolor",
    fromRgb: [pixel[0], pixel[1], pixel[2]],
    color: sketchWorkingPalette[sketchActiveColor].hex,
    tolerance: Number(sketchFillToleranceInput.value),
    mask: sketchSelection ? selectionToMask(sketchSelection) : null,
  });
}

function commitSketchCanvasRotate(quarterTurns) {
  const turns = ((quarterTurns % 4) + 4) % 4;
  if (!turns) return;
  clearSketchSelection();
  commitSketchAction({ kind: "canvas-rotate", quarterTurns: turns });
}

function commitSketchCanvasFlip(axis) {
  clearSketchSelection();
  commitSketchAction({ kind: "canvas-flip", axis });
}

function commitSketchCanvasScale(factor) {
  const next = sketchScaleTargetSize(sketchCanvas.width, sketchCanvas.height, factor);
  if (next.width === sketchCanvas.width && next.height === sketchCanvas.height) {
    setSketchActionStatus(
      factor === 0.5
        ? `Already ${sketchCanvas.width} × ${sketchCanvas.height} — cannot halve further.`
        : `Already ${sketchCanvas.width} × ${sketchCanvas.height} — cannot double further.`,
      true);
    return;
  }
  clearSketchSelection();
  const ok = commitSketchAction({
    kind: "canvas-scale",
    width: next.width,
    height: next.height,
  });
  if (!ok) return;
  setSketchActionStatus(`${next.width} × ${next.height}`);
}

function commitSketchSelectionFlip(axis) {
  if (!sketchSelection) {
    setSketchActionStatus("Make a rectangular or free selection first.", true);
    return;
  }
  const bitmap = extractSketchSelectionBitmap(sketchSelection);
  const srcW = bitmap.imageData.width;
  const srcH = bitmap.imageData.height;
  const flipped = new ImageData(srcW, srcH);
  const s = bitmap.imageData.data;
  const d = flipped.data;
  for (let y = 0; y < srcH; y++) {
    for (let x = 0; x < srcW; x++) {
      const nx = axis === "h" ? srcW - 1 - x : x;
      const ny = axis === "v" ? srcH - 1 - y : y;
      const si = (y * srcW + x) * 4;
      const di = (ny * srcW + nx) * 4;
      d[di] = s[si];
      d[di + 1] = s[si + 1];
      d[di + 2] = s[si + 2];
      d[di + 3] = s[si + 3];
    }
  }
  const ok = commitSketchAction({
    kind: "region-edit",
    clearMask: selectionToMask(sketchSelection),
    stamp: { x: bitmap.x, y: bitmap.y, imageData: flipped },
    stampLabel: axis === "v" ? "flip V" : "flip H",
  });
  if (!ok) return;
  setSketchSelection({
    type: "rect",
    points: [
      { x: bitmap.x, y: bitmap.y },
      { x: bitmap.x + srcW, y: bitmap.y },
      { x: bitmap.x + srcW, y: bitmap.y + srcH },
      { x: bitmap.x, y: bitmap.y + srcH },
    ],
    bounds: { x: bitmap.x, y: bitmap.y, w: srcW, h: srcH },
  });
}

function commitSketchCropOutside() {
  if (!sketchSelection) {
    setSketchActionStatus("Make a selection to crop.", true);
    return;
  }
  const mask = selectionToMask(sketchSelection);
  commitSketchAction({ kind: "crop-outside", mask });
}

function commitSketchCropFill() {
  if (!sketchSelection) {
    setSketchActionStatus("Make a selection to crop onto the canvas.", true);
    return;
  }
  const mask = selectionToMask(sketchSelection);
  const fit = sketchCurrentPasteFit();
  const ok = commitSketchAction({ kind: "crop-fill", mask, fit });
  if (!ok) return;
  setSketchSelection({
    type: "rect",
    points: [
      { x: 0, y: 0 },
      { x: sketchCanvas.width, y: 0 },
      { x: sketchCanvas.width, y: sketchCanvas.height },
      { x: 0, y: sketchCanvas.height },
    ],
    bounds: { x: 0, y: 0, w: sketchCanvas.width, h: sketchCanvas.height },
  });
}

function clearSketchSelection() {
  sketchSelection = null;
  sketchSelectionOp = null;
  updateSketchSelectionOpButtons();
  drawSketchSelectionOverlay();
  updateSketchToolPresentation();
}

function setSketchSelection(selection) {
  sketchSelection = selection;
  sketchSelectionOp = null;
  updateSketchSelectionOpButtons();
  drawSketchSelectionOverlay();
  updateSketchToolPresentation();
}

function updateSketchSelectionOpButtons() {
  const has = !!sketchSelection;
  for (const id of [
    "sketch-selection-clear",
    "sketch-selection-move",
    "sketch-selection-duplicate",
    "sketch-selection-stretch",
    "sketch-selection-rotate",
    "sketch-selection-flip-h",
    "sketch-selection-flip-v",
    "sketch-selection-crop",
    "sketch-selection-crop-fill",
    "sketch-selection-deselect",
  ]) {
    el(id).disabled = !has;
  }
  for (const [id, op] of [
    ["sketch-selection-move", "move"],
    ["sketch-selection-duplicate", "duplicate"],
    ["sketch-selection-stretch", "stretch"],
    ["sketch-selection-rotate", "rotate"],
  ]) {
    el(id).classList.toggle("active", has && sketchSelectionOp === op);
  }
  updateSketchDitherApplyLabel();
}

function setSketchSelectionOp(op) {
  if (!sketchSelection) {
    setSketchActionStatus("Make a rectangular or free selection first.", true);
    return;
  }
  if (!SketchSelectionModes.has(op)) return;
  sketchSelectionOp = sketchSelectionOp === op ? null : op;
  updateSketchSelectionOpButtons();
  updateSketchToolPresentation();
  setSketchActionStatus(
    sketchSelectionOp
      ? `Selection ${sketchSelectionOp}: drag on the selection to apply.`
      : "");
}

function drawSketchSelectionOverlay() {
  if (!sketchOverlayCtx) return;
  sketchOverlayCtx.clearRect(0, 0, sketchOverlay.width, sketchOverlay.height);
  const selection = sketchGesture?.kind === "selecting"
    ? sketchGesture.previewSelection
    : sketchSelection;
  if (!selection) {
    if (sketchOverlayRaf) {
      cancelAnimationFrame(sketchOverlayRaf);
      sketchOverlayRaf = 0;
    }
    return;
  }
  const dash = 6;
  const offset = (sketchOverlayFrame / 3) % (dash * 2);
  sketchOverlayCtx.save();
  sketchOverlayCtx.lineWidth = 1.5;
  sketchOverlayCtx.setLineDash([dash, dash]);
  sketchOverlayCtx.lineDashOffset = -offset;
  sketchOverlayCtx.beginPath();
  if (selection.type === "rect") {
    const b = selection.bounds;
    sketchOverlayCtx.rect(b.x + 0.5, b.y + 0.5, b.w, b.h);
  } else if (selection.points?.length) {
    sketchOverlayCtx.moveTo(selection.points[0].x, selection.points[0].y);
    for (let i = 1; i < selection.points.length; i++) {
      sketchOverlayCtx.lineTo(selection.points[i].x, selection.points[i].y);
    }
    if (selection.closed !== false) sketchOverlayCtx.closePath();
  }
  sketchOverlayCtx.strokeStyle = "#ffffff";
  sketchOverlayCtx.stroke();
  sketchOverlayCtx.lineDashOffset = -offset + dash;
  sketchOverlayCtx.strokeStyle = "#111827";
  sketchOverlayCtx.stroke();
  if (selection.type === "rect" || selection.bounds) {
    const b = selection.bounds;
    const handles = [
      { x: b.x, y: b.y },
      { x: b.x + b.w, y: b.y },
      { x: b.x, y: b.y + b.h },
      { x: b.x + b.w, y: b.y + b.h },
    ];
    sketchOverlayCtx.fillStyle = "#ffffff";
    sketchOverlayCtx.strokeStyle = "#111827";
    sketchOverlayCtx.setLineDash([]);
    for (const handle of handles) {
      sketchOverlayCtx.beginPath();
      sketchOverlayCtx.rect(handle.x - 3, handle.y - 3, 6, 6);
      sketchOverlayCtx.fill();
      sketchOverlayCtx.stroke();
    }
  }
  sketchOverlayCtx.restore();
  if (!sketchOverlayRaf) {
    const tick = () => {
      sketchOverlayFrame++;
      sketchOverlayRaf = 0;
      if (sketchOverlayFrame % 3 !== 0) {
        if (sketchSelection || sketchGesture?.kind === "selecting") {
          sketchOverlayRaf = requestAnimationFrame(tick);
        }
        return;
      }
      drawSketchSelectionOverlay();
    };
    sketchOverlayRaf = requestAnimationFrame(tick);
  }
}

function commitSketchSelectionClear() {
  if (!sketchSelection) return;
  commitSketchAction({
    kind: "region-edit",
    clearMask: selectionToMask(sketchSelection),
    stamp: null,
    stampLabel: "clear selection",
  });
}

function selectionTransformDest(bitmap, op, gesture) {
  const srcW = bitmap.imageData.width;
  const srcH = bitmap.imageData.height;
  if (op === "move" || op === "duplicate") {
    return {
      x: bitmap.x + gesture.dx,
      y: bitmap.y + gesture.dy,
      w: srcW,
      h: srcH,
    };
  }
  if (op === "stretch") {
    const scaleX = gesture.scaleX || 1;
    const scaleY = gesture.scaleY || 1;
    const originX = gesture.anchorX + (bitmap.x - gesture.anchorX) * scaleX;
    const originY = gesture.anchorY + (bitmap.y - gesture.anchorY) * scaleY;
    return {
      x: Math.min(originX, originX + srcW * scaleX),
      y: Math.min(originY, originY + srcH * scaleY),
      w: Math.max(1, Math.abs(srcW * scaleX)),
      h: Math.max(1, Math.abs(srcH * scaleY)),
    };
  }
  const angle = gesture.angle || 0;
  const cos = Math.abs(Math.cos(angle));
  const sin = Math.abs(Math.sin(angle));
  const w = Math.max(1, Math.ceil(srcW * cos + srcH * sin));
  const h = Math.max(1, Math.ceil(srcW * sin + srcH * cos));
  return {
    x: bitmap.x + srcW / 2 - w / 2,
    y: bitmap.y + srcH / 2 - h / 2,
    w,
    h,
  };
}

function transformSelectionBitmap(bitmap, op, gesture) {
  const { imageData } = bitmap;
  const srcW = imageData.width;
  const srcH = imageData.height;
  if (op === "move" || op === "duplicate") {
    return {
      x: bitmap.x + gesture.dx,
      y: bitmap.y + gesture.dy,
      imageData,
    };
  }
  const srcCanvas = gesture.stampCanvas || canvasFromSketchImageData(imageData);
  if (op === "stretch") {
    const scaleX = gesture.scaleX || 1;
    const scaleY = gesture.scaleY || 1;
    const outW = Math.max(1, Math.round(srcW * Math.abs(scaleX)));
    const outH = Math.max(1, Math.round(srcH * Math.abs(scaleY)));
    const dest = gesture.previewCanvas || document.createElement("canvas");
    dest.width = outW;
    dest.height = outH;
    const ctx = dest.getContext("2d");
    ctx.imageSmoothingEnabled = true;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, outW, outH);
    ctx.save();
    if (scaleX < 0) {
      ctx.translate(outW, 0);
      ctx.scale(-1, 1);
    }
    if (scaleY < 0) {
      ctx.translate(0, outH);
      ctx.scale(1, -1);
    }
    ctx.drawImage(srcCanvas, 0, 0, outW, outH);
    ctx.restore();
    const destBox = selectionTransformDest(bitmap, op, gesture);
    return {
      x: destBox.x,
      y: destBox.y,
      imageData: ctx.getImageData(0, 0, outW, outH),
    };
  }
  const destBox = selectionTransformDest(bitmap, op, gesture);
  const outW = Math.max(1, Math.round(destBox.w));
  const outH = Math.max(1, Math.round(destBox.h));
  const dest = gesture.previewCanvas || document.createElement("canvas");
  dest.width = outW;
  dest.height = outH;
  const ctx = dest.getContext("2d");
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, outW, outH);
  ctx.translate(outW / 2, outH / 2);
  ctx.rotate(gesture.angle || 0);
  ctx.drawImage(srcCanvas, -srcW / 2, -srcH / 2);
  return {
    x: destBox.x,
    y: destBox.y,
    imageData: ctx.getImageData(0, 0, outW, outH),
  };
}

function prepareSketchTransformGesture(gesture) {
  gesture.stampCanvas = canvasFromSketchImageData(gesture.bitmap.imageData);
  gesture.previewCanvas = document.createElement("canvas");
  if (gesture.op === "move" || gesture.op === "stretch" || gesture.op === "rotate") {
    sketchCtx.putImageData(gesture.baseImage, 0, 0);
    eraseSketchMask(gesture.mask);
    gesture.erasedBase = sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height);
    sketchCtx.putImageData(gesture.baseImage, 0, 0);
  } else {
    gesture.erasedBase = gesture.baseImage;
  }
  gesture.lastPreviewRect = {
    x: gesture.bitmap.x,
    y: gesture.bitmap.y,
    w: gesture.bitmap.imageData.width,
    h: gesture.bitmap.imageData.height,
  };
}

function previewSketchTransformGesture(gesture) {
  const dest = selectionTransformDest(gesture.bitmap, gesture.op, gesture);
  restoreSketchDirtyRect(gesture.erasedBase, gesture.lastPreviewRect);
  if (gesture.op === "move" || gesture.op === "duplicate") {
    sketchCtx.drawImage(gesture.stampCanvas, Math.round(dest.x), Math.round(dest.y));
  } else if (gesture.op === "stretch") {
    const scaleX = gesture.scaleX || 1;
    const scaleY = gesture.scaleY || 1;
    sketchCtx.save();
    sketchCtx.imageSmoothingEnabled = true;
    sketchCtx.translate(dest.x, dest.y);
    sketchCtx.scale(scaleX < 0 ? -1 : 1, scaleY < 0 ? -1 : 1);
    sketchCtx.drawImage(
      gesture.stampCanvas,
      scaleX < 0 ? -dest.w : 0,
      scaleY < 0 ? -dest.h : 0,
      dest.w,
      dest.h);
    sketchCtx.restore();
  } else {
    const srcW = gesture.bitmap.imageData.width;
    const srcH = gesture.bitmap.imageData.height;
    sketchCtx.save();
    sketchCtx.translate(dest.x + dest.w / 2, dest.y + dest.h / 2);
    sketchCtx.rotate(gesture.angle || 0);
    sketchCtx.drawImage(gesture.stampCanvas, -srcW / 2, -srcH / 2);
    sketchCtx.restore();
  }
  gesture.lastPreviewRect = dest;
}

function finishSelectionTransform(gesture) {
  const clearSource = gesture.op === "move" || gesture.op === "stretch" || gesture.op === "rotate";
  const stamp = transformSelectionBitmap(gesture.bitmap, gesture.op, gesture);
  const ok = commitSketchAction({
    kind: "region-edit",
    clearMask: clearSource ? cloneSketchMask(gesture.mask) : null,
    stamp: {
      x: stamp.x,
      y: stamp.y,
      imageData: stamp.imageData,
    },
    stampLabel: gesture.op,
  }, gesture.baseImage);
  if (!ok) return;
  const w = stamp.imageData.width;
  const h = stamp.imageData.height;
  setSketchSelection({
    type: "rect",
    points: [
      { x: stamp.x, y: stamp.y },
      { x: stamp.x + w, y: stamp.y },
      { x: stamp.x + w, y: stamp.y + h },
      { x: stamp.x, y: stamp.y + h },
    ],
    bounds: { x: stamp.x, y: stamp.y, w, h },
  });
  sketchSelectionOp = null;
  updateSketchSelectionOpButtons();
}

setSketchColor(0);
setSketchTool("brush");
renderSketchActionList();

function sketchCanvasPoint(event) {
  const rect = sketchCanvasClientRect();
  return {
    x: (event.clientX - rect.left) * (sketchCanvas.width / rect.width),
    y: (event.clientY - rect.top) * (sketchCanvas.height / rect.height),
  };
}

function cancelSketchGesturePreview() {
  if (!sketchGestureRaf) return;
  cancelAnimationFrame(sketchGestureRaf);
  sketchGestureRaf = 0;
}

function flushSketchGesturePreview() {
  cancelSketchGesturePreview();
  if (sketchGesture) previewSketchGesture(sketchGesture);
}

function scheduleSketchGesturePreview() {
  if (sketchGestureRaf) return;
  sketchGestureRaf = requestAnimationFrame(() => {
    sketchGestureRaf = 0;
    if (sketchGesture) previewSketchGesture(sketchGesture);
  });
}

function restoreSketchGestureBase(gesture) {
  if (gesture.baseImage
      && gesture.baseImage.width === sketchCanvas.width
      && gesture.baseImage.height === sketchCanvas.height) {
    sketchCtx.putImageData(gesture.baseImage, 0, 0);
    drawSketchSelectionOverlay();
    return;
  }
  renderSketchActions();
}

function previewSketchStrokeGesture(gesture) {
  if (SketchBlendTools.has(gesture.tool)) {
    if (!gesture.blendState) {
      const image = cloneSketchImageData(gesture.baseImage);
      gesture.blendState = {
        image,
        source: new Uint8ClampedArray(gesture.baseImage.data),
        lastX: -1e9,
        lastY: -1e9,
        nextIndex: 0,
      };
    }
    drawSketchStroke({
      kind: gesture.tool,
      color: gesture.color,
      width: gesture.width,
      strength: gesture.strength,
      edgeStyle: gesture.edgeStyle,
      points: gesture.points,
      blendState: gesture.blendState,
    });
    return;
  }
  if (SketchPaintStampTools.has(gesture.tool)) {
    if (!gesture.stampState) {
      gesture.stampState = { lastX: -1e9, lastY: -1e9, nextIndex: 0, visitedCount: 0 };
    }
    drawSketchStroke({
      kind: gesture.tool,
      color: gesture.color,
      width: gesture.width,
      strength: gesture.strength,
      edgeStyle: gesture.edgeStyle,
      points: gesture.points,
      stampFrom: gesture.stampState.nextIndex,
      stampLastX: gesture.stampState.lastX,
      stampLastY: gesture.stampState.lastY,
      stampVisitedCount: gesture.stampState.visitedCount,
      stampState: gesture.stampState,
    });
    return;
  }
  const from = Math.max(0, (gesture.drawnCount || 0) - 1);
  if (from >= gesture.points.length - 1 && gesture.drawnCount) return;
  drawSketchStroke({
    kind: gesture.tool,
    color: gesture.color,
    width: gesture.width,
    edgeStyle: gesture.edgeStyle,
    points: gesture.points.slice(from),
  });
  gesture.drawnCount = gesture.points.length;
}

function previewSketchGesture(gesture) {
  if (gesture.kind === "selecting") {
    drawSketchSelectionOverlay();
    return;
  }
  if (gesture.kind === "transform") {
    previewSketchTransformGesture(gesture);
    return;
  }
  if (gesture.kind === "stroke") {
    previewSketchStrokeGesture(gesture);
    return;
  }
  sketchCtx.putImageData(gesture.baseImage, 0, 0);
  drawSketchShape({
    kind: gesture.tool,
    start: gesture.start,
    end: gesture.end,
    color: gesture.color,
    width: gesture.width,
    edgeStyle: gesture.edgeStyle,
    fillRule: gesture.fillRule,
  });
}

function applySketchTransformPoint(gesture, point) {
  if (gesture.op === "move" || gesture.op === "duplicate") {
    gesture.dx = point.x - gesture.origin.x;
    gesture.dy = point.y - gesture.origin.y;
    return;
  }
  if (gesture.op === "stretch") {
    const startDx = gesture.startCornerX - gesture.anchorX || 1e-6;
    const startDy = gesture.startCornerY - gesture.anchorY || 1e-6;
    gesture.scaleX = (point.x - gesture.anchorX) / startDx;
    gesture.scaleY = (point.y - gesture.anchorY) / startDy;
    if (!Number.isFinite(gesture.scaleX)) gesture.scaleX = 1;
    if (!Number.isFinite(gesture.scaleY)) gesture.scaleY = 1;
    return;
  }
  if (gesture.op === "rotate") {
    const current = Math.atan2(point.y - gesture.cy, point.x - gesture.cx);
    gesture.angle = current - gesture.startAngle;
  }
}

function applySketchSelectingPoint(gesture, point) {
  if (gesture.tool === "select-rect") {
    const x = Math.min(gesture.start.x, point.x);
    const y = Math.min(gesture.start.y, point.y);
    const w = Math.abs(point.x - gesture.start.x);
    const h = Math.abs(point.y - gesture.start.y);
    gesture.previewSelection = {
      type: "rect",
      points: [
        { x, y },
        { x: x + w, y },
        { x: x + w, y: y + h },
        { x, y: y + h },
      ],
      bounds: { x, y, w, h },
      closed: true,
    };
    return;
  }
  gesture.points.push(point);
  gesture.previewSelection = {
    type: "poly",
    points: gesture.points.slice(),
    bounds: sketchBoundsFromPoints(gesture.points),
    closed: false,
  };
}

function commitStrokeAtPointLimit(event, gesture) {
  cancelSketchGesturePreview();
  sketchGesture = null;
  if (sketchCanvas.hasPointerCapture(event.pointerId)) {
    sketchCanvas.releasePointerCapture(event.pointerId);
  }
  commitSketchAction({
    kind: gesture.tool,
    color: gesture.color,
    width: gesture.width,
    strength: gesture.strength,
    edgeStyle: gesture.edgeStyle,
    points: gesture.points,
  }, gesture.baseImage, !gesture.baseImage);
  setSketchActionStatus(
    `Stroke committed at the ${SketchStrokePointLimit}-point safety limit. Start another stroke to continue.`);
}

function nearestStretchCorner(bounds, point) {
  const corners = [
    { x: bounds.x, y: bounds.y, anchorX: bounds.x + bounds.w, anchorY: bounds.y + bounds.h },
    { x: bounds.x + bounds.w, y: bounds.y, anchorX: bounds.x, anchorY: bounds.y + bounds.h },
    { x: bounds.x, y: bounds.y + bounds.h, anchorX: bounds.x + bounds.w, anchorY: bounds.y },
    { x: bounds.x + bounds.w, y: bounds.y + bounds.h, anchorX: bounds.x, anchorY: bounds.y },
  ];
  let best = null;
  let bestDist = 18 * 18;
  for (const corner of corners) {
    const dist = (corner.x - point.x) ** 2 + (corner.y - point.y) ** 2;
    if (dist <= bestDist) {
      best = corner;
      bestDist = dist;
    }
  }
  return best;
}

sketchCanvas.addEventListener("pointerdown", (event) => {
  if (event.button !== 0) return;
  event.preventDefault();
  updateSketchCursor(event);
  if (sketchActions.length >= SketchActionLimit) {
    setSketchActionStatus(
      `This sketch already has ${SketchActionLimit} actions. Remove or combine actions before drawing more.`,
      true);
    return;
  }
  const point = sketchCanvasPoint(event);
  const color = sketchCurrentColor();

  if (sketchSelection && sketchSelectionOp) {
    if (sketchSelectionOp === "stretch") {
      const corner = nearestStretchCorner(sketchSelection.bounds, point);
      if (!corner) {
        setSketchActionStatus("Drag a selection corner to stretch.", true);
        return;
      }
      const bitmap = extractSketchSelectionBitmap(sketchSelection);
      sketchCanvas.setPointerCapture(event.pointerId);
      sketchGesture = {
        pointerId: event.pointerId,
        kind: "transform",
        op: "stretch",
        bitmap,
        mask: selectionToMask(sketchSelection),
        anchorX: corner.anchorX,
        anchorY: corner.anchorY,
        startCornerX: corner.x,
        startCornerY: corner.y,
        scaleX: 1,
        scaleY: 1,
        dx: 0,
        dy: 0,
        baseImage: sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height),
      };
      prepareSketchTransformGesture(sketchGesture);
      return;
    }
    if (sketchSelectionOp === "rotate") {
      if (!sketchSelectionContains(sketchSelection, point)) {
        setSketchActionStatus("Drag inside the selection to rotate.", true);
        return;
      }
      const bitmap = extractSketchSelectionBitmap(sketchSelection);
      const cx = sketchSelection.bounds.x + sketchSelection.bounds.w / 2;
      const cy = sketchSelection.bounds.y + sketchSelection.bounds.h / 2;
      sketchCanvas.setPointerCapture(event.pointerId);
      sketchGesture = {
        pointerId: event.pointerId,
        kind: "transform",
        op: "rotate",
        bitmap,
        mask: selectionToMask(sketchSelection),
        cx,
        cy,
        startAngle: Math.atan2(point.y - cy, point.x - cx),
        angle: 0,
        dx: 0,
        dy: 0,
        baseImage: sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height),
      };
      prepareSketchTransformGesture(sketchGesture);
      return;
    }
    if (sketchSelectionOp === "move" || sketchSelectionOp === "duplicate") {
      if (!sketchSelectionContains(sketchSelection, point)) {
        setSketchActionStatus(`Drag inside the selection to ${sketchSelectionOp}.`, true);
        return;
      }
      const bitmap = extractSketchSelectionBitmap(sketchSelection);
      sketchCanvas.setPointerCapture(event.pointerId);
      sketchGesture = {
        pointerId: event.pointerId,
        kind: "transform",
        op: sketchSelectionOp,
        bitmap,
        mask: selectionToMask(sketchSelection),
        origin: point,
        dx: 0,
        dy: 0,
        baseImage: sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height),
      };
      prepareSketchTransformGesture(sketchGesture);
      return;
    }
  }

  if (SketchSelectionTools.has(sketchActiveTool)) {
    sketchCanvas.setPointerCapture(event.pointerId);
    sketchGesture = {
      pointerId: event.pointerId,
      kind: "selecting",
      tool: sketchActiveTool,
      start: point,
      points: [point],
      previewSelection: sketchActiveTool === "select-rect"
        ? { type: "rect", points: [point, point, point, point], bounds: { x: point.x, y: point.y, w: 0, h: 0 }, closed: true }
        : { type: "poly", points: [point], bounds: { x: point.x, y: point.y, w: 0, h: 0 }, closed: false },
    };
    drawSketchSelectionOverlay();
    return;
  }

  if (sketchActiveTool === "fill") {
    commitSketchAction({
      kind: "fill",
      x: point.x,
      y: point.y,
      color,
      tolerance: Number(sketchFillToleranceInput.value),
    });
    return;
  }
  if (sketchActiveTool === "eyedropper") {
    pickSketchEyedropper(point);
    return;
  }
  if (sketchActiveTool === "recolor") {
    commitSketchRecolorAt(point);
    return;
  }
  sketchCanvas.setPointerCapture(event.pointerId);
  if (SketchShapeTools.has(sketchActiveTool) || sketchActiveTool === "line") {
    sketchGesture = {
      pointerId: event.pointerId,
      kind: "shape",
      tool: sketchActiveTool,
      color,
      width: sketchCurrentWidth(),
      edgeStyle: sketchCurrentEdgeStyle(),
      fillRule: sketchCurrentFillRule(),
      start: point,
      end: point,
      baseImage: sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height),
    };
    return;
  }
  if (SketchBlendTools.has(sketchActiveTool) || SketchPaintStampTools.has(sketchActiveTool)) {
    sketchGesture = {
      pointerId: event.pointerId,
      kind: "stroke",
      tool: sketchActiveTool,
      color,
      width: sketchCurrentWidth(),
      strength: Number(sketchBlendStrengthInput.value),
      edgeStyle: sketchCurrentEdgeStyle(),
      points: [point],
      baseImage: sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height),
    };
    previewSketchStrokeGesture(sketchGesture);
    return;
  }
  sketchGesture = {
    pointerId: event.pointerId,
    kind: "stroke",
    tool: sketchActiveTool,
    color,
    width: sketchCurrentWidth(),
    edgeStyle: sketchCurrentEdgeStyle(),
    points: [point],
    drawnCount: 0,
  };
  previewSketchStrokeGesture(sketchGesture);
});
sketchCanvas.addEventListener("pointermove", (event) => {
  updateSketchCursor(event);
  if (!sketchGesture || sketchGesture.pointerId !== event.pointerId) return;
  const samples = typeof event.getCoalescedEvents === "function"
    ? event.getCoalescedEvents()
    : [event];
  const events = samples.length ? samples : [event];
  if (sketchGesture.kind === "selecting") {
    for (const sample of events) {
      applySketchSelectingPoint(sketchGesture, sketchCanvasPoint(sample));
    }
    scheduleSketchGesturePreview();
    return;
  }
  if (sketchGesture.kind === "transform") {
    applySketchTransformPoint(sketchGesture, sketchCanvasPoint(events[events.length - 1]));
    scheduleSketchGesturePreview();
    return;
  }
  if (sketchGesture.kind === "stroke") {
    for (const sample of events) {
      if (sketchGesture.points.length >= SketchStrokePointLimit) {
        previewSketchStrokeGesture(sketchGesture);
        commitStrokeAtPointLimit(event, sketchGesture);
        return;
      }
      sketchGesture.points.push(sketchCanvasPoint(sample));
    }
    if (sketchGesture.points.length >= SketchStrokePointLimit) {
      previewSketchStrokeGesture(sketchGesture);
      commitStrokeAtPointLimit(event, sketchGesture);
      return;
    }
    scheduleSketchGesturePreview();
    return;
  }
  sketchGesture.end = sketchCanvasPoint(events[events.length - 1]);
  scheduleSketchGesturePreview();
});
function finishSketchGesture(event, cancelled = false) {
  if (!sketchGesture || sketchGesture.pointerId !== event.pointerId) return;
  if (cancelled) {
    cancelSketchGesturePreview();
    const gesture = sketchGesture;
    sketchGesture = null;
    restoreSketchGestureBase(gesture);
    return;
  }
  flushSketchGesturePreview();
  const gesture = sketchGesture;
  sketchGesture = null;
  if (gesture.kind === "selecting") {
    if (gesture.tool === "select-rect") {
      const selection = gesture.previewSelection;
      if (!selection || selection.bounds.w < 2 || selection.bounds.h < 2) {
        drawSketchSelectionOverlay();
        setSketchActionStatus("Drag a rectangle with both width and height.", true);
        return;
      }
      setSketchSelection({
        type: "rect",
        points: selection.points,
        bounds: selection.bounds,
      });
      return;
    }
    const points = gesture.points;
    if (points.length < 3) {
      drawSketchSelectionOverlay();
      setSketchActionStatus("Draw a free selection with at least three points.", true);
      return;
    }
    const bounds = sketchBoundsFromPoints(points);
    if (bounds.w < 2 || bounds.h < 2) {
      drawSketchSelectionOverlay();
      setSketchActionStatus("Free selection is too small.", true);
      return;
    }
    setSketchSelection({
      type: "poly",
      points: points.map((entry) => ({ x: entry.x, y: entry.y })),
      bounds,
    });
    return;
  }
  if (gesture.kind === "transform") {
    if ((gesture.op === "move" || gesture.op === "duplicate")
        && Math.abs(gesture.dx) < 1 && Math.abs(gesture.dy) < 1) {
      restoreSketchGestureBase(gesture);
      setSketchActionStatus("Drag to move the selection.", true);
      return;
    }
    if (gesture.op === "stretch"
        && Math.abs((gesture.scaleX || 1) - 1) < 0.02
        && Math.abs((gesture.scaleY || 1) - 1) < 0.02) {
      restoreSketchGestureBase(gesture);
      setSketchActionStatus("Drag a corner farther to stretch.", true);
      return;
    }
    if (gesture.op === "rotate" && Math.abs(gesture.angle || 0) < 0.02) {
      restoreSketchGestureBase(gesture);
      setSketchActionStatus("Drag to rotate the selection.", true);
      return;
    }
    finishSelectionTransform(gesture);
    return;
  }
  if (gesture.kind === "stroke") {
    commitSketchAction({
      kind: gesture.tool,
      color: gesture.color,
      width: gesture.width,
      strength: gesture.strength,
      edgeStyle: gesture.edgeStyle,
      points: gesture.points,
    }, gesture.baseImage, !gesture.baseImage);
    return;
  }
  const dx = Math.abs(gesture.end.x - gesture.start.x);
  const dy = Math.abs(gesture.end.y - gesture.start.y);
  if (gesture.tool === "line" && dx < 1 && dy < 1) {
    restoreSketchGestureBase(gesture);
    setSketchActionStatus("Drag to give the line a visible length.", true);
    return;
  }
  if (gesture.tool !== "line" && (dx < 2 || dy < 2)) {
    restoreSketchGestureBase(gesture);
    setSketchActionStatus("Drag to give the shape both width and height.", true);
    return;
  }
  commitSketchAction({
    kind: gesture.tool,
    start: gesture.start,
    end: gesture.end,
    color: gesture.color,
    width: gesture.width,
    edgeStyle: gesture.edgeStyle,
    fillRule: gesture.fillRule,
  }, gesture.baseImage);
}
sketchCanvas.addEventListener("pointerup", (event) => finishSketchGesture(event));
sketchCanvas.addEventListener("pointercancel", (event) => finishSketchGesture(event, true));
sketchCanvas.addEventListener("pointerenter", updateSketchCursor);
sketchCanvas.addEventListener("pointerleave", () => sketchCursor.classList.remove("visible"));

document.querySelectorAll("#sketch-tool-rack [data-sketch-tool]").forEach((button) => {
  button.addEventListener("click", () => setSketchTool(button.dataset.sketchTool));
});
sketchBrushInput.addEventListener("input", updateSketchToolPresentation);
sketchFillToleranceInput.addEventListener("input", updateSketchToolPresentation);
sketchBlendStrengthInput.addEventListener("input", updateSketchToolPresentation);
sketchEdgeStyleSelect.addEventListener("change", updateSketchToolPresentation);
sketchFillRuleSelect.addEventListener("change", updateSketchToolPresentation);
el("sketch-undo").addEventListener("click", undoSketchActionChange);
el("sketch-redo").addEventListener("click", redoSketchActionChange);
el("sketch-clear").addEventListener("click", () => {
  clearSketchSelection();
  commitSketchAction({ kind: "clear" });
});
el("sketch-selection-clear").addEventListener("click", commitSketchSelectionClear);
el("sketch-selection-move").addEventListener("click", () => setSketchSelectionOp("move"));
el("sketch-selection-duplicate").addEventListener("click", () => setSketchSelectionOp("duplicate"));
el("sketch-selection-stretch").addEventListener("click", () => setSketchSelectionOp("stretch"));
el("sketch-selection-rotate").addEventListener("click", () => setSketchSelectionOp("rotate"));
el("sketch-selection-flip-h").addEventListener("click", () => commitSketchSelectionFlip("h"));
el("sketch-selection-flip-v").addEventListener("click", () => commitSketchSelectionFlip("v"));
el("sketch-selection-crop").addEventListener("click", commitSketchCropOutside);
el("sketch-selection-crop-fill").addEventListener("click", commitSketchCropFill);
el("sketch-selection-deselect").addEventListener("click", clearSketchSelection);
el("sketch-paste").addEventListener("click", () => pasteSketchFromClipboard());
el("sketch-copy").addEventListener("click", () => copySketchCanvasToClipboard());
el("sketch-import").addEventListener("click", () => sketchImportFileInput.click());
sketchImportFileInput.addEventListener("change", () => {
  const file = sketchImportFileInput.files?.[0];
  sketchImportFileInput.value = "";
  if (file) importImageIntoSketch(file);
});
el("sketch-rotate-cw").addEventListener("click", () => commitSketchCanvasRotate(1));
el("sketch-rotate-ccw").addEventListener("click", () => commitSketchCanvasRotate(3));
el("sketch-rotate-180").addEventListener("click", () => commitSketchCanvasRotate(2));
el("sketch-flip-h").addEventListener("click", () => commitSketchCanvasFlip("h"));
el("sketch-flip-v").addEventListener("click", () => commitSketchCanvasFlip("v"));
el("sketch-scale-half").addEventListener("click", () => commitSketchCanvasScale(0.5));
el("sketch-scale-double").addEventListener("click", () => commitSketchCanvasScale(2));
el("sketch-dither-apply").addEventListener("click", commitSketchPaletteFilter);
el("sketch-regen-template").addEventListener("click", commitSketchRegenTemplateMap);
sketchDitherMethodSelect.addEventListener("change", updateSketchDitherApplyLabel);
sketchPasteFitSelect.addEventListener("change", updateSketchDitherApplyLabel);
el("sketch-canvas-wrap").addEventListener("dragover", (event) => {
  if (![...event.dataTransfer.types].includes("Files")) return;
  event.preventDefault();
  el("sketch-canvas-wrap").classList.add("dragover");
});
el("sketch-canvas-wrap").addEventListener("dragleave", (event) => {
  if (event.currentTarget.contains(event.relatedTarget)) return;
  el("sketch-canvas-wrap").classList.remove("dragover");
});
sketchDialog.addEventListener("dragover", (event) => {
  if (![...event.dataTransfer.types].includes("Files")) return;
  event.preventDefault();
});
sketchDialog.addEventListener("drop", (event) => {
  event.preventDefault();
  el("sketch-canvas-wrap").classList.remove("dragover");
  const files = [...event.dataTransfer.files].filter((file) => file.type.startsWith("image/"));
  if (files[0]) importImageIntoSketch(files[0]);
  else setSketchActionStatus("Drop an image file onto the canvas.", true);
});
sketchDialog.addEventListener("keydown", (event) => {
  if (event.key === "Delete" || event.key === "Backspace") {
    if (event.target instanceof Element
        && event.target.closest("input, textarea, select, [contenteditable]")) {
      return;
    }
    if (sketchSelection) {
      event.preventDefault();
      commitSketchSelectionClear();
    }
    return;
  }
  if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
  if (event.target instanceof Element
      && event.target.closest("input, textarea, select, [contenteditable]")) {
    return;
  }
  const key = event.key.toLowerCase();
  if (key === "z" && !event.shiftKey) {
    event.preventDefault();
    event.stopPropagation();
    undoSketchActionChange();
  } else if (key === "y" || (key === "z" && event.shiftKey)) {
    event.preventDefault();
    event.stopPropagation();
    redoSketchActionChange();
  } else if (key === "c") {
    event.preventDefault();
    event.stopPropagation();
    copySketchCanvasToClipboard();
  } else if (key === "d" && sketchSelection) {
    event.preventDefault();
    setSketchSelectionOp("duplicate");
  }
});
sketchAspectSelect.addEventListener("change", () => {
  const next = sketchAspectSelect.value;
  if (!Object.hasOwn(SketchCanvasSizes, next)) {
    syncSketchAspectSelectToCanvas();
    return;
  }
  if (sketchScanColors().ink
      && !confirm("Changing the canvas shape clears the sketch. Continue?")) {
    syncSketchAspectSelectToCanvas();
    return;
  }
  sketchAspect = next;
  resizeSketchCanvasToAspect(next);
  clearSketchSelection();
  resetSketchActions();
  setSketchWorkingPalette(SketchPalette, { meanings: SketchPalette.map(() => "") });
  setSketchColor(0);
  syncSketchAspectSelectToCanvas();
});

// Which palette colors the sketch actually contains, by exact pixel match,
// plus whether any non-white ink exists at all. Runs on attach and before a
// destructive shape change — a full-canvas scan is a few milliseconds.
function sketchScanColors() {
  const data = sketchCtx.getImageData(0, 0, sketchCanvas.width, sketchCanvas.height).data;
  const paletteByRgb = new Map(sketchWorkingPalette.map((color, index) => [
    (parseInt(color.hex.slice(1, 3), 16) << 16)
      | (parseInt(color.hex.slice(3, 5), 16) << 8)
      | parseInt(color.hex.slice(5, 7), 16),
    index,
  ]));
  const used = new Set();
  let ink = false;
  for (let i = 0; i < data.length; i += 4) {
    const rgb = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
    if (rgb !== 0xffffff) ink = true;
    const index = paletteByRgb.get(rgb);
    if (index !== undefined) {
      used.add(index);
      if (used.size === sketchWorkingPalette.length) break;
    }
  }
  return { used, ink };
}

function buildSketchLegendParagraph(usedIndexes) {
  const meanings = [...usedIndexes]
    .sort((a, b) => a - b)
    .map((index) => ({
      name: sketchWorkingPalette[index].name,
      text: sketchMeaningInputs[index].value.trim(),
    }))
    .filter((entry) => entry.text);
  const colorSentence = meanings.length > 0
    ? ` In the sketch: ${meanings.map((entry) => `${entry.name} = ${entry.text}`).join("; ")}.`
    : "";
  return SketchLegendMarker
    + " one attached image is a spatial map for the final image. Place each named subject where"
    + " its mapped color, outline, scribble, or group of strokes appears, using roughly that share"
    + " of the frame. Interpret sparse lines and repeated strokes as the approximate extent of one"
    + " mapped region. The sketch colors identify subjects; choose the final palette, materials,"
    + " lighting, and rendering style from the rest of this prompt and from each subject's natural"
    + " appearance. White or unmarked areas are flexible background and negative space that may be"
    + " used as needed to make the complete composition."
    + colorSentence
    + " Create a complete new image from this map and use the legend words as instructions.";
}

// Replaces any existing legend paragraph (identified by its exact marker) or
// appends a new one, so revising and re-attaching the sketch never stacks
// stale legends. The rest of the prompt is untouched.
function upsertSketchLegendInPrompt(paragraph) {
  const kept = promptBox.value
    .split(/\n{2,}/)
    .filter((p) => p.trim().length > 0 && !p.trimStart().startsWith(SketchLegendMarker));
  kept.push(paragraph);
  promptBox.value = kept.join("\n\n");
  promptBox.dispatchEvent(new Event("input", { bubbles: true }));
  if (mcpheeCtl) mcpheeCtl.refresh();
  if (mcpheePanel && !mcpheePanelContainer.hidden) mcpheePanel.refresh();
  updatePromptLimitNotice();
}

async function attachSketch() {
  sketchStatus.textContent = "";
  sketchStatus.classList.remove("error");
  const { used, ink } = sketchScanColors();
  if (!ink) {
    sketchStatus.textContent = "draw something first — the canvas is blank";
    sketchStatus.classList.add("error");
    return;
  }
  if (used.size === 0) {
    sketchStatus.textContent = "no palette colors found — snap, dither, regen template, or recolor onto the legend colors, or draw with the palette";
    sketchStatus.classList.add("error");
    return;
  }
  const unlabeled = [...used]
    .filter((index) => !sketchMeaningInputs[index].value.trim())
    .map((index) => sketchWorkingPalette[index].name);
  if (unlabeled.length > 0
      && !confirm(`No meaning written for: ${unlabeled.join(", ")}. `
        + "Attach anyway? (unexplained colors are left out of the legend)")) {
    return;
  }
  const blob = await new Promise((resolve) => sketchCanvas.toBlob(resolve, "image/png"));
  if (!blob) {
    sketchStatus.textContent = "could not export the sketch as a PNG";
    sketchStatus.classList.add("error");
    return;
  }
  const file = new File([blob], "sketch.png", { type: "image/png" });
  const attachedState = currentSketchAttachedState();
  const legendParagraph = buildSketchLegendParagraph(used);
  // A revised sketch replaces the previously attached one, located by exact
  // object identity — never by position or by guessing among attachments.
  if (sketchAttachedFile) {
    const previousIndex = inputImageItems.findIndex((item) => item.file === sketchAttachedFile);
    if (previousIndex !== -1) {
      removeInputAt(previousIndex, { preserveSketchEditor: true });
    }
  }
  if (!appendImage(file)) {
    sketchStatus.textContent = `could not attach — all ${maxInputImages} input slots are in use`;
    sketchStatus.classList.add("error");
    return;
  }
  sketchAttachedFile = file;
  sketchAttachedState = attachedState;
  sketchEditorNotice = "";
  // Only some models were live-tested as actually following sketches
  // (server truth: sketchCapable in /api/config). Default-on toggle:
  // deselect the media targets that can't, leaving describe/analysis
  // selections alone. Re-enabling one later is allowed — its chip turns
  // amber via updateGeneratorCompatibility as the standing reminder.
  if (el("sketch-autodeselect").checked) {
    for (const cb of allGeneratorInputs()) {
      if (cb.checked
          && cb.dataset.kind !== "describe"
          && cb.dataset.sketchCapable !== "true") {
        cb.checked = false;
      }
    }
  }
  // Re-run compatibility now that the sketch attachment is registered:
  // applies the amber sketch-unsupported tint/tooltips and syncs the
  // checked-state classes of anything just deselected.
  updateGeneratorCompatibility();
  upsertSketchLegendInPrompt(legendParagraph);
  sketchDialog.close();
}

const SketchDialogFloor = { width: 640, height: 420 };

let sketchDialogBox = null;
let sketchDialogResizeStart = null;

function defaultSketchDialogGeometry() {
  const width = Math.min(1500, Math.max(280, window.innerWidth - 28));
  const height = Math.max(240, window.innerHeight - 28);
  return {
    width,
    height,
    left: Math.max(0, Math.round((window.innerWidth - width) / 2)),
    top: Math.max(0, Math.round((window.innerHeight - height) / 2)),
  };
}

function clampSketchDialogGeometry(box) {
  const viewWidth = window.innerWidth;
  const viewHeight = window.innerHeight;
  const minWidth = Math.min(SketchDialogFloor.width, viewWidth);
  const minHeight = Math.min(SketchDialogFloor.height, viewHeight);
  const width = Math.round(Math.max(minWidth, Math.min(viewWidth, box.width)));
  const height = Math.round(Math.max(minHeight, Math.min(viewHeight, box.height)));
  const left = Math.round(Math.min(Math.max(0, box.left), Math.max(0, viewWidth - width)));
  const top = Math.round(Math.min(Math.max(0, box.top), Math.max(0, viewHeight - height)));
  return { width, height, left, top };
}

function syncSketchMaximizeButton() {
  const button = el("sketch-maximize");
  const maximized = sketchDialog.classList.contains("is-maximized");
  button.textContent = maximized ? "restore" : "full screen";
  button.title = maximized
    ? "Return the sketch window to its previous size"
    : "Expand the sketch window to the whole screen";
  button.setAttribute("aria-pressed", maximized ? "true" : "false");
  button.setAttribute("aria-label", maximized ? "Restore sketch window" : "Maximize sketch window");
}

function applySketchDialogBox(box) {
  sketchDialogBox = box.maximized
    ? { ...box, maximized: true }
    : { ...clampSketchDialogGeometry(box), maximized: false };
  sketchDialog.classList.toggle("is-maximized", sketchDialogBox.maximized);
  if (sketchDialogBox.maximized) {
    sketchDialog.style.left = "";
    sketchDialog.style.top = "";
    sketchDialog.style.width = "";
    sketchDialog.style.height = "";
  } else {
    sketchDialog.style.left = `${sketchDialogBox.left}px`;
    sketchDialog.style.top = `${sketchDialogBox.top}px`;
    sketchDialog.style.width = `${sketchDialogBox.width}px`;
    sketchDialog.style.height = `${sketchDialogBox.height}px`;
  }
  syncSketchMaximizeButton();
}

function fitSketchCanvasDisplay() {
  const wrap = el("sketch-canvas-wrap");
  if (sketchCanvas.width < 1 || sketchCanvas.height < 1) return;
  wrap.style.setProperty("--sketch-aspect", String(sketchCanvas.width / sketchCanvas.height));
  invalidateSketchCanvasRect();
}

function layoutSketchDialog() {
  if (!sketchDialogBox) {
    applySketchDialogBox({ ...defaultSketchDialogGeometry(), maximized: false });
    return;
  }
  applySketchDialogBox(sketchDialogBox);
}

el("sketch-open").addEventListener("click", () => {
  sketchOverlay.width = sketchCanvas.width;
  sketchOverlay.height = sketchCanvas.height;
  invalidateSketchCanvasRect();
  drawSketchSelectionOverlay();
  sketchStatus.textContent = sketchEditorNotice;
  sketchStatus.classList.remove("error");
  layoutSketchDialog();
  sketchDialog.showModal();
  fitSketchCanvasDisplay();
});
el("sketch-maximize").addEventListener("click", () => {
  if (sketchDialog.classList.contains("is-maximized")) {
    const restored = sketchDialogBox
      ? { ...sketchDialogBox, maximized: false }
      : { ...defaultSketchDialogGeometry(), maximized: false };
    applySketchDialogBox(restored);
  } else {
    const current = sketchDialogBox || { ...defaultSketchDialogGeometry(), maximized: false };
    applySketchDialogBox({ ...current, maximized: true });
  }
  fitSketchCanvasDisplay();
});
if (typeof ResizeObserver === "function") {
  new ResizeObserver(() => invalidateSketchCanvasRect()).observe(el("sketch-canvas-wrap"));
}
sketchDialog.addEventListener("scroll", invalidateSketchCanvasRect, true);
window.addEventListener("resize", () => {
  if (!sketchDialog.open || sketchDialogResizeStart) {
    invalidateSketchCanvasRect();
    return;
  }
  layoutSketchDialog();
  fitSketchCanvasDisplay();
});

const sketchResizeHandle = el("sketch-resize");
function moveSketchDialogResize(event) {
  if (!sketchDialogResizeStart) return;
  applySketchDialogBox({
    width: sketchDialogResizeStart.width + event.clientX - sketchDialogResizeStart.pointerX,
    height: sketchDialogResizeStart.height + event.clientY - sketchDialogResizeStart.pointerY,
    left: sketchDialogResizeStart.left,
    top: sketchDialogResizeStart.top,
    maximized: false,
  });
  fitSketchCanvasDisplay();
}
function endSketchDialogResize(event) {
  if (!sketchDialogResizeStart) return;
  sketchDialogResizeStart = null;
  if (event && sketchResizeHandle.hasPointerCapture(event.pointerId)) {
    sketchResizeHandle.releasePointerCapture(event.pointerId);
  }
}
sketchResizeHandle.addEventListener("pointerdown", (event) => {
  if (event.button !== 0) return;
  event.preventDefault();
  const rect = sketchDialog.getBoundingClientRect();
  sketchDialogResizeStart = {
    pointerX: event.clientX,
    pointerY: event.clientY,
    width: rect.width,
    height: rect.height,
    left: rect.left,
    top: rect.top,
  };
  try { sketchResizeHandle.setPointerCapture(event.pointerId); } catch { /* the window listeners still track the drag */ }
});
window.addEventListener("pointermove", moveSketchDialogResize);
window.addEventListener("pointerup", endSketchDialogResize);
window.addEventListener("pointercancel", endSketchDialogResize);
sketchResizeHandle.addEventListener("keydown", (event) => {
  const step = event.shiftKey ? 80 : 24;
  let dw = 0;
  let dh = 0;
  if (event.key === "ArrowRight") dw = step;
  else if (event.key === "ArrowLeft") dw = -step;
  else if (event.key === "ArrowDown") dh = step;
  else if (event.key === "ArrowUp") dh = -step;
  else return;
  event.preventDefault();
  event.stopPropagation();
  const base = sketchDialog.classList.contains("is-maximized")
    ? {
      width: sketchDialog.getBoundingClientRect().width,
      height: sketchDialog.getBoundingClientRect().height,
      left: 0,
      top: 0,
    }
    : (sketchDialogBox || defaultSketchDialogGeometry());
  applySketchDialogBox({
    width: base.width + dw,
    height: base.height + dh,
    left: base.left,
    top: base.top,
    maximized: false,
  });
  fitSketchCanvasDisplay();
});
el("sketch-close").addEventListener("click", () => sketchDialog.close());
el("sketch-cancel").addEventListener("click", () => sketchDialog.close());
el("sketch-form").addEventListener("submit", (event) => {
  event.preventDefault();
  attachSketch();
});

