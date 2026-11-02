"use strict";

const $ = selector => document.querySelector(selector);

const state = {
  sending: false,
  settings: null,
  user: localStorage.getItem("chat-user") || null
};

function currentUser() {
  return state.user;
}

document.addEventListener("DOMContentLoaded", async () => {
  bindEvents();
  if (!await loadProfiles()) return;
  await loadState();
});

function bindEvents() {
  $("#chatForm").addEventListener("submit", event => {
    event.preventDefault();
    sendMessage();
  });
  $("#chatInput").addEventListener("keydown", event => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      sendMessage();
    }
  });
  $("#compactButton").addEventListener("click", compactNow);
  $("#newChatButton").addEventListener("click", startNewChat);
  $("#refreshArchives").addEventListener("click", loadArchives);
  $("#modelSelect").addEventListener("change", () => saveSettings({ model: $("#modelSelect").value }));
  const streamToggle = $("#streamToggle");
  streamToggle.checked = localStorage.getItem("chat-streaming") !== "off";
  streamToggle.addEventListener("change", () =>
    localStorage.setItem("chat-streaming", streamToggle.checked ? "on" : "off"));
  $("#userSelect").addEventListener("change", async () => {
    if (state.sending) {
      $("#userSelect").value = state.user;
      notify("error", "Изчакай отговора", "Смени профила след края на текущото съобщение.");
      return;
    }
    state.user = $("#userSelect").value;
    localStorage.setItem("chat-user", state.user);
    clearMessages();
    await loadState();
  });
  $("#saveBudget").addEventListener("click", () => {
    const budget = Number($("#budgetInput").value);
    if (!Number.isFinite(budget) || budget < 1000 || budget > 500000) {
      notify("error", "Невалиден бюджет", "Въведи число между 1 000 и 500 000 токена.");
      return;
    }
    saveSettings({ contextTokenBudget: budget });
  });
  $("#closeArchiveDialog").addEventListener("click", () => $("#archiveDialog").close());
  $("#handbackButton").addEventListener("click", handback);
  document.addEventListener("click", event => {
    const item = event.target.closest("[data-archive-id]");
    if (item) openArchive(item.dataset.archiveId, item.dataset.archiveTitle);
  });
}

async function loadState() {
  try {
    const stateResponse = await api(`/api/chat/state?user=${encodeURIComponent(state.user)}`);
    state.settings = stateResponse.settings;
    state.runtime = stateResponse.runtime;
    state.user = stateResponse.userId;
    localStorage.setItem("chat-user", state.user);
    $("#userSelect").innerHTML = stateResponse.settings.users
      .map(user => `<option value="${escapeHtml(user.id)}"${user.id === state.user ? " selected" : ""}>${escapeHtml(user.name)}</option>`)
      .join("");
    $("#strategySelect").value = stateResponse.settings.defaultStrategy;
    $("#topKSelect").value = String(stateResponse.settings.topK);
    $("#budgetInput").value = stateResponse.runtime.contextTokenBudget;
    await loadAgentNames();
    for (const message of stateResponse.messages) {
      // Възстановените отговори носят произхода си от суровия слой — мета
      // редът и стъпките оцеляват refresh-а (контекстът остава в Историята).
      const restored = message.model ? {
        context: null,
        steps: message.steps || [],
        agentKey: message.agentKey,
        model: message.model,
        promptVersion: message.promptVersion,
        usage: { inputTokens: message.inputTokens ?? 0, outputTokens: message.outputTokens ?? 0 }
      } : null;
      appendMessage(message.role, message.text, restored);
    }
    renderMicBanner(stateResponse.activeAgent, stateResponse.activeAgentName);
    $("#seedNote").classList.toggle("is-hidden", !stateResponse.summarySeed);
    renderMeter(stateResponse.meter);
    renderArchives(stateResponse.archives);
    setGenerationSource(stateResponse.generationSource);
    await loadModels(stateResponse.runtime.model);
  } catch (error) {
    notify("error", "Състоянието не се зареди", error.message);
  }
}

async function loadProfiles() {
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
  } catch (error) {
    notify("error", "Профилите не се заредиха", error.message);
    return false;
  }
  return true;
}

