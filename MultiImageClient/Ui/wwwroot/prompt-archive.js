"use strict";

(() => {
  const $ = id => document.getElementById(id);
  const fmt = new Intl.NumberFormat("en-US");
  window.MultiImageTelemetry?.init({ page: "archive", apiUrl: (p) => p });

  const state = {
    stats: null,
    mode: "random", // "random" | "search"
    page: 1,
    pageSize: 25,
    totalPages: 1,
    seenRandomIds: [],
    activePreset: "",
    sourceLabels: new Map(),
  };

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

  async function apiPost(path) {
    const resp = await fetch(path, {
      method: "POST",
      cache: "no-store",
      headers: { "X-Mic-Manage": "1" },
    });
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

  function formatDateRange(firstUtc, lastUtc) {
    const a = formatDate(firstUtc);
    const b = formatDate(lastUtc);
    if (a === "Undated" && b === "Undated") return "Undated (Early 2022)";
    if (a === b) return a;
    if (a === "Undated") return b;
    if (b === "Undated") return a;
    return `${a} → ${b}`;
  }

  function currentSourceFilter() {
    if (state.activePreset) return state.activePreset;
    return $("pa-source").value;
  }

  function buildFilterParams() {
    const params = new URLSearchParams();
    const q = $("pa-query").value.trim();
    const source = currentSourceFilter();
    const year = $("pa-year").value;
    const kind = $("pa-kind").value;
    const minChars = $("pa-min-chars").value;
    if (q) params.set("q", q);
    if (source) params.set("source", source);
    if (year !== "") params.set("year", year);
    if (kind) params.set("kind", kind);
    if (minChars) params.set("minChars", minChars);
    return params;
  }

  function updateRandomLabels() {
    const n = Number($("pa-count").value) || 10;
    $("pa-random-btn").textContent = `View ${n} random prompts`;
    $("pa-reroll-top").textContent = `Reroll ${n}`;
    const seen = state.seenRandomIds.length;
    const resetBtn = $("pa-reset-seen");
    resetBtn.hidden = seen === 0;
    resetBtn.textContent = `Reset seen (${seen})`;
  }

  function setStatus(message, isError = false) {
    const elStatus = $("pa-status");
    elStatus.textContent = message;
    elStatus.classList.toggle("pa-status-error", isError);
  }

  function renderStats(stats) {
    state.stats = stats;
    state.sourceLabels.clear();
    if (!stats || !stats.exists) {
      $("stat-unique").textContent = "0";
      $("stat-runs").textContent = "0";
      $("stat-sources").textContent = "0";
      $("stat-span").textContent = "—";
      $("pa-empty").hidden = false;
      $("pa-empty").textContent = "No prompt archive database is loaded on this instance yet. Click \"Sync this instance\" to initialize from this instance's owner jobs, or install the full prompt-archive.sqlite3 database.";
      setStatus("Prompt archive database not found on this instance.", true);
      return;
    }
    $("pa-empty").hidden = true;
    $("stat-unique").textContent = fmt.format(stats.totalPrompts);
    $("stat-runs").textContent = fmt.format(stats.totalOccurrences);
    $("stat-sources").textContent = fmt.format(stats.sources.length);

    const datedYears = stats.years.map(y => y.year).filter(y => y > 0).sort((a, b) => a - b);
    $("stat-span").textContent = datedYears.length
      ? `${datedYears[0]}–${datedYears[datedYears.length - 1]}`
      : "Undated";

    // Populate source dropdown
    const prevSource = $("pa-source").value;
    const sourceSel = $("pa-source");
    sourceSel.replaceChildren(el("option", "", "All sources"));
    sourceSel.firstChild.value = "";
    for (const s of stats.sources) {
      state.sourceLabels.set(s.id, s.label);
      const opt = el("option", "", `${s.label} (${fmt.format(s.uniquePrompts)})`);
      opt.value = s.id;
      sourceSel.append(opt);
    }
    if ([...sourceSel.options].some(o => o.value === prevSource)) {
      sourceSel.value = prevSource;
    }

    // Populate year dropdown
    const prevYear = $("pa-year").value;
    const yearSel = $("pa-year");
    yearSel.replaceChildren(el("option", "", "All years"));
    yearSel.firstChild.value = "";
    for (const y of stats.years) {
      const label = y.year === 0
        ? `Undated / Early 2022 (${fmt.format(y.count)})`
        : `${y.year} (${fmt.format(y.count)})`;
      const opt = el("option", "", label);
      opt.value = String(y.year);
      yearSel.append(opt);
    }
    if ([...yearSel.options].some(o => o.value === prevYear)) {
      yearSel.value = prevYear;
    }

    // Populate sources summary table (strictly single-line rows)
    const tbody = $("pa-sources-tbody");
    tbody.replaceChildren();
    for (const s of stats.sources) {
      const tr = el("tr");
      tr.append(
        el("td", "", s.label),
        el("td", "", s.category),
        el("td", "pa-num", fmt.format(s.uniquePrompts)),
        el("td", "pa-num", fmt.format(s.totalOccurrences)),
        el("td", "", formatDate(s.earliestUtc)),
        el("td", "", formatDate(s.latestUtc)),
      );
      const tdAct = el("td");
      const rollBtn = el("button", "pa-btn pa-btn-sm", "10 random");
      rollBtn.type = "button";
      rollBtn.addEventListener("click", () => {
        setPreset("");
        $("pa-source").value = s.id;
        state.seenRandomIds = [];
        loadRandom();
        window.scrollTo({ top: 0, behavior: "smooth" });
      });
      tdAct.append(rollBtn);
      tr.append(tdAct);
      tbody.append(tr);
    }
  }

  function renderPromptCard(item, indexOffset) {
    const card = el("article", "pa-card");
    card.dataset.promptId = String(item.id);

    const head = el("div", "pa-card-head");
    head.append(
      el("span", "pa-card-num", `#${indexOffset}`),
      el("span", "pa-card-date", formatDateRange(item.firstSeenUtc, item.lastSeenUtc)),
    );

    for (const sid of item.sources || []) {
      const label = state.sourceLabels.get(sid) || sid;
      head.append(el("span", "pa-badge", label));
    }

    const models = (item.models || []).slice(0, 4);
    for (const m of models) {
      head.append(el("span", "pa-badge pa-badge-model", m));
    }
    if ((item.models || []).length > 4) {
      head.append(el("span", "pa-badge pa-badge-model", `+${item.models.length - 4} models`));
    }

    if (item.occurrenceCount > 1) {
      head.append(el("span", "pa-badge pa-badge-count", `×${item.occurrenceCount}`));
    }

    if (item.kind && item.kind !== "image") {
      head.append(el("span", "pa-badge pa-badge-meta", item.kind));
    }

    head.append(el("span", "pa-badge pa-badge-meta", `${fmt.format(item.charLen)} chars`));

    const actions = el("div", "pa-card-actions");

    const tryUrl = `./?archivePrompt=${item.id}`;
    const tryLink = el("a", "pa-btn pa-btn-green pa-btn-sm", "Try in composer");
    tryLink.href = tryUrl;
    actions.append(tryLink);

    const tabLink = el("a", "pa-btn pa-btn-sm", "New tab");
    tabLink.href = tryUrl;
    tabLink.target = "_blank";
    tabLink.rel = "noopener";
    actions.append(tabLink);

    const copyBtn = el("button", "pa-btn pa-btn-sm", "Copy");
    copyBtn.type = "button";
    copyBtn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(item.promptText);
        copyBtn.textContent = "Copied";
        setTimeout(() => { copyBtn.textContent = "Copy"; }, 1600);
      } catch {
        copyBtn.textContent = "Copy failed";
        setTimeout(() => { copyBtn.textContent = "Copy"; }, 1600);
      }
    });
    actions.append(copyBtn);

    if (item.canOpenJob && item.originJobId) {
      const origLink = el("a", "pa-btn pa-btn-sm", "Original job");
      origLink.href = `./?job=${encodeURIComponent(item.originJobId)}`;
      origLink.target = "_blank";
      origLink.rel = "noopener";
      actions.append(origLink);
    }

    const detailsBtn = el("button", "pa-btn pa-btn-sm", "Details");
    detailsBtn.type = "button";
    detailsBtn.setAttribute("aria-expanded", "false");
    actions.append(detailsBtn);

    head.append(actions);
    card.append(head);

    const fullText = item.promptText || "";
    const PREVIEW_LIMIT = 1200;
    const isLong = fullText.length > PREVIEW_LIMIT;
    const body = el("p", "pa-prompt-text", isLong ? fullText.slice(0, PREVIEW_LIMIT) + "…" : fullText);
    card.append(body);

    if (isLong) {
      let expanded = false;
      const expandBtn = el("button", "pa-btn pa-btn-sm pa-expand-btn", `Show full prompt (${fmt.format(fullText.length)} chars)`);
      expandBtn.type = "button";
      expandBtn.addEventListener("click", () => {
        expanded = !expanded;
        body.textContent = expanded ? fullText : fullText.slice(0, PREVIEW_LIMIT) + "…";
        expandBtn.textContent = expanded ? "Collapse prompt" : `Show full prompt (${fmt.format(fullText.length)} chars)`;
      });
      card.append(expandBtn);
    }

    const detailsBox = el("div", "pa-details-box");
    detailsBox.hidden = true;
    card.append(detailsBox);

    let detailsLoaded = false;
    detailsBtn.addEventListener("click", async () => {
      const willOpen = detailsBox.hidden;
      detailsBox.hidden = !willOpen;
      detailsBtn.setAttribute("aria-expanded", String(willOpen));
      if (!willOpen || detailsLoaded) return;
      detailsBox.textContent = "Loading occurrence details…";
      try {
        const data = await apiGet(`api/admin/prompt-archive/items/${item.id}`);
        if (!data) return;
        detailsLoaded = true;
        renderDetailsBox(detailsBox, data);
      } catch (err) {
        detailsBox.textContent = `Could not load details: ${err.message}`;
      }
    });

    return card;
  }

  function summarizeOccDetails(details) {
    if (!details || typeof details !== "object") return "—";
    const parts = [];
    for (const [k, v] of Object.entries(details)) {
      if (v === null || v === undefined || v === "") continue;
      parts.push(`${k}: ${v}`);
    }
    return parts.length ? parts.join(" · ") : "—";
  }

  function renderDetailsBox(container, data) {
    container.replaceChildren();
    const header = el("div", "pa-status-text", `Occurrences shown: ${data.occurrences.length} of ${fmt.format(data.totalOccurrences)}`);
    header.style.marginBottom = "8px";
    container.append(header);

    const wrap = el("div", "pa-table-wrap");
    const table = el("table", "pa-table");
    const thead = el("thead");
    const hrow = el("tr");
    for (const col of ["Date", "Source", "Model", "Extra metadata", "Job"]) {
      hrow.append(el("th", "", col));
    }
    thead.append(hrow);
    table.append(thead);

    const tbody = el("tbody");
    for (const occ of data.occurrences) {
      const tr = el("tr");
      tr.append(
        el("td", "", occ.occurredAtUtc ? String(occ.occurredAtUtc).replace("T", " ").slice(0, 19) : "Undated"),
        el("td", "", state.sourceLabels.get(occ.sourceId) || occ.sourceId),
        el("td", "", occ.model || "—"),
        el("td", "", summarizeOccDetails(occ.details)),
      );
      const tdJob = el("td");
      if (occ.canOpenJob && occ.originJobId) {
        const link = el("a", "pa-btn pa-btn-sm", "Open job");
        link.href = `./?job=${encodeURIComponent(occ.originJobId)}`;
        link.target = "_blank";
        link.rel = "noopener";
        tdJob.append(link);
      } else {
        tdJob.textContent = occ.originInstance ? `${occ.originInstance}` : "—";
      }
      tr.append(tdJob);
      tbody.append(tr);
    }
    table.append(tbody);
    wrap.append(table);
    container.append(wrap);
  }

  async function loadRandom() {
    state.mode = "random";
    $("pa-pager").hidden = true;
    $("pa-reroll-top").hidden = false;
    $("pa-sort-wrap").hidden = true;

    const n = Number($("pa-count").value) || 10;
    const params = buildFilterParams();
    params.set("count", String(n));
    if (state.seenRandomIds.length > 0) {
      params.set("exclude", state.seenRandomIds.slice(-200).join(","));
    }

    setStatus(`Picking ${n} random prompts…`);
    try {
      const data = await apiGet(`api/admin/prompt-archive/random?${params.toString()}`);
      if (!data) return;
      if (!data.exists) {
        renderStats({ exists: false });
        $("pa-results").replaceChildren();
        return;
      }
      const items = data.items || [];
      if (data.exhaustedSeen) {
        state.seenRandomIds = [];
      }
      for (const it of items) {
        if (!state.seenRandomIds.includes(it.id)) {
          state.seenRandomIds.push(it.id);
        }
      }
      updateRandomLabels();

      const results = $("pa-results");
      results.replaceChildren();
      items.forEach((it, idx) => results.append(renderPromptCard(it, idx + 1)));

      if (items.length === 0) {
        setStatus("No prompts matched the active filters.");
      } else {
        const note = data.exhaustedSeen
          ? ` (wrapped after viewing all ${fmt.format(data.matchingCount)} matching prompts)`
          : "";
        setStatus(`Showing ${items.length} random prompts out of ${fmt.format(data.matchingCount)} matching${note}.`);
      }
    } catch (err) {
      setStatus(err.message, true);
    }
  }

  async function loadSearch(page = 1) {
    state.mode = "search";
    state.page = Math.max(1, page);
    $("pa-reroll-top").hidden = true;
    $("pa-sort-wrap").hidden = false;

    const params = buildFilterParams();
    params.set("sort", $("pa-sort").value);
    params.set("page", String(state.page));
    params.set("pageSize", String(state.pageSize));

    setStatus("Searching archive…");
    try {
      const data = await apiGet(`api/admin/prompt-archive/search?${params.toString()}`);
      if (!data) return;
      if (!data.exists) {
        renderStats({ exists: false });
        $("pa-results").replaceChildren();
        $("pa-pager").hidden = true;
        return;
      }
      const items = data.items || [];
      state.totalPages = data.totalPages || 1;
      const offsetBase = (data.page - 1) * data.pageSize;

      const results = $("pa-results");
      results.replaceChildren();
      items.forEach((it, idx) => results.append(renderPromptCard(it, offsetBase + idx + 1)));

      if (data.totalMatching === 0) {
        setStatus("No prompts matched your search and filters.");
        $("pa-pager").hidden = true;
      } else {
        const start = offsetBase + 1;
        const end = offsetBase + items.length;
        setStatus(`Showing ${fmt.format(start)}–${fmt.format(end)} of ${fmt.format(data.totalMatching)} matching prompts.`);
        $("pa-pager").hidden = state.totalPages <= 1;
        $("pa-page-info").textContent = `Page ${fmt.format(data.page)} of ${fmt.format(state.totalPages)}`;
        $("pa-prev-page").disabled = data.page <= 1;
        $("pa-next-page").disabled = data.page >= state.totalPages;
      }
    } catch (err) {
      setStatus(err.message, true);
    }
  }

  function setPreset(presetValue) {
    state.activePreset = presetValue;
    for (const chip of $("pa-presets").querySelectorAll(".pa-chip")) {
      chip.classList.toggle("active", chip.dataset.preset === presetValue);
    }
    if (presetValue) {
      $("pa-source").value = "";
    }
  }

  function rerunCurrentMode() {
    state.seenRandomIds = [];
    updateRandomLabels();
    if (state.mode === "search") {
      loadSearch(1);
    } else {
      loadRandom();
    }
  }

  async function init() {
    if (window.MicEnvironment?.adminUrl) {
      const adminLink = $("pa-admin-link");
      adminLink.href = window.MicEnvironment.adminUrl;
      adminLink.hidden = false;
    }

    $("pa-random-btn").addEventListener("click", () => loadRandom());
    $("pa-reroll-top").addEventListener("click", () => loadRandom());
    $("pa-count").addEventListener("change", () => {
      updateRandomLabels();
      loadRandom();
    });
    $("pa-reset-seen").addEventListener("click", () => {
      state.seenRandomIds = [];
      updateRandomLabels();
      loadRandom();
    });

    $("pa-search-form").addEventListener("submit", e => {
      e.preventDefault();
      $("pa-clear-query").hidden = !$("pa-query").value.trim();
      loadSearch(1);
    });
    $("pa-query").addEventListener("input", () => {
      $("pa-clear-query").hidden = !$("pa-query").value.trim();
    });
    $("pa-clear-query").addEventListener("click", () => {
      $("pa-query").value = "";
      $("pa-clear-query").hidden = true;
      state.seenRandomIds = [];
      loadRandom();
    });

    $("pa-source").addEventListener("change", () => {
      setPreset("");
      rerunCurrentMode();
    });
    $("pa-year").addEventListener("change", rerunCurrentMode);
    $("pa-kind").addEventListener("change", rerunCurrentMode);
    $("pa-min-chars").addEventListener("change", rerunCurrentMode);
    $("pa-sort").addEventListener("change", () => loadSearch(1));

    for (const chip of $("pa-presets").querySelectorAll(".pa-chip")) {
      chip.addEventListener("click", () => {
        setPreset(chip.dataset.preset || "");
        rerunCurrentMode();
      });
    }

    $("pa-prev-page").addEventListener("click", () => {
      if (state.page > 1) {
        loadSearch(state.page - 1);
        window.scrollTo({ top: 0, behavior: "smooth" });
      }
    });
    $("pa-next-page").addEventListener("click", () => {
      if (state.page < state.totalPages) {
        loadSearch(state.page + 1);
        window.scrollTo({ top: 0, behavior: "smooth" });
      }
    });

    $("pa-sync-btn").addEventListener("click", async () => {
      const btn = $("pa-sync-btn");
      const status = $("pa-sync-status");
      btn.disabled = true;
      status.textContent = "Syncing…";
      try {
        const res = await apiPost("api/admin/prompt-archive/sync");
        if (!res) return;
        status.textContent = `Synced (+${fmt.format(res.addedPrompts)} new prompts, +${fmt.format(res.addedOccurrences)} runs; ${fmt.format(res.totalPrompts)} total).`;
        const stats = await apiGet("api/admin/prompt-archive/stats");
        if (stats) renderStats(stats);
        if (state.mode === "random" && $("pa-results").children.length === 0) {
          await loadRandom();
        }
      } catch (err) {
        status.textContent = err.message;
      } finally {
        btn.disabled = false;
      }
    });

    updateRandomLabels();

    apiGet("api/config").then((cfg) => {
      const buildNode = $("build-info");
      if (buildNode && cfg?.build?.commit && window.MultiImageBuildBadge) {
        window.MultiImageBuildBadge.render(buildNode, cfg.build);
      }
    }).catch((err) => {
      console.error("Failed to load build info:", err);
    });

    try {
      const stats = await apiGet("api/admin/prompt-archive/stats");
      if (!stats) return;
      renderStats(stats);
      if (stats.exists) {
        await loadRandom();
      }
    } catch (err) {
      setStatus(err.message, true);
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init, { once: true });
  } else {
    init();
  }
})();
