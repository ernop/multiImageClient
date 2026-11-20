// Generated-image viewer, preloader, comparison, and navigation (extracted from app.js).

// ---------- generated-image viewer ----------

function getImageViewerPrompts() {
  const prompts = [];
  // Normal mode walks live jobs then expanded archive days. Favorites mode
  // walks only the dedicated filtered favorites gallery, so the same image
  // cannot appear twice when its original job card is also loaded.
  const cardSelector = favoriteBrowseUser === null
    ? "#jobs .job, #archive .job"
    : "#favorites-grid .favorite-gallery-card";
  const walkedSplitGroups = new Set();
  for (const card of document.querySelectorAll(cardSelector)) {
    if (!imageViewerWalksCard(card)) continue;
    // A split group walks the way its rows read: each image generator's
    // parts in part order, then the next generator.
    const group = card.closest(".split-group");
    if (!group) {
      pushImageViewerPrompt(prompts, card, card);
      continue;
    }
    if (walkedSplitGroups.has(group)) continue;
    walkedSplitGroups.add(group);
    const parts = [...group.querySelectorAll(".split-group-parts > .job")].filter(imageViewerWalksCard);
    for (const maker of group.querySelectorAll(".split-maker")) {
      for (const part of parts) {
        const cell = part.querySelector(`.cell[data-gen="${CSS.escape(maker.dataset.gen)}"]`);
        if (cell) pushImageViewerPrompt(prompts, part, cell);
      }
    }
  }
  return prompts;
}

// Night-hidden and person-filtered jobs are invisible to the viewer's
// keyboard walk too, as are jobs inside a collapsed archive day.
function imageViewerWalksCard(card) {
  if (card.classList.contains("night-hidden")) return false;
  if (card.classList.contains("user-filter-hidden")) return false;
  const dayContainer = card.closest(".archive-day-jobs");
  return !(dayContainer && dayContainer.hidden);
}

// One walk entry: the card's prompt and the results inside scope, which is
// the card itself or one of its cells.
function pushImageViewerPrompt(prompts, card, scope) {
  const jobId = card.dataset.jobId || card.id.substring("job-".length);
  const items = [...scope.querySelectorAll('a[data-viewer-image="true"]')].map((link) => ({
    jobId,
    generator: link.dataset.generator,
    imageIndex: Number(link.dataset.imageIndex),
    generatorCount: Number(link.dataset.generatorCount),
    url: link.href,
    thumbUrl: link.querySelector("img")?.getAttribute("src") || "",
    // Describe results ride the same walk: kind "text" items point their
    // url at the described INPUT image and carry the description text plus
    // the model's separated meta comments.
    kind: link.dataset.resultKind || "image",
    describeText: link.dataset.describeText || "",
    describeComments: link.dataset.describeComments || "",
  }));
  if (items.length === 0) return;
  let generatorExtraTexts = {};
  if (card.dataset.generatorExtraTexts) {
    try {
      generatorExtraTexts = JSON.parse(card.dataset.generatorExtraTexts);
    } catch {
      generatorExtraTexts = {};
    }
  }
  prompts.push({
    jobId,
    prompt: card.querySelector(".job-prompt").textContent,
    hasInput: card.dataset.hasInputImage === "true",
    gpt2Guidance: card.dataset.gpt2Guidance || "",         // "sent" | "off" | "" (unknown)
    gpt2GuidanceText: card.dataset.gpt2GuidanceText || "",
    generatorExtraTexts,
    generatorExtraTextsKnown: card.dataset.generatorExtraTextsKnown === "true",
    condensedPrompts: card.dataset.condensedPrompts ? JSON.parse(card.dataset.condensedPrompts) : [],
    splitLabel: card.dataset.splitLabel || "",
    items,
  });
}

function getImageViewerFlatItems() {
  return getImageViewerPrompts().flatMap((prompt) => prompt.items);
}

// A split part's job has one walk entry per image generator, so the match is
// the entry that holds this exact item.
function locateImageViewerState(prompts) {
  if (!imageViewerState) return null;
  for (const [promptIndex, prompt] of prompts.entries()) {
    if (prompt.jobId !== imageViewerState.jobId) continue;
    const itemIndex = prompt.items.findIndex((item) =>
      item.generator === imageViewerState.generator &&
      item.imageIndex === imageViewerState.imageIndex);
    if (itemIndex >= 0) return { promptIndex, itemIndex, prompt, item: prompt.items[itemIndex] };
  }
  return null;
}

function loadViewerSeen() {
  try {
    const saved = JSON.parse(localStorage.getItem(ViewerSeenKey) || "[]");
    return new Set(Array.isArray(saved) ? saved.filter((k) => typeof k === "string") : []);
  } catch {
    return new Set();
  }
}

function viewerSeenKeyFor(jobId, generator, imageIndex) {
  return `${jobId}|${generator}|${imageIndex}`;
}

// The outer-page thumbnail anchor for a viewer item/state, matched by exact
// identity (job id + generator + image index) — never by position.
function findViewerAnchor(ref) {
  if (!ref) return null;
  const scope = favoriteBrowseUser === null
    ? document.querySelectorAll("#jobs a[data-viewer-image='true'], #archive a[data-viewer-image='true']")
    : document.querySelectorAll("#favorites-grid a[data-viewer-image='true']");
  for (const link of scope) {
    if (link.dataset.jobId !== ref.jobId) continue;
    if (link.dataset.generator === ref.generator &&
        Number(link.dataset.imageIndex) === Number(ref.imageIndex)) {
      return link;
    }
  }
  return null;
}

// Called whenever a frame actually paints in the viewer: records the image as
// seen (persisted per-browser) and marks its card thumbnail immediately.
function markImageViewed(item) {
  const key = viewerSeenKeyFor(item.jobId, item.generator, item.imageIndex);
  if (!viewerSeenSet.has(key)) {
    viewerSeenSet.add(key);
    // Set preserves insertion order, so the front is the oldest mark.
    while (viewerSeenSet.size > ViewerSeenCap) {
      viewerSeenSet.delete(viewerSeenSet.values().next().value);
    }
    try {
      localStorage.setItem(ViewerSeenKey, JSON.stringify([...viewerSeenSet]));
    } catch (error) {
      console.warn("could not persist viewer-seen marks", error);
    }
  }
  const link = findViewerAnchor(item);
  if (link) link.classList.add("viewer-seen");
}

// Departure pulse: the thumbnail of the image the user just closed out of
// glows briefly so their eye lands on it — in both handback modes.
function pulseViewerAnchor(link) {
  link.classList.remove("viewer-return-pulse");
  void link.offsetWidth; // restart the animation when re-closing onto the same image
  link.classList.add("viewer-return-pulse");
  link.addEventListener("animationend",
    () => link.classList.remove("viewer-return-pulse"), { once: true });
}

function sortImageViewerPreloadWaiters() {
  imageViewerPreloadWaiters.sort((a, b) => a.priority - b.priority);
}

function enqueueImageViewerPreloadWaiter(priority, entry) {
  return new Promise((resolve, reject) => {
    const waiter = { priority, resolve, reject, entry };
    entry.waiter = waiter;
    imageViewerPreloadWaiters.push(waiter);
    sortImageViewerPreloadWaiters();
  });
}

async function takeImageViewerPreloadSlot(priority, entry) {
  if (imageViewerPreloadActive < ImageViewerPreloadConcurrency) {
    imageViewerPreloadActive++;
    entry.waiter = null;
    return;
  }
  await enqueueImageViewerPreloadWaiter(priority, entry);
  entry.waiter = null;
}

function releaseImageViewerPreloadSlot() {
  const next = imageViewerPreloadWaiters.shift();
  if (next) {
    if (next.entry) next.entry.waiter = null;
    next.resolve();
    return;
  }
  imageViewerPreloadActive = Math.max(0, imageViewerPreloadActive - 1);
}

function setImageViewerEntryPriority(entry, priority) {
  if (priority === entry.priority) return;
  entry.priority = priority;
  if (!entry.waiter) return;
  entry.waiter.priority = priority;
  sortImageViewerPreloadWaiters();
}

function discardImageViewerCacheEntry(url, entry) {
  if (imageViewerCache.get(url) !== entry) return;
  imageViewerCache.delete(url);
  if (entry.waiter) {
    const idx = imageViewerPreloadWaiters.indexOf(entry.waiter);
    if (idx >= 0) imageViewerPreloadWaiters.splice(idx, 1);
    const waiter = entry.waiter;
    entry.waiter = null;
    // Never acquired a slot — reject so the async body does not pretend it
    // holds concurrency and call release.
    waiter.reject(new DOMException("Image preload aborted", "AbortError"));
  }
  if (entry.controller) entry.controller.abort();
  if (entry.blobUrl) {
    URL.revokeObjectURL(entry.blobUrl);
    scheduleImageViewerPreloadHud();
    return;
  }
  entry.promise
    .then(() => {
      if (entry.blobUrl) URL.revokeObjectURL(entry.blobUrl);
    })
    .catch(() => {});
  scheduleImageViewerPreloadHud();
}

function abortImageViewerInFlight() {
  for (const [url, entry] of [...imageViewerCache]) {
    if (entry.fetched || entry.ready) continue;
    discardImageViewerCacheEntry(url, entry);
  }
}

function trimImageViewerReadyCache() {
  const keepers = [...imageViewerCache.entries()]
    .filter(([, entry]) => entry.fetched || entry.ready);
  const excess = keepers.length - ImageViewerCacheReadyCap;
  if (excess <= 0) return;
  for (let i = 0; i < excess; i++) {
    discardImageViewerCacheEntry(keepers[i][0], keepers[i][1]);
  }
}

