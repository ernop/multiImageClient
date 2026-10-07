"use strict";

// Members-only link to one prompt, or to one result inside it. Nothing is sent.
window.openShareLinkDialog = function openShareLinkDialog({ apiUrl, jobId, generator = null, imageIndex = null, prompt, thumbUrl = null,
  noun = generator === null ? "prompt" : "image" }) {
  const dialog = document.getElementById("share-link-dialog");
  if (dialog.open) return Promise.resolve();
  const scope = dialog.querySelector(".share-link-scope");
  const thumb = dialog.querySelector(".share-link-thumb");
  const promptText = dialog.querySelector(".share-link-prompt");
  const link = dialog.querySelector("#share-link-url");
  const copy = dialog.querySelector(".share-link-copy");
  const status = dialog.querySelector(".share-link-status");
  const close = dialog.querySelector(".share-link-close");
  const controller = new AbortController();
  scope.textContent = "Preparing the link…";
  status.textContent = "";
  status.classList.remove("error");
  link.value = "";
  copy.disabled = true;
  promptText.textContent = prompt;
  if (thumbUrl) thumb.src = thumbUrl;
  else thumb.removeAttribute("src");
  thumb.hidden = !thumbUrl;
  dialog.showModal();

  return new Promise((resolve) => {
    function finish(event) {
      event?.preventDefault();
      controller.abort();
      dialog.close();
      link.value = "";
      promptText.textContent = "";
      thumb.removeAttribute("src");
      thumb.hidden = true;
      dialog.removeEventListener("cancel", finish);
      close.removeEventListener("click", finish);
      copy.removeEventListener("click", onCopy);
      link.removeEventListener("focus", onFocus);
      resolve();
    }
    async function onCopy() {
      try {
        await navigator.clipboard.writeText(link.value);
        status.classList.remove("error");
        status.textContent = "Link copied.";
      } catch {
        link.select();
        status.classList.add("error");
        status.textContent = "Copy failed. The link is selected. Copy it with your keyboard.";
      }
    }
    function onFocus() { link.select(); }
    dialog.addEventListener("cancel", finish);
    close.addEventListener("click", finish);
    copy.addEventListener("click", onCopy);
    link.addEventListener("focus", onFocus);
    (async () => {
      try {
        const query = new URLSearchParams({ jobId });
        if (generator !== null) {
          query.set("generator", generator);
          query.set("imageIndex", String(imageIndex));
        }
        const response = await fetch(apiUrl(`api/share-link?${query}`), { cache: "no-store", signal: controller.signal });
        const body = await response.json();
        if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
        link.value = body.url;
        scope.textContent = `Members of ${body.siteName} can open this link. `
          + `Signed-in members go directly to this ${noun}. Other people log in first.`;
        copy.disabled = false;
        copy.focus();
      } catch (error) {
        if (controller.signal.aborted) return;
        scope.textContent = "No link is available.";
        status.classList.add("error");
        status.textContent = error.message;
      }
    })();
  });
};
