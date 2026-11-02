"use strict";

const $ = selector => document.querySelector(selector);

const state = {
  user: localStorage.getItem("chat-user") || null,
  sessions: [],
  selectedSession: null,
  sessionQuery: "",
  sessionStatus: "",
  messages: [],
  timelineQuery: "",
  timelineRole: ""
};

let searchDebounce = null;

document.addEventListener("DOMContentLoaded", async () => {
  $("#userSelect").addEventListener("change", async () => {
    state.user = $("#userSelect").value;
    localStorage.setItem("chat-user", state.user);
    state.selectedSession = null;
    state.messages = [];
    $("#timeline").innerHTML = "";
    $("#timelineHead").innerHTML = `<p class="sidebar-hint">Избери разговор от списъка вляво.</p>`;
    $("#timelineToolbar").classList.add("is-hidden");
    await Promise.all([loadSessions(), loadStats()]);
  });
  $("#closeArchiveDialog").addEventListener("click", () => $("#archiveDialog").close());

  // Търсенето по сесии е сървърно (рови в цялото съдържание) — с debounce,
  // за да не бием базата на всеки клавиш.
  $("#sessionSearch").addEventListener("input", () => {
    clearTimeout(searchDebounce);
    searchDebounce = setTimeout(() => {
      state.sessionQuery = $("#sessionSearch").value.trim();
      loadSessions();
    }, 300);
  });

  // Търсенето в отворения разговор е мигновено (данните са заредени).
  $("#timelineSearch").addEventListener("input", () => {
    state.timelineQuery = $("#timelineSearch").value.trim();
    renderTimeline();
  });

  document.addEventListener("click", event => {
    const statusChip = event.target.closest("#statusChips [data-status]");
    if (statusChip) {
      state.sessionStatus = statusChip.dataset.status;
      document.querySelectorAll("#statusChips .filter-chip").forEach(chip =>
        chip.classList.toggle("is-on", chip === statusChip));
      loadSessions();
      return;
    }
    const roleChip = event.target.closest("#timelineToolbar [data-role]");
    if (roleChip) {
      state.timelineRole = roleChip.dataset.role;
      document.querySelectorAll("#timelineToolbar .filter-chip").forEach(chip =>
        chip.classList.toggle("is-on", chip === roleChip));
      renderTimeline();
      return;
    }
    const card = event.target.closest("[data-session-id]");
    if (card) selectSession(card.dataset.sessionId);
    const chunkButton = event.target.closest("[data-chunk-id]");
    if (chunkButton) toggleChunk(chunkButton);
    const archiveButton = event.target.closest("[data-archive-id]");
    if (archiveButton) openArchive(archiveButton.dataset.archiveId);
  });

  if (!await loadUsers()) return;
  await Promise.all([loadSessions(), loadStats()]);
});

async function loadStats() {
  try {
    const stats = await api(`/api/chat/history/stats?user=${encodeURIComponent(state.user)}`);
    // Само числото се сменя — обяснителният текст стои в HTML-а.
    $("#statMessages b").textContent = formatNumber(stats.messages);
    $("#statSessions b").textContent = formatNumber(stats.sessions);
    $("#statDocuments b").textContent = formatNumber(stats.documents);
    $("#statChunks b").textContent = formatNumber(stats.chunks);
    const userName = $("#userSelect").selectedOptions[0]?.textContent || state.user;
    $("#flowTotal").textContent =
      `${userName}: ${formatNumber(stats.messages)} реплики в ${formatNumber(stats.sessions)} разговора · изхарчени ${formatNumber(stats.totalTokens)} токена`;
  } catch { /* диаграмата остава без бройки — не е критично */ }
}

async function loadUsers() {
  let profilesLoaded = false;
  try {
    const response = await api("/api/chat/profiles");
    const users = Array.isArray(response.users) ? response.users : [];
    if (users.length === 0) throw new Error("Няма конфигурирани потребителски профили.");

    const storedUser = String(state.user || "").toLowerCase();
    const defaultUser = String(response.defaultUserId || "").toLowerCase();
    const selected = users.find(user => String(user.id).toLowerCase() === storedUser)
      || users.find(user => String(user.id).toLowerCase() === defaultUser)
      || users[0];
    state.user = selected.id;
    localStorage.setItem("chat-user", state.user);
    $("#userSelect").innerHTML = users
      .map(user => `<option value="${escapeHtml(user.id)}"${user.id === state.user ? " selected" : ""}>${escapeHtml(user.name)}</option>`)
      .join("");
    profilesLoaded = true;
  } catch (error) {
    notify("error", "Потребителите не се заредиха", error.message);
  }
  try {
    const agents = await api("/api/chat/agents");
    state.agentNames = Object.fromEntries(agents.map(agent => [agent.key, agent.displayName]));
  } catch { state.agentNames = {}; }
  return profilesLoaded;
}

// „coach" → „Треньор"; ключът остава в title за дебъг.
function agentName(key) {
  return (state.agentNames && state.agentNames[key]) || key;
}