function imageViewerInputUrl(jobId) {
  return apiUrl(`api/jobs/${encodeURIComponent(jobId)}/images/input/0`);
}

function findImageViewerInputThumbEl(jobId) {
  for (const card of document.querySelectorAll("#jobs .job, #archive .job")) {
    const cardJobId = card.dataset.jobId || card.id.substring("job-".length);
    if (cardJobId !== jobId) continue;
    const thumb = card.querySelector(".job-input-thumb");
    if (thumb) return thumb;
  }
  return null;
}

function findViewerPreviewThumb(item) {
  const anchor = findViewerAnchor(item);
  return anchor ? anchor.querySelector("img") : null;
}

function viewerPreviewThumbUrl(thumb) {
  if (!thumb) return "";
  return thumb.currentSrc || thumb.src || "";
}

function setImageViewerPreviewNotFinal(active) {
  imageViewerPreviewBadge.hidden = !active;
  imageViewerStage.classList.toggle("preview-not-final", active);
  imageViewerDimensions.classList.toggle("preview-not-final", active);
}

function setImageViewerAwaitingPixels(active) {
  imageViewerAwaitingPixels.hidden = !active;
  imageViewerStage.classList.toggle("awaiting-pixels", active);
  imageViewerStage.setAttribute("aria-busy", active ? "true" : "false");
}

let imageViewerPreviewLoadToken = 0;

function loadImageViewerEntry(url, priority) {
  const existing = imageViewerCache.get(url);
  if (existing) {
    setImageViewerEntryPriority(existing, priority);
    return existing;
  }

  const controller = new AbortController();
  const entry = {
    promise: null,
    blobUrl: null,
    image: null,
    controller,
    priority,
    waiter: null,
    acquired: false,
    fetched: false,
    ready: false,
  };
  entry.promise = (async () => {
    await takeImageViewerPreloadSlot(entry.priority, entry);
    entry.acquired = true;
    scheduleImageViewerPreloadHud();
    try {
      if (controller.signal.aborted) {
        throw new DOMException("Image preload aborted", "AbortError");
      }
      const response = await fetch(url, {
        signal: controller.signal,
        cache: "force-cache",
      });
      if (!response.ok) throw new Error(`image preload returned HTTP ${response.status}`);
      const blob = await response.blob();
      if (!blob.type.startsWith("image/")) {
        throw new Error(`image preload returned ${blob.type || "an unknown content type"}`);
      }

      // Free the network slot before decode so neighbors can fetch while
      // this frame's pixels land in the decoder.
      entry.fetched = true;
      entry.acquired = false;
      releaseImageViewerPreloadSlot();
      scheduleImageViewerPreloadHud();

      entry.blobUrl = URL.createObjectURL(blob);
      entry.image = new Image();
      entry.image.src = entry.blobUrl;
      await entry.image.decode();
      entry.ready = true;
      scheduleImageViewerPreloadHud();
      if (imageViewer.hidden) trimImageViewerReadyCache();
      return entry;
    } finally {
      if (entry.acquired) {
        entry.acquired = false;
        releaseImageViewerPreloadSlot();
      }
    }
  })().catch((error) => {
    if (entry.blobUrl) URL.revokeObjectURL(entry.blobUrl);
    if (imageViewerCache.get(url) === entry) imageViewerCache.delete(url);
    scheduleImageViewerPreloadHud();
    throw error;
  });
  imageViewerCache.set(url, entry);
  scheduleImageViewerPreloadHud();
  return entry;
}

// Build a bounded, adaptive working set. Neutral opens hedge both directions.
// Once movement starts, image steps and prompt jumps reinforce one shared
// direction while the latest navigation kind leads its own landing-point
// family. Every plan still keeps one opposite image and prompt landing warm.
function prepareImageViewerWindow(prompts, current) {
  const allItems = prompts.flatMap((prompt) => prompt.items);
  const currentIndex = allItems.findIndex((item) =>
    item.jobId === current.item.jobId &&
    item.generator === current.item.generator &&
    item.imageIndex === current.item.imageIndex);
  const promptByJobId = new Map(prompts.map((p) => [p.jobId, p]));
  const wantedUrls = new Set();
  const schedule = [];
  let nextPriority = 0;

  const want = (url) => {
    wantedUrls.add(url);
    schedule.push({ url, priority: nextPriority++ });
  };

  const consider = (index) => {
    if (index < 0 || index >= allItems.length) return;
    const item = allItems[index];
    want(item.url);
    if (!imageViewerCompareInput) return;
    const prompt = promptByJobId.get(item.jobId);
    if (prompt && prompt.hasInput) want(imageViewerInputUrl(item.jobId));
  };

  const considerPromptFirst = (promptIndex) => {
    if (promptIndex < 0 || promptIndex >= prompts.length) return;
    const item = prompts[promptIndex].items[0];
    if (!item) return;
    want(item.url);
    if (imageViewerCompareInput && prompts[promptIndex].hasInput) {
      want(imageViewerInputUrl(item.jobId));
    }
  };

  want(current.item.url);
  if (imageViewerCompareInput && current.prompt.hasInput) {
    want(imageViewerInputUrl(current.item.jobId));
  }
  const immediateUrls = new Set(schedule.map((candidate) => candidate.url));
  if (!imageViewerCompareInput && current.prompt.hasInput) {
    // Warm the exact current input after the visible output is ready. Keeping
    // it in this bounded window prevents `c` off/on from revoking and then
    // repeatedly downloading the same full-resolution source.
    want(imageViewerInputUrl(current.item.jobId));
  }

  // Two bounded jobs share this plan. Job 1: one-image steps (±10). Job 2:
  // Ctrl+Left/Right prompt landings (items[0] of each prompt, ±10 prompts).
  // Prompt-first URLs are enqueued before far sequential images so they are
  // not starved behind ten one-step neighbors.
  consider(currentIndex - 1);
  consider(currentIndex + 1);
  considerPromptFirst(current.promptIndex);

  if (imageViewerNavDirection === 0) {
    // No observed preference yet: never assume Right.
    considerPromptFirst(current.promptIndex - 1);
    considerPromptFirst(current.promptIndex + 1);
    for (let distance = 2;
         distance <= Math.max(ImageViewerPreloadNextPrompts, ImageViewerPreloadPreviousPrompts);
         distance++) {
      considerPromptFirst(current.promptIndex - distance);
      considerPromptFirst(current.promptIndex + distance);
    }
    for (let distance = 2;
         distance <= Math.max(ImageViewerPreloadAhead, ImageViewerPreloadBehind);
         distance++) {
      consider(currentIndex - distance);
      consider(currentIndex + distance);
    }
  } else {
    const direction = imageViewerNavDirection;
    const opposite = -direction;
    // One move gives the selected direction the lead. Continued movement lets
    // up to four same-direction landings run before the opposite-side hedge.
    const leadDepth = Math.min(4, 1 + Math.floor(imageViewerNavStreak / 2));

    if (imageViewerNavKind === "prompt") {
      considerPromptFirst(current.promptIndex + direction);
      consider(currentIndex + direction);
      for (let distance = 2; distance <= leadDepth; distance++) {
        considerPromptFirst(current.promptIndex + direction * distance);
      }
      considerPromptFirst(current.promptIndex + opposite);
      consider(currentIndex + opposite);
      for (let distance = 2; distance <= leadDepth; distance++) {
        consider(currentIndex + direction * distance);
      }
    } else {
      consider(currentIndex + direction);
      considerPromptFirst(current.promptIndex + direction);
      for (let distance = 2; distance <= leadDepth; distance++) {
        consider(currentIndex + direction * distance);
      }
      consider(currentIndex + opposite);
      considerPromptFirst(current.promptIndex + opposite);
    }

    for (let distance = 2; distance <= ImageViewerPreloadNextPrompts; distance++) {
      considerPromptFirst(current.promptIndex + direction * distance);
    }
    for (let distance = 2; distance <= ImageViewerPreloadPreviousPrompts; distance++) {
      considerPromptFirst(current.promptIndex + opposite * distance);
    }
    for (let distance = 2; distance <= ImageViewerPreloadAhead; distance++) {
      consider(currentIndex + direction * distance);
    }
    for (let distance = 2; distance <= ImageViewerPreloadBehind; distance++) {
      consider(currentIndex + opposite * distance);
    }
  }

  for (const [url, entry] of imageViewerCache) {
    if (!wantedUrls.has(url)) discardImageViewerCacheEntry(url, entry);
  }

  // First occurrence is the strongest prediction. Rebuild every queued
  // priority, including demotions, so old-direction work cannot stay ahead
  // after the person reverses or switches between image and prompt movement.
  const seen = new Set();
  const uniqueSchedule = [];
  for (const candidate of schedule) {
    if (seen.has(candidate.url)) continue;
    seen.add(candidate.url);
    uniqueSchedule.push(candidate);
  }
  uniqueSchedule.forEach((candidate, priority) => {
    candidate.priority = priority;
    const entry = imageViewerCache.get(candidate.url);
    if (entry) setImageViewerEntryPriority(entry, priority);
  });
  sortImageViewerPreloadWaiters();

  // Request every thumb before starting originals. Keep the bounded range warm
  // even while the selected original is downloading or navigation continues.
  const thumbUrls = new Set();
  for (const item of allItems) {
    if (!wantedUrls.has(item.url)) continue;
    const url = viewerPreviewThumbUrl(findViewerPreviewThumb(item));
    if (!url) continue;
    thumbUrls.add(url);
    if (!imageViewerPreviewCache.has(url)) {
      const preview = new Image();
      preview.src = url;
      imageViewerPreviewCache.set(url, preview);
    }
  }
  for (const [url, preview] of imageViewerPreviewCache) {
    if (thumbUrls.has(url)) continue;
    preview.removeAttribute("src");
    imageViewerPreviewCache.delete(url);
  }

  // Keep in-range downloads. Preempt at most one neighbor only when the
  // selected original needs a slot; do not restart the entire working set.
  const selectedEntry = imageViewerCache.get(current.item.url);
  if (!selectedEntry?.acquired && !selectedEntry?.fetched &&
      imageViewerPreloadActive >= ImageViewerPreloadConcurrency) {
    const victim = [...imageViewerCache.entries()]
      .filter(([url, entry]) => !immediateUrls.has(url) && entry.acquired)
      .sort((a, b) => b[1].priority - a[1].priority)[0];
    if (victim) discardImageViewerCacheEntry(...victim);
  }
  let currentEntry = null;
  for (const { url, priority } of uniqueSchedule) {
    const entry = loadImageViewerEntry(url, priority);
    if (url === current.item.url) currentEntry = entry;
    else entry.promise.catch(() => {});
  }
  if (!currentEntry) throw new Error("viewer preload missing the selected image");
  return currentEntry;
}

