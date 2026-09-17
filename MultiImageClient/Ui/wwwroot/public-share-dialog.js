"use strict";

window.previewPublicShare = async function previewPublicShare({ apiUrl, jobId, generator, imageIndex }) {
  const dialog = document.getElementById("public-share-dialog");
  if (dialog.open) throw new Error("A share preview is already open.");
  const destination = dialog.querySelector(".share-destination");
  const status = dialog.querySelector(".share-status");
  const mediaBox = dialog.querySelector(".share-media");
  const caption = dialog.querySelector(".share-caption");
  const disclosure = dialog.querySelector(".share-disclosure");
  const details = dialog.querySelector("details");
  const frame = dialog.querySelector("iframe");
  const confirm = dialog.querySelector(".share-confirm");
  const cancel = dialog.querySelector(".share-cancel");
  let record = null, sending = false, finished = false, finalError = null;
  const controller = new AbortController();
  destination.textContent = "Preparing preview…";
  status.textContent = "";
  mediaBox.replaceChildren();
  caption.replaceChildren();
  disclosure.textContent = "";
  details.open = false;
  details.hidden = true;
  frame.removeAttribute("src");
  confirm.disabled = true;
  cancel.disabled = false;
  dialog.showModal();

  return new Promise((resolve, reject) => {
    function finish(value, error) {
      if (finished) return;
      finished = true;
      controller.abort();
      dialog.close();
      mediaBox.replaceChildren();
      frame.removeAttribute("src");
      caption.replaceChildren();
      dialog.removeEventListener("cancel", onCancel);
      cancel.removeEventListener("click", onCancel);
      confirm.removeEventListener("click", onConfirm);
      if (error) reject(error); else resolve(value);
    }
    function onCancel(event) {
      event.preventDefault();
      if (!sending) finish(null, finalError);
    }
    async function onConfirm() {
      if (confirm.disabled || !record || sending) return;
      sending = true;
      confirm.disabled = true;
      cancel.disabled = true;
      status.textContent = "Publishing and sending…";
      try {
        const form = new FormData();
        form.append("token", record.token);
        form.append("confirmed", "true");
        const response = await fetch(apiUrl("api/discord/vibecoders"), {
          method: "POST", headers: { "X-MIC-Share": "1" }, body: form,
        });
        const body = await response.json();
        if (!response.ok) {
          const error = new Error(body.error || `HTTP ${response.status}`);
          error.state = body.state;
          throw error;
        }
        if (body.item?.jobId !== jobId || body.item?.generator !== generator || body.item?.imageIndex !== imageIndex)
          throw new Error("The send response did not match this preview.");
        finish(body);
      } catch (error) {
        // A lost reply can follow a successful post. Never offer an automatic retry.
        error.state = error.state || "pending";
        status.textContent = error.state === "pending"
          ? "Publication or delivery may have completed. Check Discord before sending again."
          : error.message;
        sending = false;
        finalError = error;
        cancel.disabled = false;
        cancel.textContent = "Close";
      }
    }
    cancel.textContent = "Cancel";
    cancel.addEventListener("click", onCancel);
    dialog.addEventListener("cancel", onCancel);
    confirm.addEventListener("click", onConfirm);
    (async () => {
      try {
        const form = new FormData();
        form.append("jobId", jobId);
        form.append("generator", generator);
        form.append("imageIndex", String(imageIndex));
        const response = await fetch(apiUrl("api/discord/vibecoders/prepare"), {
          method: "POST", headers: { "X-MIC-Share": "1" }, body: form, signal: controller.signal,
        });
        const body = await response.json();
        if (finished) return;
        if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
        if (body.jobId !== jobId || body.generator !== generator || body.imageIndex !== imageIndex)
          throw new Error("The preview did not match the selected image.");
        record = body;
        destination.textContent = `Post to ${body.serverName} · #${body.channelName.replace(/^#/, "")}`;
        disclosure.textContent = body.disclosure;
        const media = document.createElement(body.mediaKind === "video" ? "video" : "img");
        if (body.mediaKind === "video") { media.controls = true; media.preload = "metadata"; }
        else media.alt = "Selected image to post";
        media.addEventListener(body.mediaKind === "video" ? "loadedmetadata" : "load", () => {
          if (!finished && !sending) confirm.disabled = false;
        }, { once: true });
        media.addEventListener("error", () => { status.textContent = "The image preview could not load. Close and try again."; }, { once: true });
        media.src = apiUrl(body.mediaUrl);
        mediaBox.append(media);
        for (const [label, suffix] of [[body.linkLabel, ""], [body.reuseLabel, "reuse"]]) {
          if (caption.childNodes.length) caption.append(" · ");
          const link = document.createElement("a");
          link.textContent = label;
          link.href = body.publicUrl + suffix;
          link.addEventListener("click", event => { event.preventDefault(); details.open = true; });
          caption.append(link);
        }
        details.hidden = false;
        details.querySelector("summary").textContent = `Public page preview · ${body.inputCount} inputs · ${body.outputCount} outputs${body.hasContactSheet ? " · contact sheet" : ""}`;
        frame.src = apiUrl(body.previewUrl);
      } catch (error) {
        if (!finished) { status.textContent = error.message; destination.textContent = "Sharing unavailable"; }
      }
    })();
  });
};
