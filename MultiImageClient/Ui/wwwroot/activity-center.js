// Floating activity center and developer request inbox (extracted from app.js).

// ---------- floating activity center ----------

const activityPreferenceInputs = {
  activityOwnGens: el("activity-own-gens"),
  activityReturning: el("activity-returning"),
  activityMyFavorites: el("activity-my-favorites"),
  activitySharedFavorites: el("activity-shared-favorites"),
  activityRequestAlerts: el("activity-request-alerts"),
  activitySound: el("activity-sound"),
  activityBrowserPopup: el("activity-browser-popup"),
};
let activityInitialized = false;
let activityDragStart = null;
let activityResizeStart = null;
let requestsLastPollAt = 0;

function initializeActivityConfig() {
  if (!activityInitialized) {
    activityInitialized = true;
    for (const [setting, input] of Object.entries(activityPreferenceInputs)) {
      input.checked = !!uiSettings[setting];
      input.addEventListener("change", async () => {
        uiSettings[setting] = input.checked;
        saveUiSettings();
        if (setting === "activityBrowserPopup" && input.checked) {
          await requestActivityNotificationPermission();
        }
        if (setting === "activitySound" && input.checked) {
          ensureActivityAudioContext();
        }
        updateActivityNotificationPermission();
      });
    }
    restoreActivityGeometry();
    setActivityExpanded(uiSettings.activityExpanded, false);
  }
  activityRequestText.maxLength = notificationConfig.maxRequestChars || 4000;
  el("activity-request-alerts-row").hidden = !notificationConfig.isDeveloper;
  activityRequestInbox.hidden = !notificationConfig.isDeveloper;
  updateActivityNotificationPermission();
}

function activityCategoryEnabled(category) {
  if (category === "own") return uiSettings.activityOwnGens;
  if (category === "returning") return uiSettings.activityReturning;
  if (category === "my-favorite") return uiSettings.activityMyFavorites;
  if (category === "shared-favorite") return uiSettings.activitySharedFavorites;
  if (category === "request") return uiSettings.activityRequestAlerts;
  return false;
}

function updateActivityNotificationPermission() {
  const status = el("activity-notification-permission");
  status.textContent = "";
  if (!("Notification" in window)) {
    status.textContent = "Browser popup notifications are not supported here.";
    return;
  }
  if (!uiSettings.activityBrowserPopup) {
    status.textContent = "Browser popup notifications are off.";
    return;
  }
  if (Notification.permission === "granted") {
    status.textContent = "Browser popup permission granted.";
    return;
  }
  if (Notification.permission === "denied") {
    status.textContent = "Browser popup permission is blocked in browser site permissions.";
    return;
  }
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = "enable browser popup permission";
  button.addEventListener("click", requestActivityNotificationPermission);
  status.appendChild(button);
}

async function requestActivityNotificationPermission() {
  if (!("Notification" in window) || Notification.permission !== "default") {
    updateActivityNotificationPermission();
    return;
  }
  try {
    await Notification.requestPermission();
  } finally {
    updateActivityNotificationPermission();
  }
}

function ensureActivityAudioContext() {
  if (activityAudioContext) {
    if (activityAudioContext.state === "suspended") activityAudioContext.resume().catch(() => {});
    return activityAudioContext;
  }
  const AudioContextClass = window.AudioContext || window.webkitAudioContext;
  if (!AudioContextClass) return null;
  activityAudioContext = new AudioContextClass();
  return activityAudioContext;
}

function playActivitySound() {
  if (!uiSettings.activitySound) return;
  const context = ensureActivityAudioContext();
  if (!context || context.state !== "running") return;
  const oscillator = context.createOscillator();
  const gain = context.createGain();
  oscillator.type = "sine";
  oscillator.frequency.value = 660;
  gain.gain.setValueAtTime(0.0001, context.currentTime);
  gain.gain.exponentialRampToValueAtTime(0.035, context.currentTime + 0.01);
  gain.gain.exponentialRampToValueAtTime(0.0001, context.currentTime + 0.14);
  oscillator.connect(gain);
  gain.connect(context.destination);
  oscillator.start();
  oscillator.stop(context.currentTime + 0.15);
}