function showImageViewerEntry(current, entry) {
  imageViewerPreviewLoadToken++;
  setImageViewerAwaitingPixels(false);
  setImageViewerPreviewNotFinal(false);
  imageViewerImage.src = entry.blobUrl;
  imageViewerImage.alt = current.item.kind === "text"
    ? `input image ${current.item.imageIndex + 1} of ${current.item.generatorCount} described by ${current.item.generator}`
    : `${current.item.generator} image ${current.item.imageIndex + 1} of ${current.item.generatorCount}`;
  imageViewerDimensions.textContent = `${entry.image.naturalWidth}×${entry.image.naturalHeight}`;
  imageViewerContentAr = entry.image.naturalWidth / entry.image.naturalHeight;
  // Apply this exact item's compare state BEFORE fitting: fitImageViewerWindow
  // reads the stage's `compare` class, so fitting first would size the window
  // for the PREVIOUS paint's mode. That made geometry depend on history —
  // toggling `c` on the same image left a compare-shaped window around a
  // single image, while nav-away-and-back fit fresh and looked different.
  applyImageViewerCompare(current);
  fitImageViewerWindow();
}

function setImageViewerIdentity(item) {
  imageViewerState = {
    jobId: item.jobId,
    generator: item.generator,
    imageIndex: item.imageIndex,
  };
  renderImageViewer();
}

// Both compare panes draw at one shared image height; the wider of the two
// aspect ratios dictates the pane size both images need to reach it.
function imageViewerComparePaneAr() {
  const inputAr = imageViewerInputImage.naturalWidth > 0
    ? imageViewerInputImage.naturalWidth / imageViewerInputImage.naturalHeight
    : imageViewerContentAr;
  return Math.max(inputAr || 1, imageViewerContentAr || 1);
}

// Compare orientation is not a fixed style: side-by-side and stacked are both
// computed for the given box and whichever draws the images larger wins.
// Tall pairs land side by side, wide pairs stack, and mid cases resolve by
// the actual geometry (including the monitor's shape) instead of a threshold.
// Returns the per-image drawn height of the winning orientation.
function imageViewerCompareOrientation(maxWidth, maxHeight) {
  const gap = 4; // matches #image-viewer-stage.compare gap
  const paneAr = imageViewerComparePaneAr();
  const rowHeight = Math.min(maxHeight, (maxWidth - gap) / 2 / paneAr);
  const stackedHeight = Math.min((maxHeight - gap) / 2, maxWidth / paneAr);
  if (stackedHeight > rowHeight) {
    return {
      stacked: true,
      imageHeight: stackedHeight,
      width: stackedHeight * paneAr,
      height: stackedHeight * 2 + gap,
    };
  }
  return {
    stacked: false,
    imageHeight: rowHeight,
    width: rowHeight * paneAr * 2 + gap,
    height: rowHeight,
  };
}

// Shrink-wrapped windows get their orientation from fitImageViewerWindow's
// candidate math. A manually resized (or not-yet-fitted) window keeps its box,
// so decide from the stage's current size instead.
function applyCompareOrientationFromStageBox() {
  if (!imageViewerStage.classList.contains("compare") || !imageViewerContentAr) return;
  const box = imageViewerStage.getBoundingClientRect();
  if (!(box.width > 0) || !(box.height > 0)) return;
  imageViewerStage.classList.toggle(
    "stacked", imageViewerCompareOrientation(box.width, box.height).stacked);
}

// Left pane of the `c` comparison: prefer the decoded full-resolution preload.
// Its exact card thumbnail can identify the same input immediately while a
// first-time background warm finishes; never retain another job's input.
function applyImageViewerCompare(current) {
  // Describe items already show the input image as the stage; comparing it
  // with itself is meaningless, so the mode stays armed but paints nothing.
  const active = !!current && imageViewerCompareInput && current.prompt.hasInput
    && current.item.kind !== "text";
  imageViewerStage.classList.toggle("compare", active);
  imageViewerInputImage.hidden = !active;
  imageViewerInputLabel.hidden = !active;
  imageViewerOutputLabel.hidden = !active;
  if (!active) {
    imageViewerStage.classList.remove("stacked");
    imageViewerInputImage.removeAttribute("src");
    return;
  }
  // fitImageViewerWindow owns the orientation while it controls the window
  // size; a user-pinned window never refits, so orient from its box here.
  if (imageViewerWindow.dataset.userSized) applyCompareOrientationFromStageBox();
  const inputUrl = imageViewerInputUrl(current.item.jobId);
  const cached = imageViewerCache.get(inputUrl);
  if (cached?.ready) {
    if (imageViewerInputImage.getAttribute("src") !== cached.blobUrl) {
      imageViewerInputImage.src = cached.blobUrl;
    }
    return;
  }
  const thumb = findImageViewerInputThumbEl(current.item.jobId);
  const thumbUrl = viewerPreviewThumbUrl(thumb);
  if (thumbUrl) {
    if (imageViewerInputImage.getAttribute("src") !== thumbUrl) {
      imageViewerInputImage.src = thumbUrl;
    }
  } else {
    imageViewerInputImage.removeAttribute("src");
  }
}

function toggleImageViewerCompare() {
  imageViewerCompareInput = !imageViewerCompareInput;
  persistPersonalConfigurationSnapshot();
  renderImageViewer();
}

// The appended text is item-specific chrome: show only the suffix that rode
// with the exact endpoint currently visible. gpt-image-2 additionally keeps
// a visible blank state because omitting its default anti-murk text once went
// unnoticed for two days. A target that received a condensed prompt shows it
// first, because the prompt above is not the text that target received.
function renderImageViewerGuidance(current) {
  const key = current?.item?.generator || "";
  const extraText = current?.prompt?.generatorExtraTexts?.[key] || "";
  const legacyGpt2State = key === "gpt2" ? current?.prompt?.gpt2Guidance : "";
  const legacyGpt2Text = current?.prompt?.gpt2GuidanceText || "";
  const sentText = extraText || (legacyGpt2State === "sent" ? legacyGpt2Text : "");
  const condensed = current?.prompt?.condensedPrompts?.find((entry) => entry.generators.includes(key));
  const parts = [];
  if (condensed) {
    const part = document.createElement("div");
    part.className = "guidance-condensed";
    const lead = document.createElement("strong");
    lead.textContent = `${genLabel(key)} received this ` +
      `${MultiImagePromptLimits.characterCount(condensed.prompt).toLocaleString()}-character condensed prompt ` +
      `from ${promptRewriteModelLabel(condensed.model)}, not the prompt above:`;
    const text = document.createElement("div");
    text.className = "guidance-condensed-text";
    text.textContent = condensed.prompt;
    part.append(lead, text);
    parts.push(part);
  }
  if (sentText) {
    const part = document.createElement("div");
    part.className = "guidance-sent";
    part.textContent = `+ appended extra text for ${genLabel(key)}: ${sentText}`;
    parts.push(part);
  } else if (generatorHasDefaultExtraText(key) &&
      (current?.prompt?.generatorExtraTextsKnown || legacyGpt2State === "off")) {
    const part = document.createElement("div");
    part.className = "guidance-off";
    part.textContent =
      `${genLabel(key)} extra text was blank — anti-murk guidance was not sent with this image`;
    parts.push(part);
  }
  imageViewerGuidance.replaceChildren(...parts);
  imageViewerGuidance.classList.toggle("condensed", !!condensed);
  imageViewerGuidance.hidden = parts.length === 0;
}

function imageViewerIdentityMatches(item) {
  return !!item && !!imageViewerState &&
    imageViewerState.jobId === item.jobId &&
    imageViewerState.generator === item.generator &&
    Number(imageViewerState.imageIndex) === Number(item.imageIndex);
}

function imageViewerActivationButtons() {
  return [imageViewerSetImage, imageViewerSetImagePrompt, imageViewerSetPrompt];
}

function hideImageViewerCopyNote() {
  imageViewerCopyNote.hidden = true;
  imageViewerCopyNote.textContent = "";
  imageViewerCopyNote.classList.remove("err");
}

function renderImageViewerCopyPrompt(prompt) {
  hideImageViewerCopyNote();
  imageViewerCopyWrap.hidden = !prompt;
}

