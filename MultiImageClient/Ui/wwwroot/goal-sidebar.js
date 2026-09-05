"use strict";
// Store only the chosen width. Mobile layouts remain a single column.
(() => {
  const main = document.getElementById("goal-main");
  const sidebar = document.getElementById("goal-sidebar");
  const handle = document.getElementById("goal-sidebar-resize");
  const key = "mic_goal_sidebar_width";
  let width = null;
  try {
    const saved = Number(localStorage.getItem(key));
    if (Number.isFinite(saved) && saved >= 250) width = saved;
  } catch { /* Storage can be disabled by the browser. */ }
  const maximum = () => Math.max(250, Math.min(900, window.innerWidth - 420));
  function apply(value, persist = false) {
    width = Math.max(250, Math.min(maximum(), value));
    main.style.setProperty("--goal-sidebar-width", `${width}px`);
    handle.setAttribute("aria-valuemin", "250");
    handle.setAttribute("aria-valuemax", String(maximum()));
    handle.setAttribute("aria-valuenow", String(Math.round(width)));
    if (persist) {
      try { localStorage.setItem(key, String(width)); } catch { /* Width still works for this page. */ }
    }
  }
  function refresh() {
    if (window.innerWidth > 980) apply(width ?? sidebar.getBoundingClientRect().width);
  }
  handle.addEventListener("pointerdown", event => {
    if (event.button !== 0) return;
    event.preventDefault();
    handle.focus();
    handle.setPointerCapture(event.pointerId);
  });
  handle.addEventListener("pointermove", event => {
    if (handle.hasPointerCapture(event.pointerId)) apply(event.clientX - sidebar.getBoundingClientRect().left);
  });
  handle.addEventListener("pointerup", event => {
    if (!handle.hasPointerCapture(event.pointerId)) return;
    handle.releasePointerCapture(event.pointerId);
    apply(width, true);
  });
  handle.addEventListener("keydown", event => {
    const current = sidebar.getBoundingClientRect().width;
    const next = { ArrowLeft: current - 20, ArrowRight: current + 20, Home: 250, End: maximum() }[event.key];
    if (next === undefined) return;
    event.preventDefault();
    apply(next, true);
  });
  handle.addEventListener("dblclick", () => apply(window.innerWidth * .22, true));
  window.addEventListener("resize", refresh);
  refresh();
})();
