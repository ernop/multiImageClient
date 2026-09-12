// Drafts belong to this tab, page directory, environment, account, and field.
// Save synchronously on input so a reload cannot outrun a pending debounce.
(() => {
  "use strict";
  const directory = location.pathname.replace(/[^/]*$/, "");
  const env = window.MicEnvironment;
  for (const field of document.querySelectorAll("textarea[data-text-draft]")) {
    const key = "mic_text_draft_v1:" + JSON.stringify([
      directory, env?.id || "", env?.user || "", field.dataset.textDraft,
    ]);
    let notice;
    function problem(message) {
      if (!notice) {
        notice = document.createElement("span");
        notice.setAttribute("role", "status");
        field.insertAdjacentElement("afterend", notice);
      }
      notice.textContent = message;
    }
    function save() {
      try {
        if (field.value === "") sessionStorage.removeItem(key);
        else sessionStorage.setItem(key, JSON.stringify({
          text: field.value,
          start: field.selectionStart,
          end: field.selectionEnd,
          direction: field.selectionDirection,
          scrollTop: field.scrollTop,
          focused: document.activeElement === field,
        }));
        if (notice) notice.textContent = "";
      } catch {
        problem("Draft saving is unavailable. Copy your text before reloading.");
      }
    }
    try {
      const raw = sessionStorage.getItem(key);
      if (raw !== null && field.value === "") {
        const draft = JSON.parse(raw);
        if (!draft || typeof draft.text !== "string"
          || !Number.isInteger(draft.start) || !Number.isInteger(draft.end)
          || draft.start < 0 || draft.end < draft.start || draft.end > draft.text.length
          || !["forward", "backward", "none"].includes(draft.direction)
          || !Number.isFinite(draft.scrollTop) || typeof draft.focused !== "boolean") {
          throw new Error("Invalid saved text draft.");
        }
        field.value = draft.text;
        field.setSelectionRange(draft.start, draft.end, draft.direction);
        field.scrollTop = draft.scrollTop;
        document.addEventListener("DOMContentLoaded", () => {
          // Do not replace browser-restored text or edits made during startup.
          if (field.value !== draft.text) return;
          field.dispatchEvent(new Event("input", { bubbles: true }));
          if (draft.focused && (!document.activeElement || document.activeElement === document.body)) {
            field.focus({ preventScroll: true });
            field.setSelectionRange(draft.start, draft.end, draft.direction);
            field.scrollTop = draft.scrollTop;
          }
        }, { once: true });
      }
    } catch {
      problem("Saved text could not be restored. Copy your current text before reloading.");
    }
    for (const event of ["input", "change", "select", "keyup", "pointerup", "compositionend", "focus", "blur"]) {
      field.addEventListener(event, save);
    }
    window.addEventListener("pagehide", save);
    window.addEventListener("beforeunload", save);
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "hidden") save();
    });
  }
})();