function renderImageViewerActiveActions(target) {
  const item = target?.item;
  const prompt = target?.prompt?.prompt || "";
  imageViewerActiveActions.hidden = !item;
  renderImageViewerCopyPrompt(prompt);
  for (const button of imageViewerActivationButtons()) {
    button.disabled = false;
    button.classList.remove("pending", "error", "success");
  }
  imageViewerSetImage.textContent = "set image active";
  imageViewerSetImage.title =
    "Replace the composer input with this image; keep the current composer prompt";
  imageViewerSetImagePrompt.textContent = "set image + prompt active";
  imageViewerSetImagePrompt.title =
    "Replace the composer input with this image and replace the composer prompt with this image's prompt";
  imageViewerSetPrompt.textContent = "set as active prompt";
  imageViewerSetPrompt.title =
    "Replace the composer prompt with this image's prompt; keep the current composer attachments";
  imageViewerSetPrompt.disabled = !prompt;
}

function applyViewedPromptToComposer(prompt) {
  MultiImageTextEntry.replace(promptBox, prompt);
  if (mcpheeCtl) mcpheeCtl.refresh();
  if (mcpheePanel && !mcpheePanelContainer.hidden) mcpheePanel.refresh();
}

let imageViewerCopyNoteTimer = null;
async function copyImageViewerPrompt() {
  const current = locateImageViewerState(getImageViewerPrompts());
  const prompt = current?.prompt?.prompt;
  if (!prompt) {
    imageViewerCopyNote.textContent = "copy failed";
    imageViewerCopyNote.classList.add("err");
    imageViewerCopyNote.hidden = false;
    return;
  }
  try {
    await navigator.clipboard.writeText(prompt);
    imageViewerCopyNote.textContent = "prompt copied";
    imageViewerCopyNote.classList.remove("err");
  } catch {
    imageViewerCopyNote.textContent = "copy failed";
    imageViewerCopyNote.classList.add("err");
  }
  imageViewerCopyNote.hidden = false;
  if (imageViewerCopyNoteTimer) clearTimeout(imageViewerCopyNoteTimer);
  imageViewerCopyNoteTimer = setTimeout(() => { hideImageViewerCopyNote(); }, 1600);
}

// These controls set only the fields they name. Image actions replace every
// current composer image with the exact viewed original. Prompt actions
// replace the composer prompt with this item's prompt. Generator selection
// and output options remain untouched. A later click supersedes an earlier
// in-flight fetch by identity.
async function setViewedImageActive(includePrompt) {
  const current = locateImageViewerState(getImageViewerPrompts());
  if (!current) return;

  const item = { ...current.item };
  const prompt = current.prompt.prompt;
  const selectedButton = includePrompt ? imageViewerSetImagePrompt : imageViewerSetImage;
  const operationVersion = ++imageViewerActivationVersion;
  if (imageViewerActivationController) imageViewerActivationController.abort();
  const controller = new AbortController();
  imageViewerActivationController = controller;

  for (const button of imageViewerActivationButtons()) {
    button.disabled = true;
    button.classList.remove("error", "success");
    button.classList.add("pending");
  }
  selectedButton.textContent = "setting…";

  try {
    // Set-active reuses the one original loader: the viewed original is
    // usually already in flight (or decoded) there, and a second parallel
    // fetch() of the same URL is exactly what the pipeline contract forbids.
    const originalUrl = apiUrl(item.url);
    let blob;
    try {
      const entry = loadImageViewerEntry(originalUrl, 0);
      await entry.promise;
      const blobResponse = await fetch(entry.blobUrl, { signal: controller.signal });
      blob = await blobResponse.blob();
    } catch (error) {
      if (controller.signal.aborted || error?.name !== "AbortError") throw error;
      // Navigation discarded the shared cache entry, so no GET of this URL
      // remains in flight; this direct fetch is the single download.
      const response = await fetch(originalUrl, {
        signal: controller.signal,
        cache: "force-cache",
      });
      if (!response.ok) throw new Error(`image fetch returned HTTP ${response.status}`);
      blob = await blobResponse.blob();
      if (!blob.type.startsWith("image/")) {
        throw new Error(`image fetch returned ${blob.type || "an unknown content type"}`);
      }
    }
    if (operationVersion !== imageViewerActivationVersion) return;

    await setImagesFromBlobs([blob]);
    if (includePrompt) applyViewedPromptToComposer(prompt);
    sendError.textContent = "";

    if (imageViewerIdentityMatches(item)) {
      selectedButton.classList.remove("pending");
      selectedButton.classList.add("success");
      selectedButton.textContent = includePrompt ? "image + prompt active" : "image active";
      selectedButton.title = includePrompt
        ? "This image and prompt are now active in the composer"
        : "This image is now active in the composer; the composer prompt was left unchanged";
    }
  } catch (error) {
    if (error && error.name === "AbortError") return;
    if (operationVersion !== imageViewerActivationVersion) return;
    if (imageViewerIdentityMatches(item)) {
      selectedButton.classList.remove("pending");
      selectedButton.classList.add("error");
      selectedButton.textContent = "set active failed";
      selectedButton.title = String(error);
    }
  } finally {
    if (operationVersion !== imageViewerActivationVersion) return;
    imageViewerActivationController = null;
    if (!imageViewerIdentityMatches(item)) return;
    for (const button of imageViewerActivationButtons()) {
      button.disabled = false;
      button.classList.remove("pending");
    }
    setTimeout(() => {
      if (operationVersion === imageViewerActivationVersion &&
          imageViewerIdentityMatches(item)) {
        renderImageViewerActiveActions(current);
      }
    }, 1800);
  }
}

function setViewedPromptActive() {
  const current = locateImageViewerState(getImageViewerPrompts());
  if (!current) return;
  const item = { ...current.item };
  const prompt = current.prompt.prompt;
  if (!prompt) return;
  const operationVersion = ++imageViewerActivationVersion;
  if (imageViewerActivationController) imageViewerActivationController.abort();
  imageViewerActivationController = null;

  for (const button of imageViewerActivationButtons()) {
    button.disabled = true;
    button.classList.remove("error", "success");
    button.classList.add("pending");
  }
  imageViewerSetPrompt.textContent = "setting…";
  applyViewedPromptToComposer(prompt);
  sendError.textContent = "";
  if (imageViewerIdentityMatches(item)) {
    imageViewerSetPrompt.classList.remove("pending");
    imageViewerSetPrompt.classList.add("success");
    imageViewerSetPrompt.textContent = "prompt active";
    imageViewerSetPrompt.title =
      "This prompt is now active in the composer; composer attachments were left unchanged";
  }
  for (const button of imageViewerActivationButtons()) {
    button.disabled = false;
    button.classList.remove("pending");
  }
  setTimeout(() => {
    if (operationVersion === imageViewerActivationVersion &&
        imageViewerIdentityMatches(item)) {
      renderImageViewerActiveActions(current);
    }
  }, 1800);
}

imageViewerCopyPrompt.addEventListener("click", () => copyImageViewerPrompt());
imageViewerSetImage.addEventListener("click", () => setViewedImageActive(false));
imageViewerSetImagePrompt.addEventListener("click", () => setViewedImageActive(true));
imageViewerSetPrompt.addEventListener("click", () => setViewedPromptActive());

// Item-specific chrome (prompt, guidance, describe panel, generator name).
// Callers pair this with same-item stage pixels only — never with another
// item's media.
function paintImageViewerChrome(target) {
  const previous = imageViewerPaintedItem;
  imageViewerPaintedItem = {
    jobId: target.item.jobId,
    generator: target.item.generator,
    imageIndex: target.item.imageIndex,
  };
  // A different item's chrome starts at its top, where the generator and place
  // in the list identify it. The same item keeps the reader's scroll position
  // when its original replaces the preview.
  if (previous?.jobId !== target.item.jobId || previous?.generator !== target.item.generator
      || previous?.imageIndex !== target.item.imageIndex) {
    imageViewerStatus.scrollTop = 0;
    imageViewerPrompt.scrollTop = 0;
    imageViewerDescribe.scrollTop = 0;
  }
  scheduleImageViewerPreloadHud();
  imageViewerPrompt.textContent = target.prompt.prompt;
  renderImageViewerGuidance(target);
  renderImageViewerActiveActions(target);
  renderImageViewerFavorite(target.item);
  renderImageViewerVideo(target.item);
  renderImageViewerHide(target.item);
  renderImageViewerShareLink(target.item);
  renderImageViewerVibecoders(target.item);
  renderImageViewerPosition(target.item);
  // Describe items: the stage IS the submitted image; the panel above the
  // prompt carries the returned description (plus the model's separated
  // comments, when any), and the header says so.
  const isText = target.item.kind === "text";
  imageViewerDescribe.hidden = !isText;
  imageViewerDescribe.replaceChildren();
  if (isText) {
    const descriptionDiv = document.createElement("div");
    descriptionDiv.textContent = target.item.describeText;
    imageViewerDescribe.appendChild(descriptionDiv);
    if (target.item.describeComments) {
      const commentsDiv = document.createElement("div");
      commentsDiv.className = "viewer-describe-comments";
      commentsDiv.textContent = `model comments: ${target.item.describeComments}`;
      imageViewerDescribe.appendChild(commentsDiv);
    }
  }
  const generatorText = isText
    ? `${genLabel(target.item.generator)} — description of the submitted image`
    : genLabel(target.item.generator);
  imageViewerGenerator.textContent = target.prompt.splitLabel
    ? `${generatorText} · ${target.prompt.splitLabel}`
    : generatorText;
}

