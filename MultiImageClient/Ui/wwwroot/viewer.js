"use strict";
// Shared full-resolution image viewer. A page hands it a source (a function
// returning the current ordered item list) and opens it on an item id; the
// viewer owns its overlay DOM, keyboard/wheel/mouse navigation, preview-first
// atomic painting, bounded preloading, and window fitting. It knows nothing
// about jobs, loops, or any page's layout.
//
//   const viewer = MultiImageViewer.create({
//     items: () => [{ id, url, thumbUrl, width, height, title, subtitle,
//                     prompt, meta: [{ label, value }], actions: [{ label, title, run }] }],
//     resolveUrl: (u) => u,          // page-relative URL resolver
//     onChange: (item, open) => {},  // optional
//   });
//   viewer.open(id); viewer.close(); viewer.isOpen(); viewer.refresh();
//
// Contract (see .cursor/rules/atomic-preloading-viewers.mdc): the visible
// pixels and every item-specific chrome field always describe the same item.
// On every selection the previous pixels are dropped first, the target's own
// card thumb (or a wordless pulsing fill) is painted together with its chrome
// and a "preview / not full resolution" badge, and the original swaps in only
// after it decoded and the selection is still current.

(function publishViewer(root) {
  const PreloadRadius = 10;
  const FetchSlots = 6;
  const KeepDecoded = 24;
  const WheelThreshold = 60;
  const Margin = 16;
  const SideStatusWidth = 280;

  function create(options) {
    if (!options || typeof options.items !== "function") {
      throw new Error("MultiImageViewer.create requires an items() source");
    }
    const source = options.items;
    const resolveUrl = typeof options.resolveUrl === "function" ? options.resolveUrl : (u) => u;
    const onChange = typeof options.onChange === "function" ? options.onChange : () => {};

    // ---- DOM ----
    const overlay = document.createElement("div");
    overlay.className = "miv-overlay";
    overlay.hidden = true;
    overlay.setAttribute("role", "dialog");
    overlay.setAttribute("aria-modal", "true");
    overlay.innerHTML = `
      <div class="miv-window">
        <div class="miv-stage">
          <img class="miv-img" alt="">
          <div class="miv-pulse" hidden></div>
          <div class="miv-badge" hidden>preview / not full resolution</div>
          <div class="miv-failed" hidden></div>
          <div class="miv-place" aria-live="polite"></div>
          <div class="miv-hud" aria-hidden="true"></div>
        </div>
        <div class="miv-status">
          <div class="miv-status-top">
            <div class="miv-title"></div>
            <button type="button" class="miv-copy" title="copy this prompt">copy prompt</button>
          </div>
          <div class="miv-subtitle"></div>
          <div class="miv-actions"></div>
          <div class="miv-dims"></div>
          <pre class="miv-prompt"></pre>
          <dl class="miv-meta"></dl>
          <div class="miv-help-line"><kbd>←</kbd> <kbd>→</kbd> step · <kbd>Home</kbd> <kbd>End</kbd> · wheel · <kbd>f</kbd> fullscreen · <kbd>Esc</kbd> close · <kbd>?</kbd> help</div>
        </div>
        <div class="miv-help" hidden></div>
      </div>`;
    document.body.appendChild(overlay);
    const win = overlay.querySelector(".miv-window");
    const stage = overlay.querySelector(".miv-stage");
    const img = overlay.querySelector(".miv-img");
    const pulse = overlay.querySelector(".miv-pulse");
    const badge = overlay.querySelector(".miv-badge");
    const failed = overlay.querySelector(".miv-failed");
    const place = overlay.querySelector(".miv-place");
    const hud = overlay.querySelector(".miv-hud");
    const titleEl = overlay.querySelector(".miv-title");
    const subtitleEl = overlay.querySelector(".miv-subtitle");
    const actionsEl = overlay.querySelector(".miv-actions");
    const dimsEl = overlay.querySelector(".miv-dims");
    const promptEl = overlay.querySelector(".miv-prompt");
    const metaEl = overlay.querySelector(".miv-meta");
    const helpEl = overlay.querySelector(".miv-help");
    const copyBtn = overlay.querySelector(".miv-copy");

    // ---- state ----
    let open = false;
    let currentId = null;
    let selection = 0;          // bumped on every selection; async completions check it
    let items = [];
    let wheelAccumulator = 0;
    let restoreFocus = null;
    let currentAspect = 1;

    // ---- preload cache: url -> entry ----
    // entry: { status: queued|fetching|decoded|failed, blobUrl, width, height,
    //          controller, promise, priority, lastUsed }
    const cache = new Map();
    let activeFetches = 0;
    const waiters = [];   // [{ priority, entry, resolve }]

    function takeSlot(priority, entry) {
      if (activeFetches < FetchSlots) {
        activeFetches++;
        return Promise.resolve();
      }
      return new Promise((resolve) => {
        waiters.push({ priority, entry, resolve });
        waiters.sort((a, b) => a.priority - b.priority);
      });
    }

    function releaseSlot() {
      const next = waiters.shift();
      if (next) {
        next.resolve();
      } else {
        activeFetches = Math.max(0, activeFetches - 1);
      }
    }

    function discard(url) {
      const entry = cache.get(url);
      if (!entry) return;
      if (entry.controller) entry.controller.abort();
      if (entry.blobUrl) URL.revokeObjectURL(entry.blobUrl);
      cache.delete(url);
    }

    function trimDecoded() {
      const decoded = [...cache.entries()].filter(([, e]) => e.status === "decoded");
      if (decoded.length <= KeepDecoded) return;
      decoded.sort((a, b) => a[1].lastUsed - b[1].lastUsed);
      for (const [url] of decoded.slice(0, decoded.length - KeepDecoded)) discard(url);
    }

    function load(url, priority) {
      const existing = cache.get(url);
      if (existing) {
        if (priority < existing.priority) existing.priority = priority;
        return existing.promise;
      }
      const entry = { status: "queued", blobUrl: null, width: 0, height: 0, controller: null, promise: null, priority, lastUsed: Date.now() };
      cache.set(url, entry);
      entry.promise = (async () => {
        await takeSlot(priority, entry);
        if (!cache.has(url)) { releaseSlot(); throw new Error("discarded"); }
        entry.status = "fetching";
        entry.controller = new AbortController();
        scheduleHud();
        try {
          const resp = await fetch(resolveUrl(url), { signal: entry.controller.signal });
          if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
          const blob = await resp.blob();
          releaseSlot();
          entry.controller = null;
          const blobUrl = URL.createObjectURL(blob);
          const probe = new Image();
          probe.src = blobUrl;
          await probe.decode();
          if (!cache.has(url)) { URL.revokeObjectURL(blobUrl); throw new Error("discarded"); }
          entry.blobUrl = blobUrl;
          entry.width = probe.naturalWidth;
          entry.height = probe.naturalHeight;
          entry.status = "decoded";
          entry.lastUsed = Date.now();
          trimDecoded();
          return entry;
        } catch (error) {
          if (entry.controller) { releaseSlot(); entry.controller = null; }
          entry.status = "failed";
          entry.error = error;
          throw error;
        } finally {
          scheduleHud();
        }
      })();
      entry.promise.catch(() => {});
      return entry.promise;
    }

    function abortUnfinished() {
      for (const [url, entry] of [...cache.entries()]) {
        if (entry.status !== "decoded") discard(url);
      }
      waiters.length = 0;
      activeFetches = 0;
    }

    // ---- items ----
    function refreshItems() {
      const list = source() || [];
      items = list.filter((it) => it && typeof it.id === "string" && typeof it.url === "string");
      return items;
    }

    function currentIndex() {
      return items.findIndex((it) => it.id === currentId);
    }

    function preloadNeighbors(index) {
      for (let d = 1; d <= PreloadRadius; d++) {
        for (const i of [index + d, index - d]) {
          if (i >= 0 && i < items.length) load(items[i].url, d);
        }
      }
    }

    // ---- painting ----
    function clearPresentation() {
      img.removeAttribute("src");
      img.alt = "";
      pulse.hidden = true;
      badge.hidden = true;
      failed.hidden = true;
      failed.textContent = "";
      titleEl.textContent = "";
      subtitleEl.textContent = "";
      actionsEl.textContent = "";
      dimsEl.textContent = "";
      promptEl.textContent = "";
      metaEl.textContent = "";
      place.textContent = "";
      hud.textContent = "";
    }

    function paintChrome(item) {
      titleEl.textContent = item.title || "";
      subtitleEl.textContent = item.subtitle || "";
      promptEl.textContent = item.prompt || "";
      promptEl.hidden = !item.prompt;
      copyBtn.hidden = !item.prompt;
      metaEl.textContent = "";
      for (const m of item.meta || []) {
        if (!m || m.value == null || m.value === "") continue;
        const dt = document.createElement("dt");
        dt.textContent = m.label;
        const dd = document.createElement("dd");
        dd.textContent = String(m.value);
        metaEl.append(dt, dd);
      }
      actionsEl.textContent = "";
      for (const a of item.actions || []) {
        const b = document.createElement("button");
        b.type = "button";
        b.textContent = a.label;
        if (a.title) b.title = a.title;
        b.addEventListener("click", () => a.run(item));
        actionsEl.appendChild(b);
      }
      img.alt = item.title || "image";
    }

    function paintPlace() {
      const index = currentIndex();
      place.textContent = index < 0 ? "" : `${index + 1} / ${items.length}`;
    }

    function paintPreview(item) {
      img.removeAttribute("src");
      failed.hidden = true;
      if (item.thumbUrl) {
        img.src = resolveUrl(item.thumbUrl);
        pulse.hidden = true;
        badge.hidden = false;
      } else {
        pulse.hidden = false;
        badge.hidden = true;
      }
      dimsEl.textContent = item.width && item.height
        ? `${item.width}×${item.height} — preview, loading full resolution…`
        : "loading full resolution…";
      if (item.width && item.height) currentAspect = item.width / item.height;
    }

    async function select(id) {
      refreshItems();
      const index = items.findIndex((it) => it.id === id);
      if (index < 0) return false;
      const item = items[index];
      const token = ++selection;
      currentId = id;
      clearPresentation();
      paintChrome(item);
      paintPreview(item);
      paintPlace();
      fitWindow();
      scheduleHud();
      onChange(item, true);
      try {
        const entry = await load(item.url, 0);
        if (token !== selection) return true;
        img.src = entry.blobUrl;
        pulse.hidden = true;
        badge.hidden = true;
        dimsEl.textContent = `${entry.width}×${entry.height}`;
        currentAspect = entry.width / entry.height;
        entry.lastUsed = Date.now();
        fitWindow();
      } catch (error) {
        if (token !== selection) return true;
        pulse.hidden = true;
        failed.hidden = false;
        failed.textContent = `full-resolution load failed: ${error && error.message ? error.message : error}`;
        dimsEl.textContent = item.width && item.height ? `${item.width}×${item.height} — preview only` : "";
      }
      if (token === selection) preloadNeighbors(index);
      return true;
    }

    // ---- preload runway HUD ----
    let hudTimer = null;
    function scheduleHud() {
      if (!open || hudTimer) return;
      hudTimer = setTimeout(() => { hudTimer = null; renderHud(); }, 80);
    }

    function renderHud() {
      hud.textContent = "";
      const index = currentIndex();
      if (index < 0) return;
      for (let i = index - PreloadRadius; i <= index + PreloadRadius; i++) {
        const tick = document.createElement("span");
        tick.className = "miv-tick";
        if (i < 0 || i >= items.length) {
          tick.classList.add("void");
        } else {
          const entry = cache.get(items[i].url);
          const status = entry ? entry.status : "none";
          tick.classList.add(status);
          if (i === index) {
            tick.classList.add("current");
            tick.textContent = String(i + 1);
          }
        }
        hud.appendChild(tick);
      }
    }

    // ---- window fitting ----
    function fitWindow() {
      if (!open) return;
      const vw = document.documentElement.clientWidth;
      const vh = document.documentElement.clientHeight;
      const maxW = vw - Margin * 2;
      const maxH = vh - Margin * 2;
      const ar = currentAspect > 0 ? currentAspect : 1;
      // Candidate A: status bar under the stage; B: side column.
      const statusH = Math.min(220, Math.max(96, Math.round(vh * 0.22)));
      const aW = Math.min(maxW, (maxH - statusH) * ar);
      const aH = aW / ar;
      const bW = Math.min(maxW - SideStatusWidth, maxH * ar);
      const bH = bW / ar;
      const side = bH > aH;
      win.classList.toggle("side-status", side);
      const stageW = Math.max(240, Math.round(side ? bW : aW));
      const stageH = Math.max(160, Math.round(side ? bH : aH));
      win.style.width = `${side ? stageW + SideStatusWidth : stageW}px`;
      win.style.height = `${side ? stageH : stageH + statusH}px`;
      win.style.setProperty("--miv-status-height", `${statusH}px`);
    }

    // ---- navigation ----
    function step(delta) {
      refreshItems();
      const index = currentIndex();
      if (index < 0) return;
      const next = Math.min(items.length - 1, Math.max(0, index + delta));
      if (next !== index) select(items[next].id);
    }

    function jumpTo(index) {
      refreshItems();
      if (!items.length) return;
      const clamped = Math.min(items.length - 1, Math.max(0, index));
      if (items[clamped].id !== currentId) select(items[clamped].id);
    }

    function onKey(event) {
      if (!open) return;
      if (!helpEl.hidden && event.key !== "?" && event.key !== "Escape") { helpEl.hidden = true; }
      switch (event.key) {
        case "ArrowRight": case "ArrowDown": case " ": step(1); break;
        case "ArrowLeft": case "ArrowUp": step(-1); break;
        case "PageDown": step(5); break;
        case "PageUp": step(-5); break;
        case "Home": jumpTo(0); break;
        case "End": jumpTo(items.length - 1); break;
        case "Escape":
          if (document.fullscreenElement === overlay) { document.exitFullscreen(); }
          else if (!helpEl.hidden) { helpEl.hidden = true; }
          else close();
          break;
        case "f": case "F": toggleFullscreen(); break;
        case "?": toggleHelp(); break;
        default: return;
      }
      event.preventDefault();
      event.stopPropagation();
    }

    function onWheel(event) {
      if (!open) return;
      if (Math.abs(event.deltaX) > Math.abs(event.deltaY)) return;
      const inner = event.target.closest(".miv-prompt, .miv-meta, .miv-help");
      if (inner && inner.scrollHeight > inner.clientHeight + 1) return;
      event.preventDefault();
      const unit = event.deltaMode === 1 ? 20 : event.deltaMode === 2 ? 120 : 1;
      wheelAccumulator += event.deltaY * unit;
      if (Math.abs(wheelAccumulator) >= WheelThreshold) {
        step(wheelAccumulator > 0 ? 1 : -1);
        wheelAccumulator = 0;
      }
    }

    function onMouseUp(event) {
      if (!open) return;
      if (event.button === 3) { event.preventDefault(); step(-1); }
      if (event.button === 4) { event.preventDefault(); step(1); }
    }

    function toggleFullscreen() {
      if (document.fullscreenElement === overlay) document.exitFullscreen();
      else if (overlay.requestFullscreen) overlay.requestFullscreen();
    }

    function toggleHelp() {
      if (!helpEl.hidden) { helpEl.hidden = true; return; }
      helpEl.textContent = "";
      const rows = [
        ["← / ↑  or  → / ↓  or  wheel", "previous / next image"],
        ["PageUp / PageDown", "five images back / forward"],
        ["Home / End", "first / last image"],
        ["mouse back / forward buttons", "previous / next image"],
        ["f", "toggle browser fullscreen"],
        ["Esc  or click outside", "close (Esc leaves fullscreen first)"],
        ["?", "this list"],
      ];
      const h = document.createElement("div");
      h.className = "miv-help-title";
      h.textContent = "Viewer controls";
      helpEl.appendChild(h);
      const dl = document.createElement("dl");
      for (const [k, v] of rows) {
        const dt = document.createElement("dt"); dt.textContent = k;
        const dd = document.createElement("dd"); dd.textContent = v;
        dl.append(dt, dd);
      }
      helpEl.appendChild(dl);
      helpEl.hidden = false;
    }

    let copyTimer = null;
    copyBtn.addEventListener("click", async () => {
      const text = promptEl.textContent || "";
      if (!text) return;
      try {
        await navigator.clipboard.writeText(text);
        copyBtn.textContent = "copied";
      } catch {
        copyBtn.textContent = "copy failed";
      }
      clearTimeout(copyTimer);
      copyTimer = setTimeout(() => { copyBtn.textContent = "copy prompt"; }, 1200);
    });

    overlay.addEventListener("click", (event) => {
      if (event.target === overlay) close();
    });
    overlay.addEventListener("wheel", onWheel, { passive: false });
    document.addEventListener("keydown", onKey, true);
    document.addEventListener("mouseup", onMouseUp, true);
    window.addEventListener("resize", () => { if (open) fitWindow(); });
    document.addEventListener("fullscreenchange", () => { if (open) fitWindow(); });

    // ---- public ----
    function openViewer(id) {
      refreshItems();
      if (!items.some((it) => it.id === id)) return false;
      if (!open) {
        restoreFocus = document.activeElement;
        open = true;
        overlay.hidden = false;
        document.body.classList.add("miv-open");
        wheelAccumulator = 0;
      }
      select(id);
      overlay.focus();
      return true;
    }

    function close() {
      if (!open) return;
      if (document.fullscreenElement === overlay) document.exitFullscreen();
      open = false;
      selection++;
      overlay.hidden = true;
      helpEl.hidden = true;
      document.body.classList.remove("miv-open");
      clearPresentation();
      abortUnfinished();
      const wasId = currentId;
      currentId = null;
      onChange(items.find((it) => it.id === wasId) || null, false);
      if (restoreFocus && typeof restoreFocus.focus === "function") restoreFocus.focus();
      restoreFocus = null;
    }

    overlay.tabIndex = -1;

    return Object.freeze({
      open: openViewer,
      close,
      isOpen: () => open,
      currentId: () => currentId,
      refresh: () => { if (open) { refreshItems(); paintPlace(); scheduleHud(); } },
      prefetch: (url) => { load(url, PreloadRadius + 1); },
    });
  }

  root.MultiImageViewer = Object.freeze({ create });
})(typeof globalThis === "object" ? globalThis : this);