async function loadModels(currentModel) {
  try {
    const response = await api("/api/chat/models");
    const models = response.models || [];
    if (currentModel && !models.includes(currentModel)) models.unshift(currentModel);
    $("#modelSelect").innerHTML = models
      .map(model => `<option value="${escapeHtml(model)}"${model === currentModel ? " selected" : ""}>${escapeHtml(model)}</option>`)
      .join("");
  } catch (error) {
    $("#modelSelect").innerHTML = `<option selected>${escapeHtml(currentModel || "?")}</option>`;
  }
}

async function saveSettings(changes) {
  try {
    const runtime = await api("/api/chat/settings", { method: "POST", body: changes });
    state.runtime = runtime;
    $("#budgetInput").value = runtime.contextTokenBudget;
    setGenerationSource(null, runtime.model);
    notify("success", "Настройките са записани",
      `Модел: ${runtime.model} · бюджет: ${formatNumber(runtime.contextTokenBudget)} токена.`);
  } catch (error) {
    notify("error", "Настройката не се записа", error.message);
  }
}

function setGenerationSource(fullSource, modelOnly) {
  const brandSmall = document.querySelector(".brand small");
  if (!brandSmall) return;
  if (fullSource) {
    brandSmall.textContent = fullSource;
  } else if (modelOnly) {
    const current = brandSmall.textContent;
    const provider = current.includes("(") ? current.slice(0, current.indexOf("(")).trim() : current;
    brandSmall.textContent = `${provider} (${modelOnly})`;
  }
}

async function sendMessage() {
  const input = $("#chatInput");
  const text = input.value.trim();
  if (!text || state.sending) return;

  state.sending = true;
  $("#sendButton").disabled = true;
  input.value = "";
  appendMessage("user", text, null);
  const body = {
    userId: currentUser(),
    text,
    strategy: $("#strategySelect").value,
    topK: Number($("#topKSelect").value)
  };
  const typing = appendTyping();

  try {
    const turn = $("#streamToggle").checked
      ? await sendStreaming(body, typing)
      : await sendBuffered(body, typing);
    if (turn) finishTurn(turn);
  } catch (error) {
    typing.remove();
    appendMessage("assistant", `⚠ Грешка: ${error.message}`, null);
  } finally {
    state.sending = false;
    $("#sendButton").disabled = false;
    input.focus();
  }
}

// Класическият режим: една заявка, отговорът пристига наведнъж.
async function sendBuffered(body, typing) {
  const turn = await api("/api/chat/message", { method: "POST", body });
  typing.remove();
  return turn;
}

// Поточният режим: NDJSON редове — context, delta..., done.
async function sendStreaming(body, typing) {
  const response = await fetch("/api/chat/message/stream", {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Search-Lab": "1", Accept: "application/x-ndjson" },
    body: JSON.stringify(body)
  });
  if (!response.ok || !response.body) {
    let problem;
    try { problem = await response.json(); } catch { problem = null; }
    throw new Error(problem?.detail || problem?.title || `HTTP ${response.status}`);
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let accumulated = "";
  let bubble = null;
  let turn = null;

  const handleEvent = event => {
    if (event.type === "delta") {
      if (!bubble) {
        typing.remove();
        bubble = appendStreamingBubble();
      }
      accumulated += event.text;
      bubble.querySelector(".bubble").innerHTML =
        `${formatText(accumulated)}<span class="stream-caret"></span>`;
      bubble.scrollIntoView({ block: "end" });
    } else if (event.type === "done") {
      turn = event.turn;
    } else if (event.type === "error") {
      throw new Error(event.message);
    }
    // event.type === "context" пристига преди генерирането; пълните детайли
    // така или иначе идват с "done", затова тук не рисуваме нищо отделно.
  };

  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let newline;
      while ((newline = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, newline).trim();
        buffer = buffer.slice(newline + 1);
        if (line) handleEvent(JSON.parse(line));
      }
    }
  } finally {
    typing.remove();
    bubble?.remove();
  }

  if (!turn) throw new Error("Потокът прекъсна, преди отговорът да завърши.");
  return turn;
}

