"use strict";
const element = id => document.getElementById(id);
const status = element("status");
async function api(path, data) {
  const response = await fetch(path, data === undefined ? { cache: "no-store" } : {
    method: "POST", headers: { "X-Mic-Manage": "1" }, body: new URLSearchParams(data), cache: "no-store",
  });
  if (!response.ok) {
    let message = response.status === 403 ? "Only the environment owner can manage people." : "The request failed.";
    try { message = (await response.json()).error || message; } catch {}
    throw new Error(message);
  }
  return response.json();
}
function showLink(url) { element("link").value = url; element("issued").hidden = false; }
async function load() {
  const people = await api("api/people");
  const config = await api("api/config");
  element("management").hidden = false;
  element("accounts").replaceChildren();
  for (const account of people.accounts) {
    const row = document.createElement("div"); row.className = "person";
    const name = document.createElement("span"); name.textContent = account.displayName + (account.owner ? " · owner" : "") + (account.revoked ? " · revoked" : "");
    row.append(name);
    for (const [label, action] of [["Replace login link", "replace"], ["Revoke link", "revoke"]]) {
      const button = document.createElement("button"); button.type = "button"; button.textContent = label;
      button.disabled = action === "revoke" && account.revoked;
      button.addEventListener("click", () => run(async () => {
        if (!confirm(action === "replace" ? "Replace this link and end its existing sessions?" : "Revoke this link and end its existing sessions?")) return;
        const result = await api(`api/people/${account.id}/${action}`, {});
        if (result.url) showLink(result.url);
        if (account.owner) {
          for (const control of element("management").querySelectorAll("button, input")) control.disabled = true;
          element("copy").disabled = false;
          element("link").disabled = false;
          status.textContent = result.url
            ? "Copy your new link, then open it to continue."
            : "Owner link revoked. Use your recovery password to log in again.";
          return;
        }
        await load(); status.textContent = action === "replace" ? "New login link created." : "Login link revoked.";
      }));
      row.append(button);
    }
    element("accounts").append(row);
  }
  const defaults = new Set(people.defaults ?? config.generators.filter(g => g.defaultOn).map(g => g.key));
  element("provider-defaults").replaceChildren();
  for (const provider of config.generators.filter(g => g.available)) {
    const label = document.createElement("label"); const input = document.createElement("input");
    input.type = "checkbox"; input.value = provider.key; input.checked = defaults.has(provider.key);
    label.append(input, document.createTextNode(" " + provider.label)); element("provider-defaults").append(label);
  }
}
async function run(action) {
  try { await action(); } catch (error) { status.textContent = error.message; }
}
element("add-person").addEventListener("submit", event => { event.preventDefault(); run(async () => {
  const button = event.submitter; button.disabled = true;
  try { const result = await api("api/people", { name: element("name").value }); showLink(result.url); element("name").value = "";
    await load(); status.textContent = "Account created. Copy its login link."; }
  finally { button.disabled = false; }
}); });
element("defaults").addEventListener("submit", event => { event.preventDefault(); run(async () => {
  const keys = [...element("provider-defaults").querySelectorAll("input:checked")].map(input => input.value);
  await api("api/environment/defaults", { keys: keys.join(",") }); status.textContent = "Environment defaults saved.";
}); });
element("copy").addEventListener("click", () => run(async () => {
  await navigator.clipboard.writeText(element("link").value); status.textContent = "Login link copied.";
}));
run(async () => { await load(); status.textContent = "Manage access for this environment."; });