// The selected item's card thumb (≤640px) plus its chrome. Changing
// `#image-viewer-image`.src keeps the previous file's pixels until the next
// file decodes, so a Left/Right to an unloaded neighbor would still show
// the old image. Blank the src first. If this item has no decoded thumb
// yet, a wordless pulsing fill occupies the stage until its own pixels
// exist. The fill has no caption: chrome (place, generator, prompt) already
// identifies the new selection.
function paintImageViewerPreview(current) {
  const thumb = findViewerPreviewThumb(current.item);
  const thumbUrl = current.item.thumbUrl || viewerPreviewThumbUrl(thumb);
  const thumbReady = !!(thumb && thumb.complete && thumb.naturalWidth > 0);
  const token = ++imageViewerPreviewLoadToken;
  paintImageViewerChrome(current);
  imageViewerImage.removeAttribute("src");
  imageViewerImage.alt =
    `preview, not full resolution: ${current.item.generator} image ${current.item.imageIndex + 1}`;

  if (thumbReady) {
    setImageViewerAwaitingPixels(false);
    setImageViewerPreviewNotFinal(true);
    imageViewerImage.src = thumb.currentSrc || thumb.src;
    imageViewerContentAr = thumb.naturalWidth / thumb.naturalHeight;
    imageViewerDimensions.textContent = "preview — loading full resolution…";
  } else {
    setImageViewerPreviewNotFinal(false);
    setImageViewerAwaitingPixels(true);
    imageViewerDimensions.textContent = "";
    if (thumbUrl) {
      const onThumbPixels = () => {
        if (token !== imageViewerPreviewLoadToken || imageViewer.hidden) return;
        if (!(imageViewerImage.naturalWidth > 0)) return;
        setImageViewerAwaitingPixels(false);
        setImageViewerPreviewNotFinal(true);
        imageViewerContentAr =
          imageViewerImage.naturalWidth / imageViewerImage.naturalHeight;
        imageViewerDimensions.textContent = "preview — loading full resolution…";
        fitImageViewerWindow();
      };
      imageViewerImage.addEventListener("load", onThumbPixels, { once: true });
      imageViewerImage.src = thumbUrl;
    }
  }
  applyImageViewerCompare(current);
  if (imageViewerContentAr) fitImageViewerWindow();
}

// A hidden viewer is not allowed to retain presentable content. A later open
// may wait on the network/decode path, and exposing the old image's chrome
// during that wait falsely associates it with the newly selected image.
function clearImageViewerPresentation() {
  imageViewerPaintedItem = null;
  hideImageViewerPreloadHud();
  setImageViewerAwaitingPixels(false);
  setImageViewerPreviewNotFinal(false);
  imageViewerImage.removeAttribute("src");
  imageViewerImage.alt = "";
  imageViewerPrompt.textContent = "";
  imageViewerDescribe.hidden = true;
  imageViewerDescribe.textContent = "";
  renderImageViewerGuidance(null);
  imageViewerGenerator.textContent = "";
  imageViewerDimensions.textContent = "";
  renderImageViewerPosition(null);
  renderImageViewerActiveActions(null);
  renderImageViewerFavorite(null);
  renderImageViewerVideo(null);
  renderImageViewerHide(null);
  renderImageViewerShareLink(null);
  renderImageViewerVibecoders(null);
  imageViewerContentAr = null;
  applyImageViewerCompare(null);
}

async function renderImageViewer() {
  if (imageViewer.hidden || !imageViewerState) return;
  const prompts = getImageViewerPrompts();
  const current = locateImageViewerState(prompts);
  if (!current) {
    imageViewerImage.removeAttribute("src");
    imageViewerPrompt.textContent = "";
    imageViewerDescribe.hidden = true;
    imageViewerDescribe.textContent = "";
    renderImageViewerGuidance(null);
    renderImageViewerActiveActions(null);
    renderImageViewerFavorite(null);
    renderImageViewerVideo(null);
    renderImageViewerHide(null);
    renderImageViewerShareLink(null);
    renderImageViewerVibecoders(null);
    imageViewerGenerator.textContent = "selected image is no longer available";
    imageViewerDimensions.textContent = "";
    renderImageViewerPosition(null);
    applyImageViewerCompare(null);
    imageViewerPaintedItem = null;
    hideImageViewerPreloadHud();
    setImageViewerPreviewNotFinal(false);
    setImageViewerAwaitingPixels(false);
    return;
  }

  const version = ++imageViewerRenderVersion;
  scheduleImageViewerPreloadHud();
  // Queue the entire range now. Paint the selected item's own preview
  // while its original downloads alongside its neighbors.
  const currentEntry = prepareImageViewerWindow(prompts, current);

  const paint = (entry) => {
    if (version !== imageViewerRenderVersion || imageViewer.hidden) return false;
    const latest = locateImageViewerState(getImageViewerPrompts());
    if (!latest ||
        latest.item.jobId !== current.item.jobId ||
        latest.item.generator !== current.item.generator ||
        latest.item.imageIndex !== current.item.imageIndex) return false;
    paintImageViewerChrome(latest);
    showImageViewerEntry(latest, entry); // applies compare state, then fits
    markImageViewed(latest.item);
    if (imageViewerCompareInput && latest.prompt.hasInput) {
      const inputEntry = imageViewerCache.get(imageViewerInputUrl(latest.item.jobId));
      if (inputEntry && !inputEntry.ready) {
        inputEntry.promise.then(() => {
          if (version !== imageViewerRenderVersion || imageViewer.hidden) return;
          const still = locateImageViewerState(getImageViewerPrompts());
          if (still) {
            applyImageViewerCompare(still);
            fitImageViewerWindow();
          }
        }).catch(() => {});
      }
    }
    return true;
  };

  if (currentEntry.ready) {
    paint(currentEntry);
    return;
  }

  paintImageViewerPreview(current);

  try {
    const entry = await currentEntry.promise;
    paint(entry);
  } catch (error) {
    if (version !== imageViewerRenderVersion || imageViewer.hidden) return;
    if (error && error.name === "AbortError") return;
    imageViewerDimensions.textContent = `load failed: ${error}`;
  }
}

function recordImageViewerNavigation(direction, kind) {
  const normalized = Math.sign(direction);
  if (normalized === 0) {
    imageViewerNavDirection = 0;
    imageViewerNavKind = "image";
    imageViewerNavStreak = 0;
    return;
  }
  imageViewerNavStreak = imageViewerNavDirection === normalized
    ? Math.min(8, imageViewerNavStreak + 1)
    : 1;
  imageViewerNavDirection = normalized;
  imageViewerNavKind = kind;
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
  recordImageViewerNavigation(delta, "image");
  setImageViewerIdentity(allItems[targetIndex]);
}

function navigateImageViewerAbsolute(index) {
  const allItems = getImageViewerFlatItems();
  if (allItems.length === 0) return;
  const targetIndex = index < 0 ? allItems.length - 1 : index;
  if (targetIndex < 0 || targetIndex >= allItems.length) return;
  recordImageViewerNavigation(0, "image");
  setImageViewerIdentity(allItems[targetIndex]);
}

function findImageViewerFlatIndex(allItems, item) {
  if (!item) return -1;
  return allItems.findIndex((candidate) =>
    candidate.jobId === item.jobId &&
    candidate.generator === item.generator &&
    candidate.imageIndex === item.imageIndex);
}

// Place-in-list indicator: a stable, 1-based current / total reading for the
// exact visible walk. It is painted with the same item-specific chrome as the
// decoded media, so the number can never advance ahead of the pixels.
function renderImageViewerPosition(item) {
  const allItems = item ? getImageViewerFlatItems() : [];
  const index = findImageViewerFlatIndex(allItems, item);
  if (index < 0) {
    imageViewerPosition.textContent = "";
    imageViewerPosition.removeAttribute("aria-label");
    return;
  }
  imageViewerPosition.textContent = `${index + 1} / ${allItems.length}`;
  imageViewerPosition.setAttribute(
    "aria-label",
    `item ${index + 1} of ${allItems.length} in the visible gallery`);
}

function imageViewerPreloadStatus(url) {
  const entry = imageViewerCache.get(url);
  if (!entry) return "idle";
  if (entry.ready) return "ready";
  if (entry.acquired || entry.fetched) return "loading";
  return "queued";
}

function hideImageViewerPreloadHud() {
  imageViewerPreload.hidden = true;
  imageViewerPreload.replaceChildren();
}

function scheduleImageViewerPreloadHud() {
  if (imageViewerPreloadHudScheduled) return;
  imageViewerPreloadHudScheduled = true;
  // Microtask, not rAF: background tabs throttle animation frames, and this
  // HUD must update while the current image's fetch is still in flight.
  queueMicrotask(() => {
    imageViewerPreloadHudScheduled = false;
    renderImageViewerPreloadHud();
  });
}

// Sequential preload runway around the painted image. Neighbor ticks are
// abstract status marks for the ±10 working set. The numbered current tick
// is the viewed item, including a labeled card-thumb preview. This strip
// is not item chrome: the place-in-list counter still swaps atomically
// with the visible media.
function renderImageViewerPreloadHud() {
  if (imageViewer.hidden) {
    hideImageViewerPreloadHud();
    return;
  }
  const allItems = getImageViewerFlatItems();
  const viewed = imageViewerPaintedItem || imageViewerState;
  const viewedIndex = findImageViewerFlatIndex(allItems, viewed);
  if (viewedIndex < 0) {
    hideImageViewerPreloadHud();
    return;
  }
  const start = Math.max(0, viewedIndex - ImageViewerPreloadBehind);
  const end = Math.min(allItems.length - 1, viewedIndex + ImageViewerPreloadAhead);
  const viewedKey = viewerSeenKeyFor(viewed.jobId, viewed.generator, viewed.imageIndex);
  const selectedKey = imageViewerState
    ? viewerSeenKeyFor(
        imageViewerState.jobId, imageViewerState.generator, imageViewerState.imageIndex)
    : "";
  const fragment = document.createDocumentFragment();
  for (let i = start; i <= end; i++) {
    const item = allItems[i];
    const tick = document.createElement("span");
    tick.className = `image-viewer-preload-tick ${imageViewerPreloadStatus(item.url)}`;
    const key = viewerSeenKeyFor(item.jobId, item.generator, item.imageIndex);
    if (key === viewedKey) {
      tick.classList.add("current");
      tick.textContent = String(i + 1);
    }
    if (selectedKey && selectedKey !== viewedKey && key === selectedKey) {
      tick.classList.add("pending");
    }
    fragment.appendChild(tick);
  }
  imageViewerPreload.replaceChildren(fragment);
  imageViewerPreload.hidden = false;
}

