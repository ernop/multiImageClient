/* Exact render lineage for protocol 8. No inferred parents or image substitutions. */
(function (root) {
  "use strict";
  const key = (turn, variant, source) => `${turn}/${variant}/${source}`;
  const candidateId = variant => /^c[1-3]-s[1-4]$/.test(variant) ? variant.slice(0, 2) : variant;
  function candidate(entries, turn, variant) {
    const design = entries.findLast(e => !e.error && e.manager?.parsed?.plan
      && (e.kind === "design" ? e.turn === turn : e.kind === "review" && e.turn === turn - 1));
    return design?.manager.parsed.plan.candidates.find(c => c.variant === candidateId(variant)) || null;
  }
  function renderPrompt(entries, entry) {
    const requests = entries.filter(e => e.kind === "render-request" && e.turn === entry.turn
      && e.render?.variant === entry.render?.variant && e.render?.source === entry.render?.source
      && (!entry.render.jobId || e.render.jobId === entry.render.jobId));
    return requests.length === 1 ? requests[0].text : null;
  }
  function collect(loop, entries) {
    if (loop.protocolVersion < 8) return { nodes: [], edges: [], errors: [] };
    const nodes = [], edges = [], errors = [], byKey = new Map();
    for (const entry of entries.filter(e => e.kind === "render-result")) {
      const r = entry.render;
      if (!r?.variant || !r.source) { errors.push(`Entry ${entry.index} lacks a render identity.`); continue; }
      const id = key(entry.turn, r.variant, r.source);
      if (byKey.has(id)) { errors.push(`Duplicate render identity: ${id}`); continue; }
      const plan = candidate(entries, entry.turn, r.variant);
      const review = entries.findLast(e => e.kind === "review" && e.turn === entry.turn && !e.error && e.manager?.parsed);
      const decision = review?.manager.parsed.candidateDecisions?.find(d => d.variant === candidateId(r.variant));
      const node = { id, entryIndex: entry.index, turn: entry.turn, variant: r.variant, source: r.source,
        title: plan?.title || id, mode: plan?.mode || "unknown", prompt: renderPrompt(entries, entry) || "Prompt record unavailable.",
        parents: plan?.parents || [], action: decision?.action || "unreviewed", reason: decision?.reason || "",
        ok: !!r.ok, thumbUrl: r.thumbUrl, best: entry.turn === loop.bestTurn
          && r.variant === loop.bestVariant && r.source === loop.bestSource };
      if (!plan) errors.push(`Missing candidate plan: ${id}`);
      nodes.push(node); byKey.set(id, node);
    }
    nodes.sort((a, b) => a.turn - b.turn || a.variant.localeCompare(b.variant) || a.source.localeCompare(b.source));
    for (const node of nodes) for (const p of node.parents) {
      const parent = byKey.get(key(p.turn, p.variant, p.source));
      if (!parent?.ok || parent.turn >= node.turn) { errors.push(`Unavailable parent for ${node.id}: ${key(p.turn, p.variant, p.source)}`); continue; }
      edges.push({ from: parent.id, to: node.id, contribution: p.contribution });
    }
    return { nodes, edges, errors };
  }
  function render(container, data, reveal, resolveUrl) {
    container.replaceChildren();
    const help = document.createElement("p");
    help.className = "goal-help";
    help.textContent = "Each image is a node. Lines show its source ideas. Select a node to open its work record.";
    container.appendChild(help);
    for (const error of data.errors) {
      const p = document.createElement("p"); p.className = "goal-error"; p.textContent = error; container.appendChild(p);
    }
    if (!data.nodes.length) { help.textContent = "The tree appears after the first generated result."; return; }
    const viewport = document.createElement("div"); viewport.className = "goal-tree-viewport";
    const canvas = document.createElement("div"); canvas.className = "goal-tree-canvas";
    const rows = [...new Set(data.nodes.map(n => n.turn))], positions = new Map();
    const columns = Math.max(...rows.map(t => data.nodes.filter(n => n.turn === t).length));
    const step = Math.max(116, Math.min(146, Math.floor((container.clientWidth - 76) / Math.min(columns, 3))));
    const nodeWidth = step - 10;
    const width = columns * step + 44;
    canvas.style.width = `${width}px`; canvas.style.height = `${rows.length * 262}px`;
    rows.forEach((turn, row) => {
      const label = document.createElement("span"); label.className = "goal-tree-turn";
      label.textContent = `Turn ${turn}`; label.style.top = `${row * 262 + 18}px`; canvas.appendChild(label);
      data.nodes.filter(n => n.turn === turn).forEach((node, col) => positions.set(node.id, { x: 44 + col * step, y: row * 262 + 18 }));
    });
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("width", width); svg.setAttribute("height", rows.length * 262); svg.setAttribute("aria-hidden", "true");
    svg.classList.add("goal-tree-edges");
    for (const edge of data.edges) {
      const a = positions.get(edge.from), b = positions.get(edge.to);
      const path = document.createElementNS(svg.namespaceURI, "path");
      const x1 = a.x + nodeWidth / 2, y1 = a.y + 210, x2 = b.x + nodeWidth / 2, y2 = b.y;
      path.setAttribute("d", `M${x1},${y1} C${x1},${y1 + 36} ${x2},${y2 - 36} ${x2},${y2}`);
      svg.appendChild(path);
    }
    canvas.appendChild(svg);
    for (const node of data.nodes) {
      const position = positions.get(node.id), button = document.createElement("button");
      button.type = "button"; button.className = `goal-tree-node action-${node.action}${node.best ? " is-best" : ""}`;
      button.style.left = `${position.x}px`; button.style.top = `${position.y}px`;
      button.style.width = `${nodeWidth}px`;
      button.title = [node.prompt, ...node.parents.map(p => `From ${key(p.turn, p.variant, p.source)}: ${p.contribution}`), node.reason].filter(Boolean).join("\n\n");
      const identity = document.createElement("span"); identity.className = "goal-tree-identity";
      identity.textContent = `${node.variant.toUpperCase()} · source ${node.source}${node.best ? " · Best" : ""}`;
      const preview = document.createElement("span"); preview.className = "goal-tree-preview";
      if (node.ok && node.thumbUrl) {
        const img = document.createElement("img"); img.src = resolveUrl(node.thumbUrl); img.alt = "";
        img.loading = "lazy"; img.decoding = "async"; preview.appendChild(img);
      } else preview.textContent = node.ok ? "Preview unavailable" : "Generation failed";
      const title = document.createElement("strong"); title.textContent = node.title;
      const state = document.createElement("span"); state.className = "goal-tree-state";
      state.textContent = `${node.mode} · idea: ${node.action}`;
      button.append(identity, preview, title, state);
      button.addEventListener("click", () => reveal(node.entryIndex));
      canvas.appendChild(button);
    }
    viewport.appendChild(canvas); container.appendChild(viewport);
  }
  root.GoalTree = { key, candidate, renderPrompt, collect, render };
})(typeof window !== "undefined" ? window : globalThis);