function showActivityBrowserNotification(message) {
  if (!uiSettings.activityBrowserPopup ||
      !("Notification" in window) ||
      Notification.permission !== "granted") return;
  try {
    const notification = new Notification("MultiImageClient", {
      body: message,
      tag: "multiimageclient-activity",
      renotify: false,
      silent: true,
    });
    setTimeout(() => notification.close(), 7000);
  } catch {
    // The in-page activity record remains the authoritative delivery.
  }
}

function rememberActivityEvent(key) {
  if (!key) return true;
  if (activityEventKeys.has(key)) return false;
  activityEventKeys.add(key);
  while (activityEventKeys.size > ActivityEventKeyCap) {
    activityEventKeys.delete(activityEventKeys.values().next().value);
  }
  return true;
}

function appendActivity(message, options = {}) {
  if (!activityCategoryEnabled(options.category)) return;
  if (!rememberActivityEvent(options.eventKey)) return;
  let row = options.dedupeKey
    ? [...activityLines.querySelectorAll(".activity-row")]
        .find((candidate) => candidate.dataset.dedupeKey === options.dedupeKey)
    : null;
  if (!row) {
    row = document.createElement("div");
    row.className = "activity-row";
    const time = document.createElement("time");
    const text = document.createElement("span");
    text.className = "activity-message";
    row.append(time, text);
    activityLines.prepend(row);
  }
  row.dataset.dedupeKey = options.dedupeKey || "";
  row.className = `activity-row${options.tone ? ` ${options.tone}` : ""}`;
  row.querySelector("time").textContent = new Date(options.at || Date.now())
    .toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  const messageElement = row.querySelector(".activity-message");
  messageElement.replaceChildren();
  if (options.decoration) {
    row.classList.add(options.decoration.className);
    const icons = document.createElement("span");
    icons.className = "activity-decoration-icons";
    icons.textContent = options.decoration.icons;
    icons.setAttribute("aria-hidden", "true");
    const copy = document.createElement("span");
    copy.textContent = message;
    const tieIn = document.createElement("span");
    tieIn.className = "activity-decoration-tie-in";
    tieIn.textContent = options.decoration.tieIn;
    messageElement.append(icons, copy, tieIn);
  } else {
    messageElement.textContent = message;
  }
  const latestMessage = options.decoration
    ? `${options.decoration.icons} ${message}`
    : message;
  activityLatest.textContent = latestMessage;
  activityLatest.title = message;
  while (activityLines.childElementCount > 120) {
    activityLines.lastElementChild.remove();
  }

  if (activityPanel.classList.contains("collapsed") || document.hidden) {
    activityUnreadCount++;
    activityUnread.hidden = false;
    activityUnread.textContent = String(Math.min(activityUnreadCount, 99));
  }
  if (options.urgent) {
    activityPanel.classList.add("has-alert");
    setTimeout(() => activityPanel.classList.remove("has-alert"), 5000);
    playActivitySound();
    showActivityBrowserNotification(latestMessage);
  }
}

function clearActivityUnread() {
  activityUnreadCount = 0;
  activityUnread.hidden = true;
  activityUnread.textContent = "0";
}

function setActivityExpanded(expanded, persist = true) {
  activityPanel.classList.toggle("collapsed", !expanded);
  activityBody.hidden = !expanded;
  activityExpand.textContent = expanded ? "▾" : "▴";
  activityExpand.setAttribute("aria-expanded", String(expanded));
  activityExpand.setAttribute("aria-label", expanded ? "Collapse activity center" : "Expand activity center");
  activityToggle.classList.toggle("open", expanded);
  activityToggle.setAttribute("aria-expanded", String(expanded));
  if (expanded) clearActivityUnread();
  if (persist) {
    uiSettings.activityExpanded = expanded;
    saveUiSettings();
  }
  clampActivityPanel();
}

function setActivityOptionsOpen(open) {
  if (open && activityPanel.classList.contains("collapsed")) setActivityExpanded(true);
  activityOptions.hidden = !open;
  activityOptionsToggle.classList.toggle("open", open);
  activityOptionsToggle.setAttribute("aria-expanded", String(open));
  if (open) updateActivityNotificationPermission();
}

