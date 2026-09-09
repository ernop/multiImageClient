"use strict";
const $ = id => document.getElementById(id);
let state;
async function api(path, body, json = false) {
  const response = await fetch(path, body === undefined ? { cache: "no-store" } : {
    method: "POST", cache: "no-store", headers: { "X-Mic-Manage": "1", ...(json ? { "Content-Type": "application/json" } : {}) },
    body: json ? JSON.stringify(body) : new URLSearchParams(body),
  });
  if (!response.ok) { let text = "Request failed (" + response.status + ")."; try { text = (await response.json()).error || text; } catch {} throw Error(text); }
  return response.json();
}
function node(tag, text) { const element = document.createElement(tag); if (text !== undefined) element.textContent = text; return element; }
function field(form, label, name, value, type = "text") {
  const row = node("label", label + " "); const input = node("input"); input.name = name; input.type = type;
  if (type === "checkbox") input.checked = !!value; else input.value = value;
  row.append(input); form.append(row); return input;
}
async function run(action) { try { await action(); } catch (error) { $("status").textContent = error.message; } }
function issued(result) {
  $("issued").hidden = false; $("link").value = result.url;
  $("credentials").textContent = result.password ? "Username: " + result.username + " · Password: " + result.password : "";
  $("issued").scrollIntoView({ block: "center" });
}
function date(value) { return value ? new Date(value).toLocaleString() : "Not recorded"; }
async function load() {
  state = await api("api/control/state"); $("environments").replaceChildren(); $("person-environment").replaceChildren();
  const catalog = (await api("api/config")).generators.filter(g => g.available);
  for (const env of state.environments) {
    const option = node("option", env.name); option.value = env.id; $("person-environment").append(option);
    const section = node("section"), heading = node("h3", env.name); section.append(heading);
    const provision = state.provisioning?.[env.id];
    if (provision) section.append(node("p", provision.status === "ready" ? "Ready" : provision.error));
    const link = node("a", "Open environment"); link.href = "/" + env.slug + "/"; section.append(link);
    const form = node("form"); const name = field(form, "Display name", "name", env.name); name.maxLength = 80; name.required = true;
    const slug = field(form, "URL name", "slug", env.original ? "Existing private route (preserved)" : env.slug);
    slug.disabled = env.original; slug.pattern = "[a-z0-9][a-z0-9-]{0,63}";
    const features = node("div"); features.className = "features";
    const goals = field(features, "Goal loops", "goals", env.goalLoops, "checkbox");
    const video = field(features, "Video generation", "video", env.video, "checkbox");
    const rewrite = field(features, "Prompt rewriting", "rewrite", env.promptRewrite, "checkbox"); form.append(features);
    const defaults = node("fieldset"); defaults.append(node("legend", "Default providers"));
    const selected = new Set(env.defaultGenerators ?? catalog.filter(g => g.defaultOn).map(g => g.key));
    const providers = catalog.map(g => field(defaults, g.label, g.key, selected.has(g.key), "checkbox")); form.append(defaults);
    const members = node("fieldset"); members.append(node("legend", "Membership"));
    const boxes = state.accounts.filter(a => a.role !== "admin").map(account => {
      const input = field(members, account.name, account.login, env.members.includes(account.login), "checkbox"); return input;
    }); form.append(members); form.append(node("p", "Your admin account can access every environment."));
    form.append(node("button", "Save configuration")); section.append(form);
    form.addEventListener("submit", event => { event.preventDefault(); run(async () => {
      await api("api/control/environment", { ...env, name: name.value.trim(), slug: env.original ? env.slug : slug.value.trim(),
        goalLoops: goals.checked, video: video.checked, promptRewrite: rewrite.checked,
        members: boxes.filter(b => b.checked).map(b => b.name), defaultGenerators: providers.filter(b => b.checked).map(b => b.name) }, true);
      $("status").textContent = "Configuration saved. URL changes take effect after provisioning finishes.";
    }); });
    const summary = node("p", "Reading activity…"); section.append(summary);
    const wrap = node("div"); wrap.className = "table"; const table = node("table");
    const header = node("tr"); for (const title of ["Account", "Level", "First login", "Last login", "Last active", "Jobs submitted", "Access"]) header.append(node("th", title));
    table.append(header); wrap.append(table); section.append(wrap); $("environments").append(section);
    let usage; try { usage = await api("/" + env.slug + "/api/admin/summary"); summary.textContent = "Login tracking starts with this release. Jobs include recorded historical submissions, including failures."; }
    catch { summary.textContent = "Environment is not reachable yet. Refresh after provisioning finishes."; }
    for (const account of state.accounts.filter(a => a.role === "admin" || env.members.includes(a.login))) {
      const activity = usage?.accounts?.[account.login]; const generation = usage?.generations?.find(g => g.login === account.login);
      const row = node("tr"); for (const text of [account.name, account.role, date(activity?.firstLogin), date(activity?.lastLogin),
        date(activity?.lastActive), usage ? String(generation?.jobsSubmitted || 0) : "Unknown"]) row.append(node("td", text));
      const controls = node("td");
      if (account.id) for (const action of ["replace", "revoke"]) {
        const button = node("button", action === "replace" ? "New link" : account.revoked ? "Revoked" : "Revoke account"); button.type = "button";
        button.disabled = action === "revoke" && account.revoked;
        button.addEventListener("click", () => run(async () => {
          if (!confirm(action === "revoke" ? "Revoke this account across every environment?" : "Replace this account's link across every environment?")) return;
          const result = await api(`api/control/accounts/${account.id}/${action}`, { environment: env.id });
          if (result.url) issued(result); else await load();
        })); controls.append(button);
      }
      row.append(controls); table.append(row);
    }
  }
}
$("create").addEventListener("submit", event => { event.preventDefault(); run(async () => {
  const form = new FormData(event.target); const slug = form.get("slug").trim();
  await api("api/control/environment?create=true", { id: slug, slug, name: form.get("name").trim(), original: false,
    goalLoops: true, video: true, promptRewrite: true, members: [], defaultGenerators: ["gpt2", "googlepro"] }, true);
  event.target.reset(); await load(); $("status").textContent = "Environment requested. Refresh to check its status.";
}); });
$("person").addEventListener("submit", event => { event.preventDefault(); run(async () => {
  const result = await api("api/control/accounts", Object.fromEntries(new FormData(event.target))); await load(); issued(result);
}); });
$("copy").addEventListener("click", () => run(async () => { await navigator.clipboard.writeText($("link").value); $("status").textContent = "Login link copied."; }));
$("refresh").addEventListener("click", () => run(load));
run(async () => { await load(); $("status").textContent = "Only your admin account can manage environments and accounts."; });