async function loadSessions() {
  try {
    const params = new URLSearchParams({ user: state.user });
    if (state.sessionQuery) params.set("q", state.sessionQuery);
    if (state.sessionStatus) params.set("status", state.sessionStatus);
    state.sessions = await api(`/api/chat/history/sessions?${params}`);
    $("#sessionsCount").textContent = state.sessions.length;
    renderSessions();
  } catch (error) {
    notify("error", "Разговорите не се заредиха", error.message);
  }
}

function sessionStatus(session) {
  if (!session.compactedAt) return { css: "active", label: "АКТИВЕН" };
  if (session.archiveDocumentId) return { css: "compacted", label: "КОМПАКТНАТ" };
  return { css: "closed", label: "ЗАТВОРЕН" };
}

function renderSessions() {
  if (!state.sessions.length) {
    $("#sessionsList").innerHTML = state.sessionQuery || state.sessionStatus
      ? `<p class="sidebar-hint">Нищо не отговаря на филтрите. Пробвай друга дума или „Всички“.</p>`
      : `<p class="sidebar-hint">Този потребител още няма записани разговори.</p>`;
    return;
  }
  $("#sessionsList").innerHTML = state.sessions.map(session => {
    const status = sessionStatus(session);
    return `
    <button class="session-card${session.id === state.selectedSession ? " is-selected" : ""}" type="button" data-session-id="${session.id}">
      <span class="session-card-top">
        <time>${formatDateTime(session.startedAt)}</time>
        <span class="session-status ${status.css}">${status.label}</span>
      </span>
      <span class="session-preview">${session.messageCount === 0 && !session.compactedAt
        ? "Нов разговор — очаква първото съобщение (продължава върху резюме от предишния)."
        : escapeHtml(session.preview || "(празен разговор)")}</span>
      <span class="session-card-meta">
        ${session.messageCount === 0 && !session.compactedAt
          ? `<span>още няма реплики</span>`
          : `<span>${session.messageCount} реплики</span>
        <span>${formatNumber(session.totalTokens)} токена</span>`}
      </span>
    </button>`;
  }).join("");
}

async function selectSession(sessionId) {
  state.selectedSession = sessionId;
  renderSessions();
  const session = state.sessions.find(item => item.id === sessionId);
  const status = sessionStatus(session);

  try {
    state.messages = await api(`/api/chat/history/sessions/${sessionId}?user=${encodeURIComponent(state.user)}`);

    $("#timelineHead").innerHTML = `
      <h3>Разговор от ${formatDateTime(session.startedAt)}</h3>
      <span class="session-status ${status.css}">${status.label}</span>
      <span class="head-meta">${state.messages.length} реплики · ${formatNumber(session.totalTokens)} токена${session.compactedAt ? ` · затворен ${formatDateTime(session.compactedAt)}` : ""}</span>
      ${session.archiveDocumentId ? `<button class="secondary-button" type="button" data-archive-id="${escapeHtml(session.archiveDocumentId)}">Виж резюмето</button>` : ""}`;

    $("#timelineToolbar").classList.remove("is-hidden");
    $("#timelineSearch").value = "";
    state.timelineQuery = "";
    renderTimeline();
    $("#timeline").scrollTop = 0;
  } catch (error) {
    notify("error", "Разговорът не се зареди", error.message);
  }
}

// Филтрите на разговора са клиентски: репликите са вече заредени, така че
// търсене + роля + осветяване стават мигновено, без нови заявки.
function renderTimeline() {
  const query = state.timelineQuery.toLowerCase();
  const filtered = state.messages.filter(message =>
    (!state.timelineRole || message.role === state.timelineRole)
    && (!query || message.content.toLowerCase().includes(query)));

  $("#matchCount").textContent = (state.timelineQuery || state.timelineRole)
    ? `${filtered.length} от ${state.messages.length} реплики`
    : "";
  $("#timeline").innerHTML = filtered.map(renderMessage).join("")
    || `<p class="sidebar-hint">Нищо не отговаря на филтъра в този разговор.</p>`;
}