// Системна бележка: кратките са просто хапче; дългите (предаването носи
// цялата задача към специалиста) показват само заглавната част, а пълният
// текст се разгъва при клик — иначе хапчето се пръска от съдържанието.
function renderSystemNote(text) {
  if (text.length <= 140) {
    const note = document.createElement("div");
    note.className = "chat-note";
    note.textContent = text;
    return note;
  }

  const colon = text.indexOf(":");
  const headline = colon > 0 && colon < 140
    ? text.slice(0, colon).trimEnd() + " —"
    : text.slice(0, 140).trimEnd() + " … —";

  const details = document.createElement("details");
  details.className = "chat-note expandable";
  const summary = document.createElement("summary");
  summary.textContent = headline + "  ⌄";
  const full = document.createElement("div");
  full.className = "note-full";
  full.textContent = text;
  details.append(summary, full);
  return details;
}

function finishTurn(turn) {
  for (const note of turn.notes || []) {
    appendMessage("system", note, null);
  }
  appendMessage("assistant", turn.answer.text, turn);
  renderMicBanner(turn.activeAgent, turn.activeAgentName);
  renderMeter(turn.meter);
  if (turn.compactedTo) {
    $("#seedNote").classList.remove("is-hidden");
    notify("success", "Чатът беше компактнат",
      `Резюмето е записано като ${turn.compactedTo.fileName} и е индексирано в архива.`);
    loadArchives();
  }
}

function renderDiaryConfirmations(confirmations) {
  if (!confirmations?.length) return "";

  const labels = {
    recorded: ["✓", "Записано"],
    corrected: ["✓", "Коригирано"],
    voided: ["×", "Анулирано"],
    "not-recorded": ["↔", "Не е записано"],
    rejected: ["!", "Нищо не е записано"]
  };

  const items = confirmations.map(item => {
    const [icon, action] = labels[item.status] || labels.rejected;
    // Без предварително escape-ване: detailRows escape-ва всяка стойност
    // точно веднъж — иначе "&" се показва като "&amp;".
    const metadata = [item.programDay, item.kind, item.id ? `id ${item.id}` : null]
      .filter(Boolean)
      .join(" · ");
    let data = "";
    if (item.dataJson) {
      try {
        data = Object.entries(JSON.parse(item.dataJson))
          .slice(0, 8)
          .map(([key, rawValue]) => {
            const value = rawValue !== null && typeof rawValue === "object"
              ? JSON.stringify(rawValue)
              : rawValue;
            switch (key.toLowerCase()) {
              case "серии": return `${value} серии`;
              case "повторения": return `${value} повторения`;
              case "тегло_кг": return `${value} кг`;
              default: return `${key.replaceAll("_", " ")} ${value}`;
            }
          })
          .join(" · ");
      } catch {
        data = "";
      }
    }
    const detailRows = [metadata, data, item.note, item.message]
      .filter(Boolean)
      .map(value => `<small>${escapeHtml(String(value))}</small>`)
      .join("");

    return `
      <div class="diary-confirmation diary-${escapeHtml(item.status || "rejected")}">
        <span class="diary-icon">${icon}</span>
        <div><strong>${action}: ${escapeHtml(item.label || "Дневникова операция")}</strong>${detailRows}</div>
      </div>`;
  }).join("");

  return `<section class="diary-confirmations"><h4>Дневник</h4>${items}</section>`;
}

// --- Микрофонът: кой държи разговора --------------------------------------

function renderMicBanner(activeAgent, activeAgentName) {
  const banner = $("#micBanner");
  banner.classList.toggle("is-hidden", !activeAgent);
  if (activeAgent) {
    $("#micAgentName").textContent = activeAgentName || activeAgent;
  }
}

async function handback() {
  try {
    const response = await api("/api/chat/handback", { method: "POST", body: { userId: currentUser() } });
    renderMicBanner(null, null);
    if (response.returnedFrom) {
      appendMessage("system", "— Потребителят върна разговора на Асистента —", null);
      notify("success", "Върнато", "Асистентът пое разговора.");
    }
  } catch (error) {
    notify("error", "Връщането не успя", error.message);
  }
}

function appendStreamingBubble() {
  $("#chatEmpty")?.remove();
  const container = document.createElement("div");
  container.className = "chat-message assistant";
  container.innerHTML = `<div class="bubble"></div>`;
  $("#chatMessages").appendChild(container);
  return container;
}

