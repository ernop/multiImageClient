// Served after the server supplies MicEnvironment. The legacy environment never runs this file.
(() => {
  "use strict";
  const env = window.MicEnvironment;
  if (!env || !env.id || !env.user || !env.identity) throw new Error("Missing environment identity.");
  const prefix = `mic-env:${env.id}:${env.user}:`;
  const hidden = [];
  if (env.role !== "admin") hidden.push("#logs-toggle", "#logs-panel");
  if (!env.goalLoops) hidden.push("#goal-loops-link");
  if (!env.video) hidden.push("#image-viewer-video", "#video-dialog");
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
    if (label) label.textContent = " · " + env.name;
    const people = document.getElementById("people-link");
    if (people) { people.href = env.adminUrl; people.textContent = "administration"; }
    const preferences = document.getElementById("settings-toggle");
    if (preferences) preferences.textContent = "preferences";
  });
})();
