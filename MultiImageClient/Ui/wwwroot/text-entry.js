// Application-owned sizing and caret visibility. Spelling libraries only decorate the field.
(() => {
  "use strict";
  const selector = 'textarea:not([readonly]):not([data-text-entry="fixed"])';
  const fields = new Map();
  const pending = new Set();
  let frame = 0;
  const metrics = [
    "fontFamily", "fontSize", "fontWeight", "fontStyle", "fontStretch", "fontVariant",
    "fontFeatureSettings", "fontVariationSettings", "letterSpacing", "wordSpacing",
    "lineHeight", "textTransform", "textIndent", "textAlign", "direction",
    "whiteSpace", "overflowWrap", "wordBreak", "tabSize",
    "paddingTop", "paddingRight", "paddingBottom", "paddingLeft",
    "borderTopWidth", "borderRightWidth", "borderBottomWidth", "borderLeftWidth",
  ];
  // One reusable measuring surface avoids changing the live field's flex geometry,
  // selection, undo history, or spelling overlay while measuring wrapped text.
  const measure = document.createElement("div");
  measure.setAttribute("aria-hidden", "true");
  measure.inert = true;
  measure.style.cssText = "position:fixed;left:0;top:0;width:0;height:0;overflow:hidden;visibility:hidden;pointer-events:none;contain:strict";
  const sizeMirror = document.createElement("textarea");
  sizeMirror.readOnly = true;
  sizeMirror.tabIndex = -1;
  const caretMirror = document.createElement("div");
  const mirrorStyle = "all:initial;display:block;box-sizing:border-box;height:0;min-height:0;max-height:none;border-style:solid;border-color:transparent;overflow:hidden";
  sizeMirror.style.cssText = mirrorStyle;
  caretMirror.style.cssText = mirrorStyle + ";height:auto";
  measure.append(sizeMirror, caretMirror);
  document.body.append(measure);

  const pixels = value => Number.parseFloat(value) || 0;

  function caretOffset(field) {
    const offset = field.selectionDirection === "backward" ? field.selectionStart : field.selectionEnd;
    const text = document.createTextNode(field.value + "\u200b");
    caretMirror.replaceChildren(text);
    const range = document.createRange();
    range.setStart(text, offset);
    range.setEnd(text, offset + 1);
    const rect = range.getBoundingClientRect();
    const top = caretMirror.getBoundingClientRect().top;
    return { top: rect.top - top, bottom: rect.bottom - top };
  }

  function reveal(field, caret, lineHeight) {
    const caretRect = () => {
      const top = field.getBoundingClientRect().top - field.scrollTop;
      return { top: top + caret.top, bottom: top + caret.bottom };
    };
    function adjustment(top, bottom) {
      const margin = Math.min(lineHeight * 2, (bottom - top) / 4);
      const rect = caretRect();
      if (rect.bottom > bottom - margin) return rect.bottom - bottom + margin;
      if (rect.top < top + margin) return rect.top - top - margin;
      return 0;
    }
    // Scroll each enclosing panel before the document. Goal setup and dialogs
    // have their own scrolling regions; scrolling only the page cannot reveal them.
    for (let parent = field.parentElement; parent && parent !== document.body; parent = parent.parentElement) {
      if (parent.scrollHeight <= parent.clientHeight) continue;
      if (!/auto|scroll|hidden/.test(getComputedStyle(parent).overflowY)) continue;
      const rect = parent.getBoundingClientRect();
      const top = rect.top + parent.clientTop;
      const delta = adjustment(top, top + parent.clientHeight);
      if (delta) parent.scrollBy({ top: delta, behavior: "instant" });
    }
    const viewport = window.visualViewport;
    let top = viewport ? viewport.offsetTop : 0;
    const bottom = top + (viewport ? viewport.height : window.innerHeight);
    for (const header of document.querySelectorAll("header")) {
      const position = getComputedStyle(header).position;
      const rect = header.getBoundingClientRect();
      if ((position === "sticky" || position === "fixed") && rect.top <= top && rect.bottom > top) {
        top = rect.bottom;
      }
    }
    const delta = adjustment(top, bottom);
    if (delta) window.scrollBy({ top: delta, behavior: "instant" });
  }

  function update(field, keepVisible = false) {
    const state = fields.get(field);
    if (!state || !field.isConnected || !field.matches(selector) || field.disabled) return;
    if (!field.getClientRects().length || !field.clientWidth) return;
    if (!field.value && state.hadText) field.style.height = state.height;
    state.hadText = !!field.value;
    const style = getComputedStyle(field);
    const rect = field.getBoundingClientRect();
    for (const name of metrics) {
      sizeMirror.style[name] = style[name];
      caretMirror.style[name] = style[name];
    }
    const width = rect.width + "px";
    sizeMirror.style.width = caretMirror.style.width = width;
    sizeMirror.wrap = field.wrap;
    sizeMirror.value = field.value;
    const lineHeight = pixels(style.lineHeight) || pixels(style.fontSize) * 1.2;
    const borders = pixels(style.borderTopWidth) + pixels(style.borderBottomWidth);
    const padding = pixels(style.paddingTop) + pixels(style.paddingBottom);
    const required = Math.ceil(sizeMirror.scrollHeight + borders + lineHeight * 2);
    const height = style.boxSizing === "border-box" ? required : required - borders - padding;
    const minimum = Math.max(state.minHeight, height) + "px";
    if (field.style.minHeight !== minimum) field.style.minHeight = minimum;
    // Keep manual enlargement and the composer's shared row height. Only clearing
    // the text resets automatic growth; deletion within a draft never pulls it upward.
    if (required > rect.height) field.style.height = height + "px";
    field.scrollTop = 0;
    if (keepVisible && document.activeElement === field) reveal(field, caretOffset(field), lineHeight);
    sizeMirror.value = "";
    caretMirror.replaceChildren();
  }

  function schedule(field) {
    pending.add(field);
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      for (const field of pending) update(field, document.activeElement === field);
      pending.clear();
    });
  }

  const resize = new ResizeObserver(entries => {
    for (const entry of entries) schedule(entry.target);
  });
  function attach(field) {
    if (fields.has(field)) return;
    fields.set(field, {
      height: field.style.height,
      minHeight: pixels(getComputedStyle(field).minHeight),
      hadText: !!field.value,
    });
    field.style.maxHeight = "none";
    field.style.overflowY = "hidden";
    resize.observe(field);
    update(field);
  }
  function scan(root) {
    if (measure.contains(root)) return;
    if (root.matches?.(selector)) attach(root);
    for (const field of root.querySelectorAll?.(selector) || []) attach(field);
  }
  scan(document);
  new MutationObserver(records => {
    for (const record of records) {
      for (const node of record.addedNodes) scan(node);
    }
    for (const field of fields.keys()) {
      if (!field.isConnected) {
        resize.unobserve(field);
        fields.delete(field);
        pending.delete(field);
      }
    }
  }).observe(document.body, { childList: true, subtree: true });

  function edited(event) {
    const field = event.target;
    if (!field.matches?.(selector)) return;
    attach(field);
    // The input handler runs before paint, including Enter, paste, and IME input.
    update(field, true);
    // Reconcile any native caret scrolling that follows the event's default action.
    schedule(field);
  }
  for (const type of ["input", "focusin", "keyup", "select", "compositionend"]) {
    document.addEventListener(type, edited, true);
  }
  document.addEventListener("selectionchange", () => {
    const field = document.activeElement;
    if (fields.has(field)) schedule(field);
  });
  function resized() {
    for (const field of fields.keys()) schedule(field);
  }
  window.addEventListener("resize", resized);
  window.visualViewport?.addEventListener("resize", resized);
  document.fonts?.ready.then(resized);
})();
