"use strict";
// Join only recorded turn/source/variant identities. Failed replies never supply scores.
(function (root) {
  function collect(loop, entries, resolve) {
    const protocol = loop.protocolVersion;
    const latestReplies = new Map();
    if (protocol >= 7) for (const e of entries) {
      if (e.error) continue;
      const who = e.kind === "review" && e.manager?.parsed ? "manager"
        : e.kind === "critique" && e.critic?.parsed ? `critic-${e.critic.index}` : null;
      if (who) {
        const key = `${e.turn}/${who}`;
        if (!latestReplies.has(key) || e.index > latestReplies.get(key).index) latestReplies.set(key, e);
      }
    }
    const generators = loop.generators?.length
      ? loop.generators
      : [{ key: loop.generatorKey, label: loop.generatorLabel, source: null }];
    const participants = [
      { id: "manager", icon: "M", label: loop.managerLabel, role: "Manager" },
      ...(loop.critics || []).map((c) => ({
        id: `critic-${c.index}`,
        icon: `C${c.index + 1}`,
        label: c.label,
        role: `Critic ${c.index + 1}`,
      })),
      ...generators.map((g) => ({
        id: g.key,
        icon: g.source || "I",
        label: g.label,
        role: "Image generator",
      })),
    ];
    const items = [],
      failures = [];
    const evaluations = (p) =>
      protocol >= 5
        ? p.renderEvaluations || []
        : [
            {
              variant: protocol >= 4 ? "refine" : null,
              source: null,
              evaluation: p.evaluation,
            },
            { variant: "fresh", source: null, evaluation: p.freshEvaluation },
          ].filter((s) => s.evaluation);
    for (const entry of entries) {
      if (entry.kind !== "render-result") continue;
      const r = entry.render;
      if (!r.ok) {
        failures.push({
          turn: entry.turn,
          label: r.generatorLabel,
          message: entry.error || entry.text,
        });
        continue;
      }
      const requests = entries.filter(
        (e) =>
          e.kind === "render-request" &&
          e.turn === entry.turn &&
          e.render?.jobId === r.jobId &&
          (e.render.variant || null) === (r.variant || null) &&
          (e.render.source || null) === (r.source || null),
      );
      if (requests.length !== 1)
        throw Error(`Expected one exact request for render ${entry.index}.`);
      if (!r.imageUrl || !r.thumbUrl)
        throw Error(
          `Render ${entry.index} lacks an original or recorded thumbnail.`,
        );
      const item = {
        id: String(entry.index),
        turn: entry.turn,
        variant: r.variant || null,
        source: r.source || null,
        generator: r.generatorKey,
        label: r.generatorLabel,
        prompt: requests[0].text,
        url: resolve(r.imageUrl),
        thumb: resolve(r.thumbUrl),
        comments: [],
        score: null,
        best: false,
      };
      for (const reply of entries) {
        if (reply.turn !== entry.turn || reply.error) continue;
        let rows, who;
        if (reply.kind === "review" && reply.manager?.parsed) {
          rows = evaluations(reply.manager.parsed);
          who = "manager";
        } else if (reply.kind === "critique" && reply.critic?.parsed) {
          rows = reply.critic.parsed.critiques.map((c) => ({
            variant: c.variant,
            source: c.source,
            evaluation: c,
          }));
          who = `critic-${reply.critic.index}`;
        } else continue;
        if (protocol >= 7 && latestReplies.get(`${reply.turn}/${who}`) !== reply) continue;
        const hits = rows.filter(
          (s) =>
            (s.variant || null) === item.variant &&
            (s.source || null) === item.source,
        );
        if (hits.length !== 1 || item.comments.some((c) => c.who === who))
          throw Error(`Ambiguous or missing score for render ${entry.index}.`);
        const ev = hits[0].evaluation;
        item.comments.push({
          who,
          entry: reply.index,
          score: ev.score,
          assessment: ev.assessment,
          components: ev.components || null,
          problems: ev.problems || [],
          ideas: ev.ideas || [],
          keep: ev.keep || [],
        });
        if (who === "manager") item.score = ev.score;
      }
      item.best =
        item.turn === loop.bestTurn &&
        item.variant === (loop.bestVariant || null) &&
        item.source === (loop.bestSource || null);
      items.push(item);
    }
    items.sort(
      (a, b) =>
        a.turn - b.turn ||
        a.generator.localeCompare(b.generator) ||
        variantOrder(a.variant) - variantOrder(b.variant),
    );
    const cuts = { all: items.map((i) => i.id), best: [], latest: [] };
    for (const g of generators) {
      const group = items.filter((i) => i.generator === g.key);
      if (!group.length) continue;
      cuts[g.key] = group.map((i) => i.id);
      cuts.latest.push(
        ...group
          .filter((i) => i.turn === Math.max(...group.map((x) => x.turn)))
          .map((i) => i.id),
      );
      const scored = group.filter((i) => i.score !== null);
      if (scored.length)
        cuts.best.push(
          ...scored
            .filter((i) => i.score === Math.max(...scored.map((x) => x.score)))
            .map((i) => i.id),
        );
    }
    if (loop.protocolVersion >= 7) cuts.best = items.filter((i) => i.best).map((i) => i.id);
    const contributions = entries
      .filter((e) => ["design", "review", "critique"].includes(e.kind))
      .map((e) => {
        const p = e.kind === "critique" ? e.critic?.parsed : e.manager?.parsed;
        return {
          turn: e.turn,
          who: e.kind === "critique" ? `critic-${e.critic.index}` : "manager",
          kind: e.kind,
          error: !!e.error,
          text: e.text,
          summary: p?.reasoning || p?.overall || e.error || "",
        };
      });
    return {
      id: loop.id,
      goal: loop.goal,
      status: loop.status,
      statusDetail: loop.statusDetail || "",
      protocolVersion: loop.protocolVersion || 1,
      rubric: entries.find((e) => e.kind === "design" && !e.error)?.manager?.parsed?.rubric || [],
      participants,
      items,
      cuts,
      failures,
      contributions,
    };
  }
  function variantOrder(v) { return /^c[1-3]-s[1-4]$/.test(v) ? (Number(v[1]) - 1) * 4 + Number(v[4]) - 1
    : ({ refine: 0, fresh: 1, c1: 0, c2: 1, c3: 2 })[v] ?? -1; }
  function variantLabel(v) { return /^c[1-3]-s[1-4]$/.test(v) ? `Candidate ${v[1]} · sample ${v[4]}`
    : /^c[1-3]$/.test(v) ? `Candidate ${v.slice(1)}` : v || "render"; }
  root.GoalRecap = { collect, variantLabel, variantOrder, shortName: label => label.replace(/^Claude /, "").replace(/ \((?:Anthropic|OpenAI|Google|xAI)\)$/, "") };
})(globalThis);
