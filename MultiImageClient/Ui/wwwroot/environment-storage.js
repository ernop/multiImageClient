// Served after the server supplies MicEnvironment. The legacy environment never runs this file.
(() => {
  "use strict";
  const env = window.MicEnvironment;
  if (!env || !env.id || !env.user || !env.identity) throw new Error("Missing environment identity.");
  const prefix = `mic-env:${env.id}:${env.user}:`;
  const hidden = [];
  if (env.role !== "admin") hidden.push("#logs-toggle", "#logs-panel", "#ram-status", "#build-info");
  if (env.nightFilter === false) hidden.push("#night-toggle", "#night-filter-preferences");
  if (!env.goalLoops) hidden.push("#goal-loops-link");
  if (!env.video) hidden.push("#image-viewer-video", "#video-dialog");
  if (env.vibecodersSharing === false) hidden.push("#image-viewer-vibecoders");
  if (!env.promptRewrite) hidden.push("#prompt-advice", "[data-rewrite-model]", "#prompt-history-toggle", "#prompt-rewrite-history", "#prompt-advice-undo");
  if (hidden.length) {
    const style = document.createElement("style"); style.textContent = hidden.join(",") + "{display:none!important}"; document.head.append(style);
  }
  function scoped(storage) {
    const keys = () => Array.from({ length: storage.length }, (_, i) => storage.key(i))
      .filter(key => key && key.startsWith(prefix));
    return {
      get length() { return keys().length; },
      key(index) { return keys()[index]?.slice(prefix.length) ?? null; },
      getItem(key) { return storage.getItem(prefix + String(key)); },
      setItem(key, value) { storage.setItem(prefix + String(key), String(value)); },
      removeItem(key) { storage.removeItem(prefix + String(key)); },
      clear() { keys().forEach(key => storage.removeItem(key)); },
    };
  }
  for (const name of env.legacyStorage ? [] : ["localStorage", "sessionStorage"]) {
    const storage = scoped(window[name]);
    Object.defineProperty(window, name, { configurable: false, get: () => storage });
  }
  // A second link can change this environment's account while an old tab remains open.
  // Every app request carries its boot identity; the server rejects stale tabs before mutation.
  const originalFetch = window.fetch.bind(window);
  const base = new URL("./", location.href);
  window.fetch = async (input, init) => {
    const url = new URL(input instanceof Request ? input.url : input, location.href);
    if (url.origin === base.origin && url.pathname.startsWith(base.pathname + "api/")) {
      const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
      headers.set("X-Mic-Identity", env.identity);
      const response = await originalFetch(input, { ...init, headers });
      if (response.status === 401) location.reload();
      return response;
    }
    return originalFetch(input, init);
  };
  window.addEventListener("DOMContentLoaded", () => {
    document.title = env.name;
    const label = document.getElementById("environment-name");
    if (label) {
      label.textContent = env.name;
      const heading = label.closest("h1");
      const home = heading?.querySelector(".header-home");
      if (home) { home.textContent = ""; home.append(label); }
      if (heading) for (const child of [...heading.childNodes]) {
        if (child.nodeType === Node.TEXT_NODE) child.remove();
      }
    }
    const header = document.querySelector("header");
    if (header && env.environments?.length > 1) {
      const switcher = document.createElement("select");
      switcher.id = "environment-switcher";
      switcher.setAttribute("aria-label", "Switch environment");
      switcher.title = "Switch environment";
      switcher.style.cssText = "max-width:min(320px,85vw);padding:5px;font:inherit";
      for (const choice of env.environments) {
        const option = document.createElement("option");
        option.value = choice.id; option.textContent = choice.name;
        option.selected = choice.id === env.id; switcher.append(option);
      }
      switcher.addEventListener("change", () => {
        const choice = env.environments.find(e => e.id === switcher.value);
        if (choice && choice.id !== env.id) location.assign(choice.url);
      });
      const caption = document.createElement("label"); caption.htmlFor = switcher.id;
      caption.textContent = "Environment "; caption.append(switcher);
      header.querySelector("h1")?.insertAdjacentElement("afterend", caption);
    }
    const people = document.getElementById("people-link");
    if (people && env.adminUrl) { people.href = env.adminUrl; people.textContent = "administration"; }
    const preferences = document.getElementById("settings-toggle");
    if (preferences) preferences.textContent = "preferences";
  });
})();
