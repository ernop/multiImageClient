"use strict";

function formatShareMiB(bytes) {
  const n = Number(bytes || 0);
  if (!Number.isFinite(n) || n <= 0) return "0 MiB";
  const mib = n / (1024 * 1024);
  return mib >= 10 ? `${mib.toFixed(1)} MiB` : `${mib.toFixed(2)} MiB`;
}

window.previewPublicShare = async function previewPublicShare({ apiUrl, jobId, generator, imageIndex }) {
  const dialog = document.getElementById("public-share-dialog");
  if (dialog.open) throw new Error("A share preview is already open.");
  const destination = dialog.querySelector(".share-destination");
  const picker = dialog.querySelector(".share-series-picker");
  const batchesBox = dialog.querySelector(".share-batches");
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
  if (picker) { picker.replaceChildren(); picker.hidden = true; }
  if (batchesBox) { batchesBox.textContent = ""; batchesBox.hidden = true; }
  status.textContent = "";
  mediaBox.replaceChildren();
  caption.replaceChildren();
  disclosure.textContent = "";
  details.open = false;
  details.hidden = true;
  frame.removeAttribute("src");
  confirm.disabled = true;
  confirm.textContent = "Make public & send";
  cancel.disabled = false;
  dialog.showModal();

  return new Promise((resolve, reject) => {
    function finish(value, error) {
      if (finished) return;
      finished = true;
      controller.abort();
      dialog.close();
      if (picker) { picker.replaceChildren(); picker.hidden = true; }
      if (batchesBox) { batchesBox.textContent = ""; batchesBox.hidden = true; }
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
        if (body.pagePublished) {
          confirm.textContent = "Send to thread";
          status.textContent = "This prompt already has a public page. This image will use the same page.";
        }
        destination.textContent = `Post to ${body.serverName} · #${body.channelName.replace(/^#/, "")} → ${body.threadName} (Pacific time)`;
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
        for (const [label, url] of [[body.linkLabel, body.viewUrl], [body.reuseLabel, body.publicUrl + "reuse"]]) {
          if (caption.childNodes.length) caption.append(" · ");
          const link = document.createElement("a");
          link.textContent = label;
          link.href = url;
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

window.previewPublicSeriesShare = async function previewPublicSeriesShare({ apiUrl, groupId, generator, pageVariants }) {
  const dialog = document.getElementById("public-share-dialog");
  if (dialog.open) throw new Error("A share preview is already open.");
  const destination = dialog.querySelector(".share-destination");
  const picker = dialog.querySelector(".share-series-picker");
  const batchesBox = dialog.querySelector(".share-batches");
  const status = dialog.querySelector(".share-status");
  const mediaBox = dialog.querySelector(".share-media");
  const caption = dialog.querySelector(".share-caption");
  const disclosure = dialog.querySelector(".share-disclosure");
  const details = dialog.querySelector("details");
  const frame = dialog.querySelector("iframe");
  const confirm = dialog.querySelector(".share-confirm");
  const cancel = dialog.querySelector(".share-cancel");

  const variants = Array.isArray(pageVariants) ? pageVariants : [];
  const selectedIndices = variants.map(() => 0);
  let record = null, sending = false, finished = false, finalError = null;
  let prepareSeq = 0;
  let prepareController = null;

  function resetPreviewArea(message) {
    record = null;
    destination.textContent = message;
    if (batchesBox) { batchesBox.textContent = ""; batchesBox.hidden = true; }
    status.textContent = "";
    mediaBox.replaceChildren();
    caption.replaceChildren();
    disclosure.textContent = "";
    details.open = false;
    details.hidden = true;
    frame.removeAttribute("src");
    confirm.disabled = true;
    confirm.textContent = "Make public & send";
  }

  resetPreviewArea("Preparing series preview…");
  if (picker) { picker.replaceChildren(); picker.hidden = true; }
  cancel.disabled = false;
  dialog.showModal();

  return new Promise((resolve, reject) => {
    function finish(value, error) {
      if (finished) return;
      finished = true;
      prepareController?.abort();
      dialog.close();
      if (picker) { picker.replaceChildren(); picker.hidden = true; }
      if (batchesBox) { batchesBox.textContent = ""; batchesBox.hidden = true; }
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
      if (picker) {
        for (const btn of picker.querySelectorAll("button")) btn.disabled = true;
      }
      const batchCount = Array.isArray(record.batches) ? record.batches.length : 1;
      status.textContent = batchCount > 1
        ? `Publishing series page and sending ${batchCount} Discord messages…`
        : "Publishing series page and sending to Discord…";
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
        if (body.item?.kind !== "series" || body.item?.groupId !== groupId || body.item?.generator !== generator)
          throw new Error("The send response did not match this series preview.");
        finish(body);
      } catch (error) {
        error.state = error.state || "pending";
        status.textContent = error.state === "pending"
          ? (error.message || "Publication or delivery may have completed. Check Discord before sending again.")
          : error.message;
        sending = false;
        finalError = error;
        cancel.disabled = false;
        cancel.textContent = "Close";
      }
    }

    function renderVariantPicker() {
      if (!picker) return;
      const multiPages = variants
        .map((v, pageIdx) => ({ v, pageIdx }))
        .filter(({ v }) => Array.isArray(v.thumbs) && v.thumbs.length > 1);
      picker.replaceChildren();
      if (!multiPages.length) {
        picker.hidden = true;
        return;
      }
      const heading = document.createElement("p");
      heading.className = "share-series-picker-head";
      heading.textContent = "Choose 1 image per page to include in this series:";
      picker.append(heading);
      for (const { v, pageIdx } of multiPages) {
        const row = document.createElement("div");
        row.className = "share-series-picker-row";
        const label = document.createElement("span");
        label.className = "share-series-picker-label";
        label.textContent = v.title ? `Page ${v.index} · ${v.title}` : `Page ${v.index}`;
        row.append(label);
        const choices = document.createElement("div");
        choices.className = "share-series-picker-choices";
        v.thumbs.forEach((thumbUrl, optIdx) => {
          const btn = document.createElement("button");
          btn.type = "button";
          btn.className = "share-series-choice" + (selectedIndices[pageIdx] === optIdx ? " selected" : "");
          btn.setAttribute("aria-pressed", String(selectedIndices[pageIdx] === optIdx));
          btn.title = `Page ${v.index}, image ${optIdx + 1}`;
          const img = document.createElement("img");
          img.src = thumbUrl;
          img.alt = `Page ${v.index} option ${optIdx + 1}`;
          const num = document.createElement("span");
          num.textContent = `#${optIdx + 1}`;
          btn.append(img, num);
          btn.addEventListener("click", () => {
            if (sending || selectedIndices[pageIdx] === optIdx) return;
            selectedIndices[pageIdx] = optIdx;
            renderVariantPicker();
            prepareCurrentSelection();
          });
          choices.append(btn);
        });
        row.append(choices);
        picker.append(row);
      }
      picker.hidden = false;
    }

    async function prepareCurrentSelection() {
      if (finished || sending) return;
      prepareController?.abort();
      const controller = new AbortController();
      prepareController = controller;
      const mySeq = ++prepareSeq;
      resetPreviewArea("Preparing series preview…");
      try {
        const form = new FormData();
        form.append("groupId", groupId);
        form.append("generator", generator);
        if (selectedIndices.length) {
          form.append("imageIndices", selectedIndices.join(","));
        }
        const response = await fetch(apiUrl("api/discord/vibecoders/prepare-series"), {
          method: "POST",
          headers: { "X-MIC-Share": "1" },
          body: form,
          signal: controller.signal,
        });
        const body = await response.json();
        if (finished || mySeq !== prepareSeq) return;
        if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
        if (body.kind !== "series" || body.groupId !== groupId || body.generator !== generator)
          throw new Error("The preview did not match the selected series.");
        record = body;
        if (body.pagePublished) {
          confirm.textContent = "Send to thread";
          status.textContent = "This exact series already has a public page. Posting will reuse that page.";
        }
        destination.textContent = `Post to ${body.serverName} · #${body.channelName.replace(/^#/, "")} → ${body.threadName} (Pacific time)`;
        disclosure.textContent = body.disclosure;

        if (batchesBox && Array.isArray(body.batches) && body.batches.length) {
          const totalText = formatShareMiB(body.totalBytes);
          if (body.batches.length === 1) {
            batchesBox.textContent = `1 Discord message · ${body.pageCount} original images (${totalText} total)`;
          } else {
            const partsDesc = body.batches
              .map((b) => (b.startPage === b.endPage ? `Page ${b.startPage}` : `Pages ${b.startPage}–${b.endPage}`)
                + ` (${formatShareMiB(b.totalBytes)})`)
              .join(", ");
            batchesBox.textContent = `${body.batches.length} Discord messages (split to stay under Discord's 25 MiB per-message limit): ${partsDesc}`;
          }
          batchesBox.hidden = false;
        }

        const strip = document.createElement("div");
        strip.className = "share-series-strip";
        let remaining = body.pages.length;
        let failedLoad = false;
        for (const p of body.pages) {
          const card = document.createElement("figure");
          card.className = "share-series-thumb";
          const img = document.createElement("img");
          img.alt = p.title ? `Page ${p.index}: ${p.title}` : `Page ${p.index}`;
          img.addEventListener("load", () => {
            if (finished || mySeq !== prepareSeq) return;
            remaining--;
            if (remaining <= 0 && !failedLoad && !sending) confirm.disabled = false;
          }, { once: true });
          img.addEventListener("error", () => {
            if (finished || mySeq !== prepareSeq) return;
            failedLoad = true;
            confirm.disabled = true;
            status.textContent = `Page ${p.index} preview could not load. Close and try again.`;
          }, { once: true });
          img.src = apiUrl(p.previewUrl);
          const cap = document.createElement("figcaption");
          cap.textContent = `${p.index}. ${p.title ? p.title + " · " : ""}${formatShareMiB(p.bytes)}`;
          card.append(img, cap);
          strip.append(card);
        }
        mediaBox.append(strip);

        const firstBatch = body.batches?.[0];
        const prefix = firstBatch && body.batches.length > 1
          ? (firstBatch.startPage === firstBatch.endPage
              ? `Page ${firstBatch.startPage} of ${body.pageCount} · `
              : `Pages ${firstBatch.startPage}–${firstBatch.endPage} of ${body.pageCount} · `)
          : `${body.pageCount} pages · `;
        caption.append(prefix);
        for (const [idx, [label, url]] of [[body.linkLabel, body.viewUrl], [body.reuseLabel, body.publicUrl + "reuse"]].entries()) {
          if (idx > 0) caption.append(" · ");
          const link = document.createElement("a");
          link.textContent = label;
          link.href = url;
          link.addEventListener("click", (event) => { event.preventDefault(); details.open = true; });
          caption.append(link);
        }

        details.hidden = false;
        details.querySelector("summary").textContent =
          `Public series reader preview · ${body.pageCount} pages (${body.generatorLabel})${body.inputCount ? ` · ${body.inputCount} inputs` : ""} · contact sheet`;
        frame.src = apiUrl(body.previewUrl);
      } catch (error) {
        if (error?.name === "AbortError") return;
        if (!finished && mySeq === prepareSeq) {
          status.textContent = error.message;
          destination.textContent = "Sharing unavailable";
        }
      }
    }

    cancel.textContent = "Cancel";
    cancel.addEventListener("click", onCancel);
    dialog.addEventListener("cancel", onCancel);
    confirm.addEventListener("click", onConfirm);
    renderVariantPicker();
    prepareCurrentSelection();
  });
};