// Halfway jump: move halfway through the remaining distance to an endpoint.
// Repetition converges exponentially (1/2, 3/4, 7/8...) while floor/ceil make
// the final press reach the exact first/last item instead of stalling beside it.
function navigateImageViewerHalfway(direction) {
  const allItems = getImageViewerFlatItems();
  const prompts = getImageViewerPrompts();
  const current = locateImageViewerState(prompts);
  if (!current || allItems.length === 0) return;
  const currentIndex = findImageViewerFlatIndex(allItems, current.item);
  if (currentIndex < 0) return;
  const lastIndex = allItems.length - 1;
  const targetIndex = direction < 0
    ? Math.floor(currentIndex / 2)
    : Math.ceil((currentIndex + lastIndex) / 2);
  if (targetIndex === currentIndex) return;
  recordImageViewerNavigation(direction, "image");
  setImageViewerIdentity(allItems[targetIndex]);
}

function navigateImageViewerPrompt(delta) {
  const prompts = getImageViewerPrompts();
  const current = locateImageViewerState(prompts);
  if (!current) return;
  const targetPrompt = prompts[current.promptIndex + delta];
  // At either boundary, keep the current prompt and return to its first image.
  // Prompt navigation never guesses a different destination.
  recordImageViewerNavigation(delta, "prompt");
  setImageViewerIdentity((targetPrompt || current.prompt).items[0]);
}

function hideImageViewerHelp() {
  imageViewerHelpOpen = false;
  imageViewerHelp.hidden = true;
}

