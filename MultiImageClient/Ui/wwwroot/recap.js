"use strict";
const $ = (id) => document.getElementById(id);
const node = (tag, text, cls) => {
  const n = document.createElement(tag);
  if (text !== undefined) n.textContent = text;
  if (cls) n.className = cls;
  return n;
};
const resolve = (path) =>
  /^https?:|^data:/i.test(path)
    ? path
    : new URL(path.replace(/^\//, ""), new URL("./", location.href)).href;
let data,
  people,
  filtered = [],
  cut = "best",
  viewer;
const downloads = [];
function person(p) {
  const box = node("div", undefined, "person");
  box.append(node("span", p.icon, "icon"));
  const label = node("div");
  label.append(node("div", p.role, "role"), node("div", p.label));
  box.append(label);
  return box;
}
function comments(item, target) {
  for (const c of item.comments) {
    const box = node("details", undefined, "comment"),
      p = people.get(c.who);
    box.append(
      node("summary", `${p.role} · ${p.label} · ${c.score}/10`),
      node("p", c.assessment),
    );
    for (const field of ["problems", "ideas", "keep"])
      if (c[field].length) {
        box.append(node("strong", field));
        const ul = node("ul");
        for (const line of c[field]) ul.append(node("li", line));
        box.append(ul);
      }
    target.append(box);
  }
  if (item.score === null)
    target.prepend(node("p", "No accepted manager review."));
}
function render() {
  const ids = new Set(data.cuts[cut]);
  filtered = data.items.filter(
    (i) =>
      ids.has(i.id) &&
      (!$("generator").value || i.generator === $("generator").value),
  );
  $("grid").replaceChildren();
  document
    .querySelectorAll("[data-cut]")
    .forEach((b) =>
      b.setAttribute("aria-pressed", String(b.dataset.cut === cut)),
    );
  $("count").textContent = `${filtered.length} / ${data.items.length} images`;
  $("present").disabled = $("png").disabled = !filtered.length;
  for (const item of filtered) {
    const card = node("article", undefined, "card"),
      btn = node("button", undefined, "image-button"),
      img = node("img");
    img.src = item.thumb;
    img.alt = `${item.label}, turn ${item.turn}, ${item.variant || "render"}`;
    img.loading = "lazy";
    btn.append(img);
    btn.onclick = () => viewer.open(item.id);
    const body = node("div", undefined, "card-body"),
      head = node("div", undefined, "card-head");
    head.append(node("span", people.get(item.generator).icon, "icon"));
    const label = node("div");
    label.append(
      node("h3", item.label),
      node("div", `Turn ${item.turn} · ${item.variant || "render"}`),
    );
    head.append(
      label,
      node("span", item.score === null ? "—" : String(item.score), "score"),
    );
    body.append(head);
    if (item.best) body.append(node("span", "Recorded best", "tag"));
    const assessment = item.comments.find((c) => c.who === "manager");
    if (assessment) body.append(node("p", assessment.assessment));
    comments(item, body);
    card.append(btn, body);
    $("grid").append(card);
  }
}
function download(blob, name) {
  const url = URL.createObjectURL(blob);
  downloads.push(url);
  const a = node("a", name);
  a.href = url;
  a.download = name;
  $("downloads").append(a);
  a.click();
}
function clearDownloads() {
  for (const url of downloads) URL.revokeObjectURL(url);
  downloads.length = 0;
  $("downloads").replaceChildren();
}
async function response(url) {
  const r = await fetch(url);
  if (!r.ok) throw Error(`Could not load export resource (HTTP ${r.status}).`);
  return r;
}
async function image(url) {
  const img = new Image();
  img.crossOrigin = "anonymous";
  img.src = url;
  await img.decode();
  return img;
}
function lines(ctx, text, width, max) {
  const result = [];
  let line = "";
  for (const word of text.split(/\s+/)) {
    const next = (line + " " + word).trim();
    if (line && ctx.measureText(next).width > width) {
      result.push(line);
      line = word;
    } else line = next;
  }
  if (line) result.push(line);
  if (result.length > max) {
    result.length = max;
    result[max - 1] = result[max - 1].slice(0, -3) + "…";
  }
  return result;
}
async function png() {
  const selection = [...filtered],
    label = `${cut}-${$("generator").value || "all-generators"}`;
  clearDownloads();
  for (let start = 0; start < selection.length; start += 8) {
    $("message").textContent = `Building PNG page ${1 + start / 8}…`;
    const canvas = document.createElement("canvas");
    canvas.width = 1920;
    canvas.height = 140 + Math.ceil(Math.min(8, selection.length - start) / 4) * 590;
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = getComputedStyle(document.documentElement).getPropertyValue("--paper").trim();
    ctx.fillRect(0, 0, 1920, 1320);
    ctx.fillStyle = getComputedStyle(document.documentElement).getPropertyValue("--ink").trim();
    ctx.font = "bold 30px system-ui";
    ctx.fillText(`VISUAL RECAP / ${label}`, 24, 42);
    ctx.font = "18px system-ui";
    ctx.fillText(
      "Manager scores. All ties retained. Latest images may lack a review. Preview images, not full resolution.",
      24,
      76,
    );
    for (let j = 0; j < Math.min(8, selection.length - start); j++) {
      const item = selection[start + j],
        img = await image(item.thumb),
        x = 24 + (j % 4) * 474,
        y = 106 + Math.floor(j / 4) * 590,
        scale = Math.min(446 / img.width, 444 / img.height);
      ctx.drawImage(
        img,
        x + (446 - img.width * scale) / 2,
        y + (444 - img.height * scale) / 2,
        img.width * scale,
        img.height * scale,
      );
      ctx.font = "bold 18px system-ui";
      ctx.fillText(item.label, x, y + 477);
      ctx.font = "18px system-ui";
      ctx.fillText(
        `Turn ${item.turn} / ${item.variant || "render"} / ${item.score === null ? "not reviewed" : item.score + "/10"}`,
        x,
        y + 501,
      );
      const comment =
        item.comments.find((c) => c.who === "manager")?.assessment ||
        "No accepted manager review.";
      lines(ctx, comment, 446, 3).forEach((s, n) =>
        ctx.fillText(s, x, y + 526 + n * 23),
      );
    }
    const blob = await new Promise((ok, bad) =>
      canvas.toBlob(
        (b) => (b ? ok(b) : bad(Error("PNG encoding failed."))),
        "image/png",
      ),
    );
    download(blob, `${data.id}-${label}-${1 + start / 8}.png`);
    canvas.width = canvas.height = 0;
  }
  $("message").textContent =
    "PNG pages are ready. Each download link remains available below.";
}
async function portable() {
  clearDownloads();
  const copy = structuredClone(data);
  delete copy.siteHome;
  for (let n = 0; n < copy.items.length; n++) {
    const item = copy.items[n];
    $("message").textContent =
      `Embedding preview ${n + 1} / ${copy.items.length}…`;
    if (!item.thumb.startsWith("data:")) {
      const blob = await (await response(item.thumb)).blob();
      item.thumb = await new Promise((ok, bad) => {
        const r = new FileReader();
        r.onload = () => ok(r.result);
        r.onerror = () => bad(Error("Preview encoding failed."));
        r.readAsDataURL(blob);
      });
    }
    const original = new URL(item.url);
    if (original.protocol !== "https:" || original.origin === location.origin)
      throw Error(
        "Portable HTML requires externally hosted originals. This image uses the private app host.",
      );
  }
  const texts = await Promise.all(
    [
      "recap.html",
      "recap.css",
      "viewer.css",
      "viewer.js",
      "recap-model.js",
      "recap.js",
      "style.css",
      "goal.css",
    ].map(async (name) => (await response(resolve(name))).text()),
  );
  const doc = new DOMParser().parseFromString(texts[0], "text/html");
  for (const [name, text] of [["style.css", texts[6]], ["goal.css", texts[7]], ["recap.css", texts[1]], ["viewer.css", texts[2]]]) {
    const style = doc.createElement("style");
    style.textContent = text;
    doc.querySelector(`link[href="${name}"]`).replaceWith(style);
  }
  for (const [name, text] of [["viewer.js", texts[3]], ["recap-model.js", texts[4]], ["recap.js", texts[5]]]) {
    const script = doc.querySelector(`script[src="${name}"]`);
    script.removeAttribute("src");
    script.textContent = text.replace(/<\/script/gi, "<\\/script");
  }
  doc.getElementById("recap-data").textContent = JSON.stringify(copy).replace(/</g, "\\u003c");
  const html = "<!doctype html>\n" + doc.documentElement.outerHTML;
  download(new Blob([html], { type: "text/html" }), `${data.id}-recap.html`);
  $("message").textContent =
    "Portable HTML is ready. Previews and comments work offline. Original images require access to their recorded host.";
}
async function action(run) {
  $("png").disabled = $("html").disabled = true;
  try {
    await run();
  } catch (ex) {
    $("message").textContent = ex.message;
  } finally {
    $("png").disabled = !filtered.length;
    $("html").disabled = false;
  }
}
async function init() {
  const embedded = JSON.parse($("recap-data").textContent);
  if (embedded) {
    data = embedded;
    $("back").hidden = true;
    $("html").hidden = true;
  } else {
    const id = new URL(location.href).searchParams.get("loop");
    if (!id) throw Error("Open a visual recap from a goal conversation.");
    const r = await response(
      resolve(`api/goal-loops/${encodeURIComponent(id)}?after=0`),
    );
    const body = await r.json();
    if (body.total !== body.entries.length)
      throw Error("The conversation snapshot is incomplete. Reload to retry.");
    data = GoalRecap.collect(body.loop, body.entries, resolve);
    $("back").href = `goal.html?loop=${encodeURIComponent(id)}`;
  }
  const siteHome = embedded ? data.siteHome : new URL("./", location.href).href;
  if (siteHome) {
    $("site-home").href = $("site-title").href = siteHome;
    $("site-loops").href = new URL("goal.html", siteHome).href;
    const host = new URL(siteHome).hostname.toLowerCase();
    const online = host === "fuseki.net" || host.endsWith(".fuseki.net");
    $("environment-name").textContent = !embedded && window.MicEnvironment ? window.MicEnvironment.name : online ? "-alpha.fuseki.net" : "-local";
    document.querySelector("header").classList.add(online ? "environment-online" : "environment-local");
  } else {
    $("site-home").hidden = $("site-loops").hidden = true;
    $("site-title").removeAttribute("href");
  }
  if (embedded) $("environment-name").textContent += " · saved snapshot";
  for (const item of [...data.participants, ...data.items, ...data.failures]) item.label = GoalRecap.shortName(item.label);
  people = new Map(data.participants.map((p) => [p.id, p]));
  $("goal").textContent = data.goal;
  $("stats").textContent =
    `${data.items.length} images · ${new Set(data.items.map((i) => i.turn)).size} turns · ${data.participants.filter((p) => p.role === "Image generator").length} generators`;
  $("status").textContent = `${data.status}: ${data.statusDetail}`;
  for (const p of data.participants) {
    $("roster").append(person(p));
    if (p.role === "Image generator") {
      const option = node("option", p.label);
      option.value = p.id;
      $("generator").append(option);
    }
  }
  for (const c of data.contributions) {
    const d = node("details", undefined, "comment"),
      p = people.get(c.who);
    d.append(
      node(
        "summary",
        `Turn ${c.turn} · ${p.role} · ${p.label} · ${c.kind}${c.error ? " · failed reply" : ""}`,
      ),
      node("p", c.summary),
      node("pre", c.text),
    );
    $("contributions").append(d);
  }
  viewer = MultiImageViewer.create({
    items: () =>
      filtered.map((i) => ({
        id: i.id,
        url: i.url,
        thumbUrl: i.thumb,
        title: i.label,
        subtitle: `Turn ${i.turn} · ${i.variant || "render"} · ${i.score === null ? "No manager review" : i.score + "/10"}`,
        prompt: i.prompt,
        meta: i.comments.map((c) => ({
          label: `${people.get(c.who).role} · ${people.get(c.who).label} · ${c.score}/10`,
          value: [c.assessment, ...c.problems, ...c.ideas].join("\n"),
        })),
      })),
    resolveUrl: (u) => u,
  });
  for (const b of document.querySelectorAll("[data-cut]"))
    b.onclick = () => {
      cut = b.dataset.cut;
      render();
    };
  $("generator").onchange = render;
  $("present").onclick = () => viewer.open(filtered[0].id);
  $("png").onclick = () => action(png);
  $("html").onclick = () => action(portable);
  if (!data.cuts.best.length) cut = "all";
  render();
  $("message").textContent = "";
  if (data.failures.length)
    $("message").textContent +=
      "\n" +
      data.failures
        .map((f) => `Turn ${f.turn} · ${f.label}: ${f.message}`)
        .join("\n");
}
init().catch((ex) => {
  $("message").textContent = ex.message;
  for (const b of document.querySelectorAll("button")) b.disabled = true;
});