activityToggle.addEventListener("click", () =>
  setActivityExpanded(activityPanel.classList.contains("collapsed")));
activityExpand.addEventListener("click", () =>
  setActivityExpanded(activityPanel.classList.contains("collapsed")));
activityOptionsToggle.addEventListener("click", () =>
  setActivityOptionsOpen(activityOptions.hidden));

function restoreActivityGeometry() {
  if (uiSettings.activityWidth) activityPanel.style.width = `${uiSettings.activityWidth}px`;
  if (uiSettings.activityHeight) activityPanel.style.height = `${uiSettings.activityHeight}px`;
  if (uiSettings.activityLeft != null && uiSettings.activityTop != null) {
    activityPanel.style.right = "auto";
    activityPanel.style.bottom = "auto";
    activityPanel.style.left = `${uiSettings.activityLeft}px`;
    activityPanel.style.top = `${uiSettings.activityTop}px`;
  }
  requestAnimationFrame(clampActivityPanel);
}

function ensureActivityUsesLeftTop() {
  const rect = activityPanel.getBoundingClientRect();
  activityPanel.style.right = "auto";
  activityPanel.style.bottom = "auto";
  activityPanel.style.left = `${rect.left}px`;
  activityPanel.style.top = `${rect.top}px`;
  return rect;
}

function clampActivityPanel() {
  if (!activityPanel.style.left || !activityPanel.style.top) return;
  const rect = activityPanel.getBoundingClientRect();
  const left = Math.max(6, Math.min(rect.left, window.innerWidth - rect.width - 6));
  const top = Math.max(6, Math.min(rect.top, window.innerHeight - rect.height - 6));
  activityPanel.style.left = `${left}px`;
  activityPanel.style.top = `${top}px`;
}

function persistActivityGeometry() {
  const rect = activityPanel.getBoundingClientRect();
  uiSettings.activityLeft = Math.round(rect.left);
  uiSettings.activityTop = Math.round(rect.top);
  if (!activityPanel.classList.contains("collapsed")) {
    uiSettings.activityWidth = Math.round(rect.width);
    uiSettings.activityHeight = Math.round(rect.height);
  }
  saveUiSettings();
}

activityDrag.addEventListener("pointerdown", (event) => {
  event.preventDefault();
  const rect = ensureActivityUsesLeftTop();
  activityDragStart = {
    pointerX: event.clientX,
    pointerY: event.clientY,
    left: rect.left,
    top: rect.top,
  };
  activityDrag.setPointerCapture(event.pointerId);
});
activityDrag.addEventListener("pointermove", (event) => {
  if (!activityDragStart) return;
  activityPanel.style.left = `${activityDragStart.left + event.clientX - activityDragStart.pointerX}px`;
  activityPanel.style.top = `${activityDragStart.top + event.clientY - activityDragStart.pointerY}px`;
  clampActivityPanel();
});
activityDrag.addEventListener("pointerup", () => {
  if (!activityDragStart) return;
  activityDragStart = null;
  persistActivityGeometry();
});
activityDrag.addEventListener("pointercancel", () => { activityDragStart = null; });

activityResize.addEventListener("pointerdown", (event) => {
  if (activityPanel.classList.contains("collapsed")) return;
  event.preventDefault();
  const rect = ensureActivityUsesLeftTop();
  activityResizeStart = {
    pointerX: event.clientX,
    pointerY: event.clientY,
    width: rect.width,
    height: rect.height,
  };
  activityResize.setPointerCapture(event.pointerId);
});
activityResize.addEventListener("pointermove", (event) => {
  if (!activityResizeStart) return;
  const left = activityPanel.getBoundingClientRect().left;
  const top = activityPanel.getBoundingClientRect().top;
  const width = Math.max(290, Math.min(
    activityResizeStart.width + event.clientX - activityResizeStart.pointerX,
    window.innerWidth - left - 6));
  const height = Math.max(220, Math.min(
    activityResizeStart.height + event.clientY - activityResizeStart.pointerY,
    window.innerHeight - top - 6));
  activityPanel.style.width = `${width}px`;
  activityPanel.style.height = `${height}px`;
});
activityResize.addEventListener("pointerup", () => {
  if (!activityResizeStart) return;
  activityResizeStart = null;
  persistActivityGeometry();
});
activityResize.addEventListener("pointercancel", () => { activityResizeStart = null; });
window.addEventListener("resize", clampActivityPanel);

