"use strict";

(() => {
  const $ = id => document.getElementById(id);
  const fmt = new Intl.NumberFormat("en-US");
  const seenIds = [];
  const sourceLabels = new Map();

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  async function apiGet(path) {
    const resp = await fetch(path, { cache: "no-store" });
    if (resp.status === 401) { location.reload(); return null; }
    if (!resp.ok) {
      let msg = `Request failed (${resp.status}).`;
      try { const data = await resp.json(); if (data?.error) msg = data.error; } catch {}
      throw new Error(msg);
    }
    return resp.json();
  }

  function formatDate(utc) {
    if (!utc) return "Undated";
    return String(utc).slice(0, 10);
  }

  async function loadStats() {
    const summary = $("admin-archive-summary");
    try {
      const stats = await apiGet("api/admin/prompt-archive/stats");
      if (!stats) return;
      sourceLabels.clear();
      if (!stats.exists) {
        summary.textContent = "No prompt archive database is loaded on this instance yet. Open the Prompt Archive page to sync this instance.";
        return;
      }
      for (const s of stats.sources || []) sourceLabels.set(s.id, s.label);
      const years = (stats.years || []).map(y => y.year).filter(y => y > 0).sort((a, b) => a - b);
      const span = years.length ? `${years[0]}–${years[years.length - 1]}` : "Undated";
      summary.textContent = `${fmt.format(stats.totalPrompts)} unique prompts · ${fmt.format(stats.totalOccurrences)} total runs · ${stats.sources.length} sources · ${span}`;
    } catch (err) {
      summary.textContent = `Could not read prompt archive: ${err.message}`;
    }
  }

  async function loadTenRandom() {
    const btn = $("admin-archive-random-btn");
    const status = $("admin-archive-status");
    const list = $("admin-archive-list");
    btn.disabled = true;
    status.textContent = "Loading 10 random prompts…";
    try {
      const params = new URLSearchParams({ count: "10", kind: "image" });
      if (seenIds.length) params.set("exclude", seenIds.slice(-200).join(","));
      const data = await apiGet(`api/admin/prompt-archive/random?${params.toString()}`);
      if (!data) return;
      if (!data.exists) {
        status.textContent = "Prompt archive database is not loaded on this instance.";
        list.replaceChildren();
        return;
      }
      const items = data.items || [];
      if (data.exhaustedSeen) seenIds.length = 0;
      for (const it of items) {
        if (!seenIds.includes(it.id)) seenIds.push(it.id);
      }
      list.replaceChildren();
      items.forEach((item, idx) => {
        const card = el("article", "admin-pa-card");
        const head = el("div", "admin-pa-head");
        head.append(
          el("span", "admin-pa-num", `#${idx + 1}`),
          el("span", "admin-pa-date", formatDate(item.firstSeenUtc || item.lastSeenUtc)),
        );
        for (const sid of (item.sources || []).slice(0, 2)) {
          head.append(el("span", "admin-pa-badge", sourceLabels.get(sid) || sid));
        }
        if (item.occurrenceCount > 1) {
          head.append(el("span", "admin-pa-count", `×${item.occurrenceCount}`));
        }
        const actions = el("span", "admin-pa-actions");
        const tryLink = el("a", "admin-pa-link admin-pa-try", "Try in composer");
        tryLink.href = `./?archivePrompt=${item.id}`;
        const tabLink = el("a", "admin-pa-link", "New tab");
        tabLink.href = `./?archivePrompt=${item.id}`;
        tabLink.target = "_blank";
        tabLink.rel = "noopener";
        const copyBtn = el("button", "admin-pa-btn", "Copy");
        copyBtn.type = "button";
        copyBtn.addEventListener("click", async () => {
          try {
            await navigator.clipboard.writeText(item.promptText);
            copyBtn.textContent = "Copied";
            setTimeout(() => { copyBtn.textContent = "Copy"; }, 1400);
          } catch {
            copyBtn.textContent = "Copy failed";
          }
        });
        actions.append(tryLink, tabLink, copyBtn);
        head.append(actions);
        card.append(head);

        const body = el("p", "admin-pa-text", item.promptText.length > 900 ? item.promptText.slice(0, 900) + "…" : item.promptText);
        card.append(body);
        list.append(card);
      });
      status.textContent = items.length
        ? `Showing ${items.length} random image prompts out of ${fmt.format(data.matchingCount)}.`
        : "No prompts matched.";
      btn.textContent = "Reroll 10 random prompts";
    } catch (err) {
      status.textContent = err.message;
    } finally {
      btn.disabled = false;
    }
  }

  function init() {
    const btn = $("admin-archive-random-btn");
    if (!btn) return;
    btn.addEventListener("click", loadTenRandom);
    loadStats();
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init, { once: true });
  } else {
    init();
  }
})();