async function compactNow() {
  if (state.sending) return;
  setBusy($("#compactButton"), true);
  try {
    const response = await api("/api/chat/compact", { method: "POST", body: { userId: currentUser() } });
    if (response.archived) {
      clearMessages();
      $("#seedNote").classList.remove("is-hidden");
      renderMeter({ usedTokens: 0, budgetTokens: state.runtime?.contextTokenBudget || 0, percent: 0, warning: false, compactImminent: false });
      notify("success", "Чатът беше компактнат", `Резюмето е записано като ${response.archived.fileName}.`);
      await loadArchives();
    } else {
      notify("info", "Няма какво да се компактва", "Разговорът е празен.");
    }
  } catch (error) {
    notify("error", "Компактирането не успя", error.message);
  } finally {
    setBusy($("#compactButton"), false);
  }
}

async function startNewChat() {
  if (state.sending) return;
  setBusy($("#newChatButton"), true);
  try {
    const response = await api("/api/chat/new", { method: "POST", body: { userId: currentUser(), archive: true } });
    clearMessages();
    $("#seedNote").classList.add("is-hidden");
    renderMeter({ usedTokens: 0, budgetTokens: state.runtime?.contextTokenBudget || 0, percent: 0, warning: false, compactImminent: false });
    if (response.archived) {
      notify("success", "Нов чат", `Старият разговор е архивиран като ${response.archived.fileName}.`);
      await loadArchives();
    } else {
      notify("info", "Нов чат", "Започваме на чисто.");
    }
  } catch (error) {
    notify("error", "Новият чат не се стартира", error.message);
  } finally {
    setBusy($("#newChatButton"), false);
  }
}

// --- Рендиране --------------------------------------------------------------

function appendMessage(role, text, turn) {
  $("#chatEmpty")?.remove();
  if (role === "system") {
    const note = renderSystemNote(text);
    $("#chatMessages").appendChild(note);
    note.scrollIntoView({ behavior: "smooth", block: "end" });
    return note;
  }

  const container = document.createElement("div");
  container.className = `chat-message ${role}`;

  let contextHtml = "";
  if (turn) {
    // Контекст блокът има смисъл само за живи ходове (context е масив);
    // при възстановени от базата пълният одит е в Историята.
    let contextBlock = "";
    if (Array.isArray(turn.context)) {
      const snippets = turn.context;
      const body = snippets.length
        ? snippets.map(snippet => `
            <div class="context-snippet">
              <div class="context-snippet-head"><strong>${escapeHtml(snippet.documentTitle)}</strong><em>score ${Number(snippet.score).toFixed(4)} · ${escapeHtml(snippet.strategy)}</em></div>
              <pre>${escapeHtml(snippet.text)}</pre>
            </div>`).join("")
        : `<div class="context-none">Архивът не върна нищо за този въпрос — моделът отговаря само от текущия разговор.</div>`;
      contextBlock = `
        <details class="context-details">
          <summary><svg class="icon"><use href="#icon-search"></use></svg> Контекст, подаден на модела · ${snippets.length} откъса <svg class="icon"><use href="#icon-chevron"></use></svg></summary>
          ${body}
        </details>`;
    }

    contextHtml = `
      ${contextBlock}
      ${turn.steps?.length ? `<div class="turn-steps">${turn.steps.map(step => `<span>${escapeHtml(step)}</span>`).join("")}</div>` : ""}
      <div class="message-meta">${escapeHtml(agentName(turn.agentKey))} · ${escapeHtml(turn.model || "?")} · промпт v${turn.promptVersion ?? "?"} · токени: ${turn.usage.inputTokens} вход · ${turn.usage.outputTokens} изход</div>`;
  }

  const diaryHtml = turn
    ? renderDiaryConfirmations(turn.diaryConfirmations)
    : "";
  container.innerHTML = `${diaryHtml}<div class="bubble">${formatText(text)}</div>${contextHtml}`;
  $("#chatMessages").appendChild(container);
  container.scrollIntoView({ behavior: "smooth", block: "end" });
  return container;
}

// Ключ на агент → човешкото му име („coach" → „Треньор") — зарежда се веднъж.
let agentNamesMap = null;

async function loadAgentNames() {
  try {
    const agents = await api("/api/chat/agents");
    agentNamesMap = Object.fromEntries(agents.map(agent => [agent.key, agent.displayName]));
  } catch { agentNamesMap = {}; }
}

function agentName(key) {
  return (agentNamesMap && agentNamesMap[key]) || key || "Асистент";
}

function appendTyping() {
  const container = document.createElement("div");
  container.className = "chat-message assistant";
  container.innerHTML = `<div class="bubble"><span class="typing"><span></span><span></span><span></span></span></div>`;
  $("#chatMessages").appendChild(container);
  container.scrollIntoView({ behavior: "smooth", block: "end" });
  return container;
}