function handleSharedActivity(entry) {
  const actor = entry.actor || "somebody";
  if (entry.kind === "creator-return") {
    if (entry.isActor || actor === currentUsername()) return;
    appendActivity(`${actor} started making images after some time away`, {
      category: "returning",
      tone: "social",
      urgent: true,
      at: entry.at,
      eventKey: `server:${entry.id}`,
    });
    return;
  }
  if (entry.kind === "favorite-image" || entry.kind === "favorite-prompt") {
    const target = entry.target || "somebody";
    const resource = entry.kind === "favorite-image" ? "an image" : "a prompt";
    const mine = entry.isTarget || target === currentUsername();
    appendActivity(
      mine
        ? `${actor} favorited ${resource} you made`
        : `${actor} favorited ${resource} by ${target}`,
      {
        category: mine ? "my-favorite" : "shared-favorite",
        tone: "social",
        decoration: {
          className: "favorite-feast",
          icons: "✨ 🌮 🍔 🍗 🥫 ✨",
          tieIn: "Imaginary, unofficial Darth Plagueis the Wise × Taco Bell × McDonald’s Chicken McNuggets special-sauce tie-in",
        },
        urgent: true,
        at: entry.at,
        eventKey: `server:${entry.id}`,
      });
    return;
  }
  if (entry.kind === "request-submitted" && notificationConfig.isDeveloper && !entry.isActor) {
    appendActivity(`${actor} submitted a developer request`, {
      category: "request",
      tone: "social",
      urgent: true,
      at: entry.at,
      eventKey: `server:${entry.id}`,
    });
  }
}

async function pollActivity() {
  if (activityPollInFlight) return;
  if (activityPollTimer) {
    clearTimeout(activityPollTimer);
    activityPollTimer = null;
  }
  if (!tabActivity.shouldPoll()) return;
  activityPollInFlight = true;
  try {
    const query = activityCursor == null ? "" : `?after=${activityCursor}`;
    const response = await fetch(apiUrl(`api/activity/poll${query}`));
    if (response.status === 401) { location.reload(); return; }
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const body = await response.json();
    if (body.reset) activityEventKeys.clear();
    for (const entry of body.entries || []) handleSharedActivity(entry);
    activityCursor = body.cursor;
    if (notificationConfig.isDeveloper &&
        Date.now() - requestsLastPollAt >= (document.hidden ? 30000 : 5000)) {
      pollDeveloperRequests();
    }
  } catch (error) {
    activityLatest.textContent = `activity disconnected — ${error}`;
  } finally {
    activityPollInFlight = false;
    activityPollTimer = setTimeout(pollActivity, document.hidden ? 10000 : 2500);
  }
}

function recordOwnJobActivity(jobId, card, event) {
  if (!jobActivityHydrated || !ownedJobIds.has(jobId) || !uiSettings.activityOwnGens) return;
  const at = event.at || Date.now();
  const generator = event.gen ? genLabel(event.gen) : "";
  if (event.type === "accepted" || event.type === "job-queued") {
    appendActivity("your generation job is queued", {
      category: "own",
      at,
      eventKey: `job:${jobId}:${event.type}:${at}`,
      dedupeKey: `job:${jobId}`,
    });
  } else if (event.type === "job-start") {
    appendActivity("your generation job started", {
      category: "own",
      at,
      eventKey: `job:${jobId}:start:${at}`,
      dedupeKey: `job:${jobId}`,
    });
  } else if (event.type === "gen-start") {
    appendActivity(`${generator} started`, {
      category: "own",
      at,
      eventKey: `job:${jobId}:${event.gen}:start:${at}`,
      dedupeKey: `job:${jobId}:${event.gen}`,
    });
  } else if (event.type === "gen-result") {
    appendActivity(event.ok ? `${generator} finished` : `${generator} failed`, {
      category: "own",
      tone: event.ok ? "success" : "error",
      urgent: !event.ok,
      at,
      eventKey: `job:${jobId}:${event.gen}:result:${at}`,
      dedupeKey: `job:${jobId}:${event.gen}`,
    });
  } else if (event.type === "job-done") {
    appendActivity("your generation job finished", {
      category: "own",
      tone: "success",
      urgent: true,
      at,
      eventKey: `job:${jobId}:done:${at}`,
      dedupeKey: `job:${jobId}`,
    });
  }
}

activityRequestForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  const body = activityRequestText.value.trim();
  const user = currentUsername();
  activityRequestStatus.className = "";
  if (!user) {
    activityRequestStatus.textContent = "choose a creating-as name first";
    activityRequestStatus.className = "error";
    return;
  }
  if (!body) {
    activityRequestStatus.textContent = "write the request first";
    activityRequestStatus.className = "error";
    return;
  }
  activityRequestSubmit.disabled = true;
  activityRequestStatus.textContent = "storing…";
  const form = new FormData();
  form.append("user", user);
  form.append("body", body);
  try {
    const response = await fetch(apiUrl("api/requests"), { method: "POST", body: form });
    if (response.status === 401) { location.reload(); return; }
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || `HTTP ${response.status}`);
    activityRequestText.value = "";
    activityRequestStatus.textContent = "request stored";
    appendActivity("your developer request was stored", {
      category: "own",
      tone: "success",
      at: result.submittedAtUnixMs,
      eventKey: `request:${result.id}`,
    });
  } catch (error) {
    activityRequestStatus.textContent = String(error);
    activityRequestStatus.className = "error";
  } finally {
    activityRequestSubmit.disabled = false;
  }
});

function appendDeveloperRequest(request) {
  if (activityRequestList.querySelector(`[data-request-id="${CSS.escape(request.id)}"]`)) return;
  const item = document.createElement("article");
  item.className = "activity-request";
  item.dataset.requestId = request.id;
  const meta = document.createElement("div");
  meta.className = "activity-request-meta";
  const who = document.createElement("span");
  who.textContent = request.submitterLogin && request.submitterLogin !== request.submitter
    ? `${request.submitter} (${request.submitterLogin})`
    : request.submitter;
  const when = document.createElement("time");
  when.textContent = new Date(request.submittedAtUnixMs).toLocaleString();
  meta.append(who, when);
  const body = document.createElement("div");
  body.className = "activity-request-body";
  body.textContent = request.body;
  const copy = document.createElement("button");
  copy.type = "button";
  copy.className = "activity-request-copy";
  copy.textContent = "copy";
  copy.addEventListener("click", async () => {
    const fullText = `From: ${who.textContent}\nSubmitted: ${when.textContent}\n\n${request.body}`;
    await navigator.clipboard.writeText(fullText);
    copy.textContent = "copied";
    setTimeout(() => { copy.textContent = "copy"; }, 1200);
  });
  item.append(meta, body, copy);
  activityRequestList.prepend(item);
  activityRequestCount.textContent = `${activityRequestList.childElementCount} loaded`;
}

async function pollDeveloperRequests() {
  if (!notificationConfig.isDeveloper || requestsPollInFlight) return;
  requestsPollInFlight = true;
  requestsLastPollAt = Date.now();
  try {
    const response = await fetch(apiUrl(`api/requests?after=${requestsCursor}`));
    if (response.status === 401) { location.reload(); return; }
    if (response.status === 403) return;
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const body = await response.json();
    if (body.reset) {
      activityRequestList.textContent = "";
      requestsHydrated = false;
    }
    for (const request of body.requests || []) appendDeveloperRequest(request);
    requestsCursor = body.cursor;
    requestsHydrated = true;
  } catch (error) {
    activityRequestCount.textContent = `inbox unavailable: ${error}`;
  } finally {
    requestsPollInFlight = false;
  }
}

