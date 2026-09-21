"use strict";
(() => {
  const status = $("discord-request-status"), list = $("discord-requests"), refresh = $("refresh-discord-requests");
  const labels = {
    review: "Awaiting test", "test-pending": "Test delivery unconfirmed; user delivery blocked",
    tested: "Test sent to Brouhahaha", "test-failed": "Test failed", pending: "User delivery unconfirmed",
    sent: "Account link sent", failed: "Account link failed", rejected: "Request rejected",
    activating: "Account creation in progress", granting: "Account access in progress",
  };
  let busy = false;
  async function loadRequests() {
    const result = await api("api/control/discord-account-requests");
    const rows = [];
    for (const request of result.requests) {
      const row = node("div"); row.style.margin = "16px 0";
      row.append(node("strong", "@" + request.username), node("p", (labels[request.state] || request.state)
        + " · Requested " + date(request.createdAt) + " · Expires " + date(request.expiresAt)));
      for (const [action, text, enabled] of [["test", "Test", request.canTest],
        ["send", "Confirmed, send to user", request.canSend], ["reject", "Reject request", request.canReject]]) {
        const button = node("button", text); button.type = "button"; button.disabled = !enabled;
        button.addEventListener("click", () => perform(async () => {
          const reply = await api("api/control/discord-account-requests/" + encodeURIComponent(request.id) + "/" + action, {});
          await loadRequests(); status.textContent = reply.message;
        }));
        row.append(button);
      }
      rows.push(row);
    }
    list.replaceChildren(...rows);
    status.textContent = !result.enabled ? "Discord account requests are paused."
      : rows.length ? "Review each recipient before sending." : "No account requests need review.";
  }
  async function perform(action) {
    if (busy) return;
    busy = true; refresh.disabled = true;
    const buttons = [...list.querySelectorAll("button")];
    buttons.forEach(button => { button.disabled = true; });
    try { await action(); }
    catch (error) {
      // A lost response can follow a completed send. Read durable state before enabling any action again.
      try { await loadRequests(); } catch { /* Keep the old actions disabled until a successful refresh. */ }
      status.textContent = error.message;
    } finally { busy = false; refresh.disabled = false; }
  }
  refresh.addEventListener("click", () => perform(loadRequests));
  perform(loadRequests);
})();