function renderMessage(message) {
  const time = `<div class="time-tick">${formatDateTime(message.createdAt, true)}</div>`;

  if (message.role === "user") {
    return `
    <div class="turn">
      <div class="turn-user">
        <span class="turn-avatar">Т</span>
        <div class="turn-body">
          <div class="turn-content">${highlight(message.content)}</div>
          ${time}
        </div>
      </div>
    </div>`;
  }

  // Отговор на асистента: произходът като чипове + разгъваем контекст.
  const chips = [
    message.agentKey ? `<span class="prov-chip agent" title="${escapeHtml(message.agentKey)}"><svg class="icon"><use href="#icon-spark"></use></svg>${escapeHtml(agentName(message.agentKey))}</span>` : "",
    message.model ? `<span class="prov-chip">${escapeHtml(message.model)}</span>` : "",
    message.promptVersion != null ? `<span class="prov-chip prompt">промпт v${message.promptVersion}</span>` : "",
    message.inputTokens != null ? `<span class="prov-chip"><svg class="icon"><use href="#icon-token"></use></svg>${formatNumber(message.inputTokens)} вх · ${formatNumber(message.outputTokens || 0)} изх</span>` : ""
  ].filter(Boolean).join("");

  // Одитната следа: кои инструменти е викал агентът в този ход.
  let stepsHtml = "";
  const steps = parseRefs(message.stepsJson);
  if (steps.length) {
    stepsHtml = `<div class="turn-steps">${steps.map(step => `<span>${highlight(String(step))}</span>`).join("")}</div>`;
  }

  let contextHtml = "";
  const refs = parseRefs(message.contextRefsJson);
  if (refs.length) {
    contextHtml = `
    <details class="context-used">
      <summary><svg class="icon"><use href="#icon-search"></use></svg> Контекст от паметта · ${refs.length} откъса <svg class="icon"><use href="#icon-chevron"></use></svg></summary>
      ${refs.map(ref => `
        <div class="context-ref">
          <div class="context-ref-head">
            <strong>${escapeHtml(ref.documentId || ref.chunkId)}</strong>
            <span>score ${Number(ref.score).toFixed(5)}</span>
            <span>${escapeHtml(ref.strategy || "")}</span>
            <button class="secondary-button" type="button" data-chunk-id="${escapeHtml(ref.chunkId)}">Покажи текста</button>
          </div>
          <pre data-chunk-content="${escapeHtml(ref.chunkId)}"></pre>
        </div>`).join("")}
    </details>`;
  } else if (message.contextRefsJson === null && message.agentKey) {
    contextHtml = `<div class="context-used"><summary style="cursor:default"><svg class="icon"><use href="#icon-search"></use></svg> Без контекст от паметта — отговорът е само от разговора</summary></div>`;
  }

  return `
  <div class="turn">
    <div class="turn-assistant">
      <span class="turn-avatar">A</span>
      <div class="turn-body">
        <div class="turn-content">${highlight(message.content)}</div>
        ${stepsHtml}
        <div class="provenance">${chips}</div>
        ${contextHtml}
        ${time}
      </div>
    </div>
  </div>`;
}

function parseRefs(json) {
  if (!json) return [];
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

// Лениво зареждане на текста на chunk-а — чак когато човек го поиска.
async function toggleChunk(button) {
  const chunkId = button.dataset.chunkId;
  const container = button.closest(".context-ref");
  const pre = container.querySelector(`pre[data-chunk-content]`);
  if (container.classList.contains("is-open")) {
    container.classList.remove("is-open");
    button.textContent = "Покажи текста";
    return;
  }
  if (!pre.textContent) {
    try {
      pre.textContent = await api(`/api/chat/history/chunks/${encodeURIComponent(chunkId)}?user=${encodeURIComponent(state.user)}`);
    } catch (error) {
      pre.textContent = `(откъсът не се зареди: ${error.message})`;
    }
  }
  container.classList.add("is-open");
  button.textContent = "Скрий";
}

async function openArchive(archiveId) {
  try {
    const content = await api(`/api/chat/archives/${encodeURIComponent(archiveId)}?user=${encodeURIComponent(state.user)}`);
    $("#archiveDialogTitle").textContent = "Резюмето, в което е компактнат разговорът";
    $("#archiveDialogContent").textContent = content;
    $("#archiveDialog").showModal();
  } catch (error) {
    notify("error", "Резюмето не се отвори", error.message);
  }
}

// --- Помощни -----------------------------------------------------------------

async function api(path) {
  const response = await fetch(path, { headers: { Accept: "application/json" } });
  if (!response.ok) {
    let problem;
    try { problem = await response.json(); } catch { problem = null; }
    throw new Error(problem?.detail || problem?.title || `HTTP ${response.status}`);
  }
  const contentType = response.headers.get("content-type") || "";
  return contentType.includes("json") ? response.json() : response.text();
}

// Escape + осветяване на търсения текст с <mark> — върху вече escape-натия
// низ, за да не отвори XSS врата.
function highlight(text) {
  const safe = escapeHtml(text);
  if (!state.timelineQuery) return safe;
  const safeQuery = escapeHtml(state.timelineQuery).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return safe.replace(new RegExp(safeQuery, "gi"), match => `<mark>${match}</mark>`);
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;").replaceAll("'", "&#39;");
}

function formatDateTime(value, withSeconds = false) {
  const date = new Date(value);
  const base = `${String(date.getDate()).padStart(2, "0")}.${String(date.getMonth() + 1).padStart(2, "0")} ${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
  return withSeconds ? `${base}:${String(date.getSeconds()).padStart(2, "0")}` : base;
}

function formatNumber(value) {
  return Number(value || 0).toLocaleString("bg-BG");
}

function notify(kind, title, message) {
  const toast = document.createElement("div");
  toast.className = `toast ${kind}`;
  toast.innerHTML = `<strong>${escapeHtml(title)}</strong><p>${escapeHtml(message)}</p>`;
  $("#toastStack").appendChild(toast);
  setTimeout(() => toast.remove(), 6000);
}