function clearMessages() {
  $("#chatMessages").innerHTML = `
    <div class="chat-empty" id="chatEmpty">
      <svg class="icon"><use href="#icon-spark"></use></svg>
      <strong>Нов разговор</strong>
      <p>Пиши свободно. Архивът пази резюметата на старите разговори и моделът ги намира чрез търсенето.</p>
    </div>`;
}

function renderMeter(meter) {
  const percent = Math.round((meter.percent || 0) * 100);
  const fill = $("#meterFill");
  fill.style.width = `${Math.min(100, percent)}%`;
  fill.classList.toggle("warn", meter.warning && !meter.compactImminent);
  fill.classList.toggle("danger", meter.compactImminent);
  $("#meterText").textContent = `Контекст: ${percent}% (${formatNumber(meter.usedTokens)}/${formatNumber(meter.budgetTokens)} ток.)`;

  const banner = $("#compactBanner");
  banner.classList.toggle("is-hidden", !meter.warning);
  banner.classList.toggle("is-danger", meter.compactImminent);
  $("#compactBannerText").textContent = meter.compactImminent
    ? "Контекстът достигна лимита — чатът ще бъде компактнат автоматично при следващото съобщение."
    : `Контекстът е на ${percent}% — наближава автоматичното компактиране (на 90%). Можеш да компактнеш и ръчно.`;
}

function renderArchives(archives) {
  const list = $("#archiveList");
  if (!archives || archives.length === 0) {
    list.innerHTML = `<p class="sidebar-hint">Все още няма архивирани разговори.</p>`;
    return;
  }
  list.innerHTML = archives.map(item => `
    <button class="archive-item" type="button" data-archive-id="${escapeHtml(item.id)}" data-archive-title="${escapeHtml(item.title)}">
      <strong>${escapeHtml(item.title)}</strong>
      <small>${escapeHtml(item.fileName)} · ${formatNumber(item.contentLength)} знака</small>
    </button>`).join("");
}

async function loadArchives() {
  try {
    renderArchives(await api(`/api/chat/archives?user=${encodeURIComponent(currentUser())}`));
  } catch (error) {
    notify("error", "Архивът не се зареди", error.message);
  }
}

async function openArchive(id, title) {
  try {
    const content = await api(`/api/chat/archives/${encodeURIComponent(id)}?user=${encodeURIComponent(currentUser())}`);
    $("#archiveDialogTitle").textContent = title || "Архив";
    $("#archiveDialogContent").textContent = content;
    $("#archiveDialog").showModal();
  } catch (error) {
    notify("error", "Архивът не се отвори", error.message);
  }
}

// --- Помощни ----------------------------------------------------------------

async function api(path, options = {}) {
  const method = options.method || "GET";
  const headers = { Accept: "application/json" };
  if (method !== "GET") headers["X-Search-Lab"] = "1";
  const fetchOptions = { method, headers };
  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
    fetchOptions.body = JSON.stringify(options.body);
  }
  const response = await fetch(path, fetchOptions);
  if (!response.ok) {
    let problem;
    try { problem = await response.json(); } catch { problem = null; }
    throw new Error(problem?.detail || problem?.title || `HTTP ${response.status}`);
  }
  const contentType = response.headers.get("content-type") || "";
  return contentType.includes("json") ? response.json() : response.text();
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;").replaceAll("'", "&#39;");
}

// Минимално форматиране: escape + **удебеляване** + [цитати] + нови редове.
function formatText(text) {
  let safe = escapeHtml(text);
  safe = safe.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  safe = safe.replace(/\[([\w][\w.\-]{0,60})\]/g, '<span class="citation">[$1]</span>');
  return safe.replaceAll("\n", "<br>");
}

function formatNumber(value) {
  return Number(value || 0).toLocaleString("bg-BG");
}

function setBusy(button, busy) {
  button.disabled = busy;
}

function notify(kind, title, message) {
  const toast = document.createElement("div");
  toast.className = `toast ${kind}`;
  toast.innerHTML = `<strong>${escapeHtml(title)}</strong><p>${escapeHtml(message)}</p>`;
  $("#toastStack").appendChild(toast);
  setTimeout(() => toast.remove(), 6000);
}
