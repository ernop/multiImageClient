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
function issued(result, username, environment, action) {
  $("issued").hidden = true;
  $("issued-title").textContent = "Login details for " + username;
  $("issued-context").textContent = action + " · " + environment;
  $("link").value = result.url || "";
  $("issued-username").value = result.username || "";
  $("issued-password").value = result.password || "";
  $("issued-username-row").hidden = !result.username;
  $("issued-password-row").hidden = !result.password;
  $("copy-details").hidden = !(result.url && result.username && result.password);
  $("issued").hidden = false;
  $("issued").scrollIntoView({ block: "start" });
}
function dismissIssued() {
  $("issued").hidden = true;
  for (const id of ["link", "issued-username", "issued-password"]) $(id).value = "";
  $("issued-title").textContent = "Login details";
  $("issued-context").textContent = "";
}
function detailsText() {
  const lines = ["Click this link to log in.", $("link").value];
  if (!$("issued-username-row").hidden) lines.push("", "Username: " + $("issued-username").value);
  if (!$("issued-password-row").hidden) lines.push("Password: " + $("issued-password").value);
  return lines.join("\n");
}
function date(value) { return value ? new Date(value).toLocaleString() : "Not recorded"; }
async function load() {
  state = await api("api/control/state"); $("environments").replaceChildren();
  const destination = $("person-environment").value;
  const placeholder = node("option", "Select an environment"); placeholder.value = ""; placeholder.disabled = true;
  $("person-environment").replaceChildren(placeholder);
  for (const env of state.environments) {
    const option = node("option", env.name); option.value = env.id; $("person-environment").append(option);
  }
  $("person-environment").value = state.environments.some(env => env.id === destination) ? destination : "";
  const catalog = (await api("api/config")).generators.filter(g => g.available);
  for (const env of state.environments) {
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
    const night = field(features, "Night filter", "night", env.nightFilter ?? true, "checkbox");
    const sharing = field(features, "Send to Vibecoders", "sharing", env.vibecodersSharing ?? env.original, "checkbox");
    const accountRequests = env.id === "vibecoders-ai-generation"
      ? field(features, "Discord account requests", "accountRequests", env.discordAccountRequests ?? false, "checkbox") : null;
    const targetLabel = node("label", "Target ");
    const target = node("select"); target.name = "discordShareTarget";
    for (const [value, label] of [["vibecoders", "Vibecoders"], ["bot-testing", "Bot testing"]]) {
      const option = node("option", label); option.value = value; target.append(option);
    }
    target.value = env.discordShareTarget ?? "vibecoders"; targetLabel.append(target); features.append(targetLabel);
    const defaults = node("fieldset"); defaults.append(node("legend", "Default providers"));
    const selected = new Set(env.defaultGenerators ?? catalog.filter(g => g.defaultOn).map(g => g.key));
    const providerRow = node("div"); providerRow.className = "generator-options";
    const providers = catalog.map(g => {
      const toggle = createGeneratorToggle(g, { checked: selected.has(g.key) });
      const input = toggle.querySelector("input"); input.name = g.key;
      providerRow.append(toggle); return input;
    });
    defaults.append(providerRow); form.append(defaults);
    const members = node("fieldset"); members.append(node("legend", "Membership"));
    const boxes = state.accounts.filter(a => a.role !== "admin").map(account => {
      const input = field(members, account.name, account.login, env.members.includes(account.login), "checkbox"); return input;
    }); form.append(members); form.append(node("p", "Your admin account can access every environment."));
    form.append(node("button", "Save configuration")); section.append(form);
    form.addEventListener("submit", event => { event.preventDefault(); run(async () => {
      await api("api/control/environment", { ...env, name: name.value.trim(), slug: env.original ? env.slug : slug.value.trim(),
        goalLoops: goals.checked, video: video.checked, promptRewrite: rewrite.checked, vibecodersSharing: sharing.checked, discordShareTarget: target.value, nightFilter: night.checked,
        discordAccountRequests: accountRequests?.checked ?? false,
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
      if (account.id) for (const action of ["credentials", "replace", "revoke"]) {
        const button = node("button", action === "credentials" ? "Issue login details" : action === "replace" ? "New link" : account.revoked ? "Revoked" : "Revoke account"); button.type = "button";
        button.disabled = action === "revoke" && account.revoked;
        button.addEventListener("click", () => run(async () => {
          const promptText = action === "revoke" ? "Revoke this account across every environment?"
            : action === "credentials" ? "Create a new password and login link? The previous password and link stop working."
            : "Replace this account's link across every environment?";
          if (!confirm(promptText)) return;
          const result = await api(`api/control/accounts/${account.id}/${action}`, { environment: env.id });
          if (result.url) issued(result, account.login, env.name, action === "credentials" ? "Login details reissued" : "Login link replaced");
          else await load();
        })); controls.append(button);
      } else if (account.role !== "admin") {
        const loginInput = node("input");
        loginInput.type = "text";
        loginInput.value = account.login;
        loginInput.maxLength = 32;
        loginInput.setAttribute("aria-label", "New username for " + account.login);
        const button = node("button", "Convert to login-link account");
        button.type = "button";
        button.addEventListener("click", () => run(async () => {
          if (!confirm("Convert this password-file account? The previous username and password stop working. A new password and login link appear once.")) return;
          const result = await api("api/control/password-accounts/convert", {
            source: account.login, login: loginInput.value.trim(), environment: env.id });
          issued(result, result.username, env.name, "Account converted"); await load();
        }));
        controls.append(loginInput, button);
      }
      row.append(controls); table.append(row);
    }
  }
}
$("create").addEventListener("submit", event => { event.preventDefault(); run(async () => {
  const form = new FormData(event.target); const slug = form.get("slug").trim();
  await api("api/control/environment?create=true", { id: slug, slug, name: form.get("name").trim(), original: false,
    goalLoops: true, video: true, promptRewrite: true, vibecodersSharing: false, nightFilter: true, members: [], defaultGenerators: ["gpt2", "googlepro"] }, true);
  event.target.reset(); await load(); $("status").textContent = "Environment requested. Refresh to check its status.";
}); });
$("person").addEventListener("submit", event => { event.preventDefault(); run(async () => {
  const form = new FormData(event.target);
  const environment = state.environments.find(env => env.id === form.get("environment"));
  if (!environment) throw Error("Select an environment for the new account.");
  const result = await api("api/control/accounts", { name: form.get("new-account-name").trim(), environment: environment.id });
  event.target.reset(); $("new-account-name").value = ""; $("person-environment").value = "";
  issued(result, result.username, environment.name, "Account created"); await load();
}); });
$("new-account-name").value = "";
$("person-environment").value = "";
$("dismiss-issued").addEventListener("click", dismissIssued);
$("copy").addEventListener("click", () => run(async () => { await navigator.clipboard.writeText($("link").value); $("status").textContent = "Login link copied."; }));
$("copy-details").addEventListener("click", () => run(async () => { await navigator.clipboard.writeText(detailsText()); $("status").textContent = "Login details copied."; }));
$("refresh").addEventListener("click", () => run(load));
run(async () => { await load(); $("status").textContent = "Only your admin account can manage environments and accounts."; });