function showImageViewerHelp() {
  imageViewerHelpList.textContent = "";
  for (const command of ImageViewerCommands) {
    // help may be a function so entries can report live state (e.g. the
    // close-handback toggle shows its current ON/OFF).
    const helpText = typeof command.help === "function" ? command.help() : command.help;
    if (!helpText) continue;
    const item = document.createElement("li");
    const keys = document.createElement("kbd");
    keys.textContent = command.keys.join(" / ");
    const label = document.createElement("span");
    label.className = "image-viewer-help-command";
    const name = document.createElement("strong");
    name.textContent = command.name;
    const explanation = document.createElement("span");
    explanation.textContent = helpText;
    label.append(name, explanation);
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
    id: "positionIndicator",
    keys: ["Always visible"],
    name: "Place-in-list indicator",
    match: () => false,
    help: "The 1-based current / total count identifies your exact place in the visible gallery.",
    run: () => {},
  },
  {
    id: "wheelStep",
    keys: ["Wheel"],
    name: "Intent-filtered wheel stepping",
    match: () => false,
    help: "Vertical wheel movement over the image advances one item after a short threshold so trackpad noise does not skip. Ctrl+wheel jumps to the first image of the next prompt, matching Ctrl+Left/Right. A wheel without Ctrl over a prompt, description, or other viewer box that already has a vertical scrollbar scrolls that box only and does not change the image.",
    run: () => {},
  },
  {
    id: "mouseSideStep",
    keys: ["Mouse Back", "Mouse Forward"],
    name: "Mouse side-button stepping",
    match: () => false,
    help: "A mouse's Back or Forward thumb button selects the previous or next image without changing browser history.",
    run: () => {},
  },
  {
    id: "swipeStep",
    keys: ["Swipe left", "Swipe right"],
    name: "Touch swipe stepping",
    match: () => false,
    help: "On a touch screen, swipe the image left for the next item or right for the previous item. Pinch zoom works normally. While the page is zoomed in, a swipe pans instead.",
    run: () => {},
  },
  {
    id: "previous",
    keys: ["Left", "Up"],
    name: "Single-step traversal",
    match: (event) =>
      !event.ctrlKey && !event.metaKey &&
      (event.key === "ArrowLeft" || event.key === "ArrowUp"),
    help: "Select the previous image in the visible gallery, crossing prompt boundaries. The card preview appears immediately.",
    run: () => navigateImageViewerImage(-1),
  },
  {
    id: "next",
    keys: ["Right", "Down"],
    name: "Single-step traversal",
    match: (event) =>
      !event.ctrlKey && !event.metaKey &&
      (event.key === "ArrowRight" || event.key === "ArrowDown"),
    help: "Select the next image in the visible gallery, crossing prompt boundaries. The card preview appears immediately.",
    run: () => navigateImageViewerImage(1),
  },
  {
    id: "newerPrompt",
    keys: ["Ctrl+Left", "Ctrl+Wheel up"],
    name: "Prompt-boundary jump",
    match: (event) =>
      (event.ctrlKey || event.metaKey) && event.key === "ArrowLeft",
    help: "Select the first image belonging to the newer prompt.",
    run: () => navigateImageViewerPrompt(-1),
  },
  {
    id: "olderPrompt",
    keys: ["Ctrl+Right", "Ctrl+Wheel down"],
    name: "Prompt-boundary jump",
    match: (event) =>
      (event.ctrlKey || event.metaKey) && event.key === "ArrowRight",
    help: "Select the first image belonging to the older prompt.",
    run: () => navigateImageViewerPrompt(1),
  },
  {
    id: "pageBack",
    keys: ["PageUp"],
    name: "Fixed-distance jump",
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "PageUp",
    help: `Move ${ImageViewerPageJumpSize} images toward the beginning of the visible gallery.`,
    run: () => navigateImageViewerImage(-ImageViewerPageJumpSize),
  },
  {
    id: "pageForward",
    keys: ["PageDown"],
    name: "Fixed-distance jump",
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "PageDown",
    help: `Move ${ImageViewerPageJumpSize} images toward the end of the visible gallery.`,
    run: () => navigateImageViewerImage(ImageViewerPageJumpSize),
  },
  {
    id: "halfwayBack",
    keys: ["Ctrl+PageUp"],
    name: "Halfway jump",
    match: (event) =>
      (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey &&
      event.key === "PageUp",
    help: "Move halfway through the remaining distance to the first item; repeated presses rapidly narrow a long gallery.",
    run: () => navigateImageViewerHalfway(-1),
  },
  {
    id: "halfwayForward",
    keys: ["Ctrl+PageDown"],
    name: "Halfway jump",
    match: (event) =>
      (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey &&
      event.key === "PageDown",
    help: "Move halfway through the remaining distance to the last item; repeated presses rapidly narrow a long gallery.",
    run: () => navigateImageViewerHalfway(1),
  },
  {
    id: "first",
    keys: ["Home"],
    name: "Endpoint jump",
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "Home",
    help: "Select the first item in the visible gallery.",
    run: () => navigateImageViewerAbsolute(0),
  },
  {
    id: "last",
    keys: ["End"],
    name: "Endpoint jump",
    match: (event) => !event.ctrlKey && !event.metaKey && event.key === "End",
    help: "Select the last item in the visible gallery.",
    run: () => navigateImageViewerAbsolute(-1),
  },
  {
    id: "compareInput",
    keys: ["c"],
    name: "Sticky input comparison",
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey &&
      (event.key === "c" || event.key === "C"),
    help: "Keep input/output comparison armed while traversing; jobs without an input remain single-image. The two panes sit side by side or stack vertically, whichever draws the images larger for their shape.",
    run: () => toggleImageViewerCompare(),
  },
  {
    id: "favorite",
    keys: ["v"],
    name: "Shared exact-image favorite",
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey &&
      event.key === "v",
    help: "Add or remove your persistent favorite for this exact job, generator, and image index.",
    run: () => toggleImageViewerFavorite(),
  },
  {
    id: "fullscreen",
    keys: ["f"],
    name: "Fullscreen inspection",
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey &&
      event.key.toLowerCase() === "f",
    help: "Use the full monitor while preserving the viewer's adaptive image and information layout.",
    run: () => toggleImageViewerFullscreen(),
  },
  {
    id: "returnSync",
    keys: ["s"],
    name: "Close handback",
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey &&
      (event.key === "s" || event.key === "S"),
    help: () =>
      `Currently ${imageViewerReturnSync ? "ON" : "OFF"}. When ON, ` +
      "closing the viewer scrolls the page to the image you were viewing and focuses it; " +
      "when OFF, the page stays where it was",
    run: () => {
      setViewerReturnSync(!imageViewerReturnSync);
      // Refresh the ON/OFF readout in place when toggled from the help list.
      if (imageViewerHelpOpen) showImageViewerHelp();
    },
  },
  {
    id: "help",
    keys: ["?", "/"],
    name: "Control glossary",
    match: (event) =>
      !event.ctrlKey && !event.metaKey && !event.altKey &&
      (event.key === "?" || event.key === "/"),
    help: "Show or hide these named viewer interactions and their exact behavior.",
    run: () => toggleImageViewerHelp(),
  },
  {
    id: "historyBack",
    keys: ["Back"],
    name: "Back-button exit",
    match: () => false,
    help: "The phone or browser Back control closes the viewer. The page stays open at the same place.",
    run: () => {},
  },
  {
    id: "close",
    keys: ["Escape"],
    name: "Layered exit",
    match: (event) => event.key === "Escape",
    help: "Close the glossary first, then fullscreen, then the viewer on successive presses.",
    run: () => {
      if (imageViewerHelpOpen) hideImageViewerHelp();
      // While fullscreen, Esc only exits fullscreen (the browser does this
      // natively too); the viewer stays open. A second Esc closes it.
      else if (document.fullscreenElement === imageViewer) document.exitFullscreen();
      else closeImageViewer();
    },
  },
];

// Fullscreen covers the whole monitor with the viewer overlay; entering and
// exiting both fire a window resize, so fitImageViewerWindow re-shrink-wraps
// the window to the new viewport automatically.
async function toggleImageViewerFullscreen() {
  if (document.fullscreenElement) {
    await document.exitFullscreen();
    return;
  }
  // Entering fullscreen means "draw this as large as the screen allows", so
  // a prior manual drag-resize stops pinning the window size.
  delete imageViewerWindow.dataset.userSized;
  await imageViewer.requestFullscreen();
}

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
// The info panel (generator, resolution, prompt, guidance) goes wherever it
// costs the image the least: below the stage, or as a fixed-width right
// column when that draws the image larger — typical for wide images and for
// compare mode, where the doubled stage makes height the binding constraint.
const ImageViewerSideStatusWidth = 280; // matches the .side-status grid column in style.css
// Must match the phone media query in style.css.
const PhoneLayoutQuery = window.matchMedia("(max-width: 700px), (pointer: coarse) and (max-height: 500px)");

// Phone screens are smaller than the desktop minimum window. Matches the
// #image-viewer-window minimums in style.css.
function imageViewerMinimumSize() {
  return PhoneLayoutQuery.matches ? { width: 300, height: 260 } : { width: 440, height: 320 };
}

function fitImageViewerWindow() {
  if (imageViewer.hidden || imageViewerWindow.dataset.userSized || !imageViewerContentAr) return;
  const margin = 16;
  const gap = 4; // matches #image-viewer-stage.compare gap
  // The window never extends past the screen.
  const minimum = imageViewerMinimumSize();
  const minWidth = Math.min(minimum.width, window.innerWidth - margin * 2);
  const minHeight = Math.min(minimum.height, window.innerHeight - margin * 2);
  const availWidth = Math.max(minWidth, window.innerWidth - margin * 2);
  const availHeight = Math.max(minHeight, window.innerHeight - margin * 2);
  const stageSizeWithin = (maxWidth, maxHeight) => {
    if (imageViewerStage.classList.contains("compare")) {
      // Both orientations are computed and the larger drawn image wins —
      // see imageViewerCompareOrientation. imageHeight is the comparison
      // metric because in stacked mode it is half the stage, not equal to it.
      return imageViewerCompareOrientation(maxWidth, maxHeight);
    }
    const height = Math.min(maxHeight, maxWidth / imageViewerContentAr);
    return { width: height * imageViewerContentAr, height, imageHeight: height, stacked: false };
  };
  const placeWindow = (width, height) => {
    imageViewerWindow.style.width = `${width}px`;
    imageViewerWindow.style.height = `${height}px`;
    imageViewerWindow.style.left = `${Math.max(margin, (window.innerWidth - width) / 2)}px`;
    imageViewerWindow.style.top = `${Math.max(margin, (window.innerHeight - height) / 2)}px`;
  };

  // Bottom-bar candidate: the bar's height depends on the window width (the
  // prompt wraps differently as the window narrows), so measure it in bottom
  // mode over two passes. This leaves the bottom layout applied.
  imageViewerWindow.classList.remove("side-status");
  let bottomStage = null;
  for (let pass = 0; pass < 2; pass++) {
    const statusHeight = imageViewerStatus.offsetHeight;
    bottomStage = stageSizeWithin(availWidth, Math.max(120, availHeight - statusHeight));
    placeWindow(
      Math.max(minWidth, Math.min(availWidth, Math.round(bottomStage.width))),
      Math.max(minHeight, Math.min(availHeight, Math.round(bottomStage.height + statusHeight))));
  }

  // Side-column candidate: fixed-width info column, full-height stage.
  // imageHeight is the drawn per-image height in every layout (equal to the
  // stage height except in stacked compare, where it is half), so comparing
  // it compares image scale directly. Ties keep the bottom bar.
  const sideStage = stageSizeWithin(availWidth - ImageViewerSideStatusWidth, availHeight);
  let chosen = bottomStage;
  if (sideStage.imageHeight > bottomStage.imageHeight + 1) {
    chosen = sideStage;
    imageViewerWindow.classList.add("side-status");
    placeWindow(
      Math.max(minWidth, Math.min(availWidth, Math.round(sideStage.width + ImageViewerSideStatusWidth))),
      Math.max(minHeight, Math.min(availHeight, Math.round(sideStage.height))));
  }
  imageViewerStage.classList.toggle("stacked", !!chosen.stacked);
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

// Android Back, the iOS edge swipe, and the browser Back button close the
// viewer instead of leaving the page. Opening adds one same-URL history entry,
// and closing any other way removes it again. Scroll restoration stays manual
// until that entry is gone, so returning to the page entry cannot undo the
// close handback scroll.
let imageViewerHistoryEntry = false;
let imageViewerSavedScrollRestoration = null;

function addImageViewerHistoryEntry() {
  if (imageViewerHistoryEntry) return;
  if (imageViewerSavedScrollRestoration === null) {
    imageViewerSavedScrollRestoration = history.scrollRestoration;
  }
  history.scrollRestoration = "manual";
  history.pushState({ micImageViewer: true }, "");
  imageViewerHistoryEntry = true;
}

function releaseImageViewerHistoryEntry() {
  if (!imageViewerHistoryEntry) return;
  imageViewerHistoryEntry = false;
  if (history.state?.micImageViewer) history.back();
}

window.addEventListener("popstate", () => {
  if (imageViewerSavedScrollRestoration !== null && !history.state?.micImageViewer) {
    history.scrollRestoration = imageViewerSavedScrollRestoration;
    imageViewerSavedScrollRestoration = null;
  }
  if (!imageViewerHistoryEntry) return;
  imageViewerHistoryEntry = false;
  if (!imageViewer.hidden) closeImageViewer();
});

function openImageViewer(link) {
  imageViewerFocusBeforeOpen =
    document.activeElement instanceof HTMLElement ? document.activeElement : null;
  // Clear while still hidden. The selected item's card thumb and chrome
  // paint immediately; the original swaps in when it is decoded.
  clearImageViewerPresentation();
  imageViewerState = {
    jobId: link.dataset.jobId,
    generator: link.dataset.generator,
    imageIndex: Number(link.dataset.imageIndex),
  };
  recordImageViewerNavigation(0, "image");
  hideImageViewerHelp();
  imageViewerWheelAccumulator = 0;
  imageViewer.hidden = false;
  document.body.classList.add("image-viewer-open");
  addImageViewerHistoryEntry();
  // A manual drag-resize takes over until reload. Otherwise the very first
  // open pre-sizes to the viewport (content unknown while loading); later
  // opens keep the previous shrink-wrapped size until the new image decodes
  // and fitImageViewerWindow refits, avoiding a full-screen flash.
  if (imageViewerWindow.dataset.userSized) clampImageViewerWindow();
  else if (!imageViewerWindow.style.width) sizeImageViewerWindow();
  else clampImageViewerWindow();
  renderImageViewer();
  imageViewerWindow.focus({ preventScroll: true });
}

function closeImageViewer() {
  hideImageViewerHelp();
  if (document.fullscreenElement === imageViewer) document.exitFullscreen();
  // Resolve the departed image's outer-page anchor BEFORE clearing state: the
  // departure pulse (always) and the close handback (opt-in, `s` / settings)
  // both target it.
  const departedAnchor = findViewerAnchor(imageViewerState);
  imageViewer.hidden = true;
  document.body.classList.remove("image-viewer-open");
  releaseImageViewerHistoryEntry();
  imageViewerState = null;
  imageViewerRenderVersion++;
  imageViewerWheelAccumulator = 0;
  recordImageViewerNavigation(0, "image");
  clearImageViewerPresentation();
  abortImageViewerInFlight();
  for (const preview of imageViewerPreviewCache.values()) preview.removeAttribute("src");
  imageViewerPreviewCache.clear();
  trimImageViewerReadyCache();
  const restore = imageViewerFocusBeforeOpen;
  imageViewerFocusBeforeOpen = null;
  if (departedAnchor) pulseViewerAnchor(departedAnchor);
  if (imageViewerReturnSync && departedAnchor) {
    // Handback: inner movement implies outer movement. Scroll only when the
    // thumbnail isn't already fully on screen, and move focus to it so the
    // keyboard continues from where the viewer walk ended.
    const rect = departedAnchor.getBoundingClientRect();
    if (rect.top < 0 || rect.bottom > window.innerHeight) {
      departedAnchor.scrollIntoView({ block: "center" });
    }
    departedAnchor.focus({ preventScroll: true });
  } else if (restore && document.contains(restore) && typeof restore.focus === "function") {
    restore.focus({ preventScroll: true });
  }
  if (favoriteBrowseUser !== null &&
      favoriteBrowseUser !== "*" &&
      !favoriteUsers.has(favoriteBrowseUser)) {
    setFavoriteBrowseUser(null);
  } else if (favoritesGalleryRenderPending && favoriteBrowseUser !== null) {
    renderFavoritesGallery();
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

// Hover uses the same original loader as the viewer. One URL identity, one
// in-flight GET. Latest hover wins among unfinished hover-priority fetches.
const HoverPrefetchIntentMs = 150;
let hoverPrefetchTimer = null;

function startHoverPrefetch(urls) {
  for (const [url, entry] of [...imageViewerCache]) {
    if (urls.includes(url)) continue;
    if (entry.priority !== ImageViewerHoverPriority) continue;
    if (entry.fetched || entry.ready) continue;
    discardImageViewerCacheEntry(url, entry);
  }
  for (const url of urls) {
    const existing = imageViewerCache.get(url);
    if (existing?.fetched || existing?.ready) continue;
    loadImageViewerEntry(url, ImageViewerHoverPriority).promise.catch(() => {});
  }
}

// The full set of originals a click on this anchor will paint: the result
// itself, plus — while compare mode is armed and the job carried an input —
// the exact same job's submitted input image, so both compare panes open
// from cache with equal speed.
function hoverPrefetchUrlsFor(link) {
  const urls = [link.href];
  // Describe anchors (resultKind "text") already point at the input image
  // itself; adding the input URL would fetch the same bytes twice.
  if (imageViewerCompareInput &&
      link.dataset.resultKind !== "text" &&
      link.dataset.jobId &&
      link.closest(".job")?.dataset.hasInputImage === "true") {
    urls.push(imageViewerInputUrl(link.dataset.jobId));
  }
  return urls;
}

document.addEventListener("pointerover", (event) => {
  if (!imageViewer.hidden) return;
  const link = event.target.closest('a[data-viewer-image="true"]');
  if (!link) return;
  const urls = hoverPrefetchUrlsFor(link);
  if (urls.every((url) => {
    const entry = imageViewerCache.get(url);
    return !!(entry?.fetched || entry?.ready);
  })) return;
  if (hoverPrefetchTimer) clearTimeout(hoverPrefetchTimer);
  hoverPrefetchTimer = setTimeout(() => {
    hoverPrefetchTimer = null;
    startHoverPrefetch(urls);
  }, HoverPrefetchIntentMs);
});

document.addEventListener("pointerout", (event) => {
  const link = event.target.closest('a[data-viewer-image="true"]');
  if (!link) return;
  const to = event.relatedTarget instanceof Element
    ? event.relatedTarget.closest('a[data-viewer-image="true"]')
    : null;
  if (to === link) return;
  if (hoverPrefetchTimer) {
    clearTimeout(hoverPrefetchTimer);
    hoverPrefetchTimer = null;
  }
});

// Navigation uses keys, normalized wheel intent, optional mouse thumb
// buttons, and touch swipes (see ImageViewerCommands; ? shows the named
// interaction glossary). Closing is Escape, a click outside the window, or Back.
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

function normalizedImageViewerWheelDelta(event) {
  if (event.deltaY === 0 || Math.abs(event.deltaX) > Math.abs(event.deltaY)) return 0;
  // Normalize Firefox line/page wheel units into approximate CSS pixels so
  // one physical gesture has comparable semantics across mice and trackpads.
  const unit = event.deltaMode === 1
    ? 16
    : event.deltaMode === 2
      ? Math.max(1, imageViewerStage.clientHeight || window.innerHeight)
      : 1;
  return event.deltaY * unit;
}

function imageViewerElementHasVerticalScrollbar(node) {
  if (!(node instanceof HTMLElement)) return false;
  const overflowY = getComputedStyle(node).overflowY;
  if (overflowY !== "auto" && overflowY !== "scroll" && overflowY !== "overlay") {
    return false;
  }
  return node.scrollHeight > node.clientHeight + 1;
}

function imageViewerWheelOwnedByInnerScroller(event) {
  let node = event.target instanceof Element
    ? event.target
    : event.target instanceof Node
      ? event.target.parentElement
      : null;
  while (node && node !== imageViewer) {
    if (imageViewerElementHasVerticalScrollbar(node)) return true;
    node = node.parentElement;
  }
  return false;
}

imageViewer.addEventListener("wheel", (event) => {
  if (imageViewer.hidden) return;
  const promptJump = event.ctrlKey || event.metaKey;
  // A prompt, description, guidance, help panel, or side-status column
  // with its own scrollbar owns an unmodified wheel. Ctrl/Meta+wheel is
  // a prompt-boundary jump (same as Ctrl+Left/Right) and must also
  // cancel the browser's zoom default.
  if (!promptJump && imageViewerWheelOwnedByInnerScroller(event)) return;
  event.preventDefault();
  const delta = normalizedImageViewerWheelDelta(event);
  if (delta === 0) return;
  if (imageViewerHelpOpen) hideImageViewerHelp();
  if (imageViewerWheelPromptJump !== promptJump) {
    imageViewerWheelAccumulator = 0;
    imageViewerWheelPromptJump = promptJump;
  }
  imageViewerWheelAccumulator += delta;
  if (imageViewerWheelResetTimer) clearTimeout(imageViewerWheelResetTimer);
  imageViewerWheelResetTimer = setTimeout(() => {
    imageViewerWheelAccumulator = 0;
    imageViewerWheelResetTimer = null;
  }, 280);
  if (Math.abs(imageViewerWheelAccumulator) < ImageViewerWheelThreshold) return;
  const direction = imageViewerWheelAccumulator > 0 ? 1 : -1;
  imageViewerWheelAccumulator = 0;
  if (promptJump) navigateImageViewerPrompt(direction);
  else navigateImageViewerImage(direction);
}, { passive: false });

function isImageViewerSideButton(event) {
  return event.button === 3 || event.button === 4;
}

// Prevent Back/Forward thumb buttons from changing browser history while the
// modal viewer owns the interaction, then map them to exact adjacent items.
imageViewer.addEventListener("mousedown", (event) => {
  if (!imageViewer.hidden && isImageViewerSideButton(event)) event.preventDefault();
});
imageViewer.addEventListener("mouseup", (event) => {
  if (imageViewer.hidden || !isImageViewerSideButton(event)) return;
  event.preventDefault();
  event.stopPropagation();
  if (imageViewerHelpOpen) hideImageViewerHelp();
  navigateImageViewerImage(event.button === 3 ? -1 : 1);
});
imageViewer.addEventListener("auxclick", (event) => {
  if (!imageViewer.hidden && isImageViewerSideButton(event)) event.preventDefault();
});

// A quick, mostly horizontal one-finger swipe on the stage steps one image.
// The listeners are passive, so pinch zoom and panning stay with the browser,
// and a zoomed-in page pans instead of stepping.
const ImageViewerSwipeMinDistance = 48;
const ImageViewerSwipeMaxMs = 700;
let imageViewerSwipe = null;
imageViewerStage.addEventListener("touchstart", (event) => {
  imageViewerSwipe = event.touches.length === 1
    ? { x: event.touches[0].clientX, y: event.touches[0].clientY, at: event.timeStamp }
    : null;
}, { passive: true });
imageViewerStage.addEventListener("touchmove", (event) => {
  if (event.touches.length !== 1) imageViewerSwipe = null;
}, { passive: true });
imageViewerStage.addEventListener("touchcancel", () => {
  imageViewerSwipe = null;
}, { passive: true });
imageViewerStage.addEventListener("touchend", (event) => {
  const start = imageViewerSwipe;
  imageViewerSwipe = null;
  if (!start || imageViewer.hidden || event.touches.length !== 0) return;
  if ((window.visualViewport?.scale ?? 1) > 1.01) return;
  if (event.timeStamp - start.at > ImageViewerSwipeMaxMs) return;
  const touch = event.changedTouches[0];
  const dx = touch.clientX - start.x;
  const dy = touch.clientY - start.y;
  if (Math.abs(dx) < ImageViewerSwipeMinDistance || Math.abs(dx) < Math.abs(dy) * 1.5) return;
  if (imageViewerHelpOpen) hideImageViewerHelp();
  navigateImageViewerImage(dx < 0 ? 1 : -1);
}, { passive: true });

document.addEventListener("keydown", (event) => {
  if (imageViewer.hidden) return;
  // A modal/form layered over the viewer owns its keyboard completely. This
  // is especially important for Make Video: letters such as c/f/s/v and
  // navigation keys must edit/select normally, never operate the viewer
  // behind the dialog.
  const formOwnsKey = event.target instanceof Element &&
    event.target.closest("input, textarea, select, [contenteditable]");
  if (document.querySelector("dialog[open]") || formOwnsKey) {
    return;
  }

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
  // returnSync keeps the help list up so its ON/OFF readout refreshes in place.
  if (imageViewerHelpOpen && !["help", "close", "returnSync"].includes(command.id)) {
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
  const minimum = imageViewerMinimumSize();
  const maximumWidth = window.innerWidth - imageViewerResizeStart.left - 8;
  const maximumHeight = window.innerHeight - imageViewerResizeStart.top - 8;
  const width = Math.max(
    Math.min(minimum.width, maximumWidth),
    Math.min(maximumWidth, imageViewerResizeStart.width + event.clientX - imageViewerResizeStart.pointerX));
  const height = Math.max(
    Math.min(minimum.height, maximumHeight),
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
  if (imageViewerWindow.dataset.userSized || !imageViewerContentAr) {
    clampImageViewerWindow();
    applyCompareOrientationFromStageBox();
  } else {
    fitImageViewerWindow();
  }
});
// The compare pane's input image loads independently of the output; its
// aspect ratio can widen the shrink-wrapped window once known — and can
// change which compare orientation draws larger.
imageViewerInputImage.addEventListener("load", () => {
  if (imageViewerWindow.dataset.userSized) applyCompareOrientationFromStageBox();
  else fitImageViewerWindow();
});


const viewerSeenSet = loadViewerSeen();
