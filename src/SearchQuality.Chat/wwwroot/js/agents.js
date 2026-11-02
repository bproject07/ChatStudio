"use strict";

// Страницата „Агенти и промптове": списък, редактор на версионирани промптове,
// визитка/модел/включване и каталогът на инструментите. Всичко минава през
// същото /api/chat/agents API — страницата е само по-широк изглед.

const $ = selector => document.querySelector(selector);

const agentsUi = { agents: [], selectedAgent: null, versions: [], selectedVersion: null };
let toolCatalog = null;

document.addEventListener("DOMContentLoaded", async () => {
  $("#saveVersionButton").addEventListener("click", saveNewVersion);
  $("#agentModelSelect").addEventListener("change", saveAgentModel);
  $("#agentEnabled").addEventListener("change", saveAgentEnabled);
  $("#saveActivation").addEventListener("click", saveAgentActivation);
  $("#newAgentButton").addEventListener("click", openNewAgentDialog);
  $("#closeNewAgentDialog").addEventListener("click", () => $("#newAgentDialog").close());
  $("#createAgentButton").addEventListener("click", createAgent);
  $("#promptEditor").addEventListener("input", updateEditorState);

  document.addEventListener("click", event => {
    const agentItem = event.target.closest("[data-agent-key]");
    if (agentItem) selectAgent(agentItem.dataset.agentKey);
    const versionRow = event.target.closest(".version-row[data-version]");
    if (versionRow && !event.target.closest("[data-activate-version]")) {
      selectVersion(Number(versionRow.dataset.version));
    }
  });

  // Незапазен промпт не бива да се губи мълчаливо при затваряне на таба.
  window.addEventListener("beforeunload", event => {
    if (isEditorDirty()) event.preventDefault();
  });

  try {
    agentsUi.agents = await api("/api/chat/agents");
    await selectAgent(agentsUi.agents[0]?.key);
  } catch (error) {
    notify("error", "Агентите не се заредиха", error.message);
  }
});

async function selectAgent(key) {
  if (!key) return;
  if (agentsUi.selectedAgent && agentsUi.selectedAgent !== key
      && isEditorDirty() && !confirm("Имаш незапазени промени в промпта. Да ги загубя ли?")) {
    return;
  }
  agentsUi.selectedAgent = key;
  renderAgentsList();
  const agent = agentsUi.agents.find(item => item.key === key);
  const kindLabel = agent?.kind === "system" ? "системен" : agent?.kind === "specialist" ? "специалист" : "оркестратор";
  $("#agentTitle").textContent = agent ? `${agent.displayName} (${kindLabel})` : key;
  const isSpecialist = agent?.kind === "specialist";
  $("#agentEnabledWrap").classList.toggle("is-hidden", !isSpecialist);
  $("#activationWrap").classList.toggle("is-hidden", !isSpecialist);
  if (isSpecialist) {
    $("#agentEnabled").checked = agent.enabled;
    $("#agentActivation").value = agent.activation || "";
  }
  renderAgentTools(agent);
  // Моделите за dropdown-а — от provider-а; текущият модел на агента е избран.
  try {
    const response = await api("/api/chat/models");
    const models = response.models || [];
    if (agent && !models.includes(agent.model)) models.unshift(agent.model);
    $("#agentModelSelect").innerHTML = models
      .map(model => `<option value="${escapeHtml(model)}"${model === agent?.model ? " selected" : ""}>${escapeHtml(model)}</option>`)
      .join("");
  } catch { /* dropdown-ът остава празен, не е критично */ }
  try {
    agentsUi.versions = await api(`/api/chat/agents/${encodeURIComponent(key)}/versions`);
    const active = agentsUi.versions.find(version => version.active);
    selectVersion(active ? active.version : agentsUi.versions[0]?.version, true);
  } catch (error) {
    notify("error", "Версиите не се заредиха", error.message);
  }
}

function renderAgentsList() {
  $("#agentsList").innerHTML = agentsUi.agents.map(agent => `
    <button class="agent-item${agent.key === agentsUi.selectedAgent ? " is-active" : ""}" type="button" data-agent-key="${escapeHtml(agent.key)}">
      <strong>${escapeHtml(agent.displayName)}</strong>
      <small>${escapeHtml(agent.model)} · промпт v${agent.activeVersion ?? "?"} · ${escapeHtml(agent.kind)}</small>
    </button>`).join("");
}

// Каталогът е статичен за сесията — тегли се веднъж; показва на избрания
// агент точно инструментите (и описанията), които вижда моделът.
async function renderAgentTools(agent) {
  if (!agent || agent.kind === "system") {
    $("#agentToolsCount").textContent = "";
    $("#agentToolsList").innerHTML = `<p class="sidebar-hint">Този агент няма инструменти — вика се програмно (компактиране).</p>`;
    return;
  }
  try {
    toolCatalog = toolCatalog || await api("/api/chat/tools");
  } catch {
    $("#agentToolsList").innerHTML = `<p class="sidebar-hint">Каталогът не се зареди.</p>`;
    return;
  }
  const tools = agent.kind === "specialist" ? toolCatalog.specialist : toolCatalog.chat;
  $("#agentToolsCount").textContent = `(${tools.length})`;
  $("#agentToolsList").innerHTML = tools.map(tool => `
    <div class="tool-row">
      <code>${escapeHtml(tool.name)}</code>
      <span>${escapeHtml(tool.description)}</span>
    </div>`).join("");
}

function selectVersion(versionNumber, skipDirtyCheck = false) {
  if (!skipDirtyCheck && isEditorDirty()
      && !confirm("Имаш незапазени промени в промпта. Да ги загубя ли?")) {
    return;
  }
  agentsUi.selectedVersion = versionNumber;
  $("#versionsList").innerHTML = agentsUi.versions.map(version => `
    <button class="version-row${version.version === versionNumber ? " is-viewing" : ""}" type="button" data-version="${version.version}">
      <span class="version-number">v${version.version}</span>
      <span class="version-note${version.note ? "" : " is-empty"}">${escapeHtml(version.note || "без бележка")}</span>
      <span class="version-date">${formatDate(version.createdAt)}</span>
      ${version.active
        ? `<span class="live-badge">АКТИВНА</span>`
        : `<span class="secondary-button" role="button" data-activate-version="${version.version}">Активирай</span>`}
    </button>`).join("");
  const selected = agentsUi.versions.find(version => version.version === versionNumber);
  if (selected) $("#promptEditor").value = selected.instructions;
  updateEditorState();
  document.querySelectorAll("[data-activate-version]").forEach(button =>
    button.addEventListener("click", async event => {
      event.stopPropagation();
      await activateVersion(Number(button.dataset.activateVersion));
    }));
}

function isEditorDirty() {
  const viewed = agentsUi.versions.find(version => version.version === agentsUi.selectedVersion);
  return viewed ? $("#promptEditor").value !== viewed.instructions : $("#promptEditor").value.trim().length > 0;
}

// Едно изречение винаги казва къде си: коя версия гледаш, коя е активната,
// има ли незаписани промени. „Запази“ светва само при реална промяна.
function updateEditorState() {
  const active = agentsUi.versions.find(version => version.active);
  const viewing = agentsUi.selectedVersion;
  const dirty = isEditorDirty();
  let text = `Разглеждаш <strong>v${viewing ?? "?"}</strong>`;
  if (active && active.version === viewing) {
    text += " · тя е активната";
  } else if (active) {
    text += ` · активна е <strong>v${active.version}</strong>`;
  }
  if (dirty) {
    text += ` · <span class="dirty-hint">променено — запази го като нова версия</span>`;
  }
  $("#editorState").innerHTML = text;
  $("#saveVersionButton").disabled = !dirty;
  const next = agentsUi.versions.length ? Math.max(...agentsUi.versions.map(v => v.version)) + 1 : 1;
  $("#saveVersionButton").textContent = `Запази като v${next}`;
}

async function activateVersion(versionNumber) {
  try {
    await api(`/api/chat/agents/${encodeURIComponent(agentsUi.selectedAgent)}/activate`, {
      method: "POST",
      body: { version: versionNumber }
    });
    notify("success", "Версията е активирана", `${agentsUi.selectedAgent} · промпт v${versionNumber} — действа от следващото съобщение.`);
    agentsUi.agents = await api("/api/chat/agents");
    await selectAgent(agentsUi.selectedAgent);
  } catch (error) {
    notify("error", "Активирането не успя", error.message);
  }
}

async function saveNewVersion() {
  const instructions = $("#promptEditor").value.trim();
  if (!instructions || !isEditorDirty()) {
    return;
  }
  try {
    const version = await api(`/api/chat/agents/${encodeURIComponent(agentsUi.selectedAgent)}/versions`, {
      method: "POST",
      body: {
        instructions,
        note: $("#versionNote").value.trim() || null,
        activate: $("#activateNew").checked
      }
    });
    $("#versionNote").value = "";
    notify("success", `Записана е v${version.version}`,
      version.active ? "И е активирана — действа от следващото съобщение." : "Запазена е, без да е активна.");
    agentsUi.agents = await api("/api/chat/agents");
    await selectAgent(agentsUi.selectedAgent);
  } catch (error) {
    notify("error", "Версията не се записа", error.message);
  }
}

async function saveAgentModel() {
  try {
    agentsUi.agents = await api(`/api/chat/agents/${encodeURIComponent(agentsUi.selectedAgent)}`, {
      method: "POST",
      body: { model: $("#agentModelSelect").value }
    });
    renderAgentsList();
    notify("success", "Моделът е сменен", `${agentsUi.selectedAgent} → ${$("#agentModelSelect").value}`);
  } catch (error) {
    notify("error", "Моделът не се смени", error.message);
  }
}

async function saveAgentEnabled() {
  try {
    agentsUi.agents = await api(`/api/chat/agents/${encodeURIComponent(agentsUi.selectedAgent)}`, {
      method: "POST",
      body: { enabled: $("#agentEnabled").checked }
    });
    renderAgentsList();
    notify("success", "Записано", `${agentsUi.selectedAgent}: ${$("#agentEnabled").checked ? "включен" : "изключен"}.`);
  } catch (error) {
    notify("error", "Настройката не се записа", error.message);
  }
}

async function saveAgentActivation() {
  try {
    agentsUi.agents = await api(`/api/chat/agents/${encodeURIComponent(agentsUi.selectedAgent)}`, {
      method: "POST",
      body: { activation: $("#agentActivation").value.trim() }
    });
    renderAgentsList();
    notify("success", "Записано", "Визитката на агента е обновена.");
  } catch (error) {
    notify("error", "Визитката не се записа", error.message);
  }
}

// --- Нов специалист ---------------------------------------------------------

async function openNewAgentDialog() {
  try {
    const response = await api("/api/chat/models");
    $("#newAgentModel").innerHTML = (response.models || [])
      .map(model => `<option value="${escapeHtml(model)}">${escapeHtml(model)}</option>`)
      .join("");
  } catch { /* dropdown-ът остава празен */ }
  $("#newAgentDialog").showModal();
}

async function createAgent() {
  try {
    agentsUi.agents = await api("/api/chat/agents", {
      method: "POST",
      body: {
        key: $("#newAgentKey").value.trim(),
        displayName: $("#newAgentName").value.trim(),
        activation: $("#newAgentActivation").value.trim(),
        model: $("#newAgentModel").value,
        instructions: $("#newAgentPrompt").value.trim()
      }
    });
    $("#newAgentDialog").close();
    notify("success", "Агентът е създаден", "Асистентът вече го вижда в регистъра.");
    renderAgentsList();
    await selectAgent($("#newAgentKey").value.trim().toLowerCase());
    ["#newAgentKey", "#newAgentName", "#newAgentActivation", "#newAgentPrompt"]
      .forEach(id => { $(id).value = ""; });
  } catch (error) {
    notify("error", "Агентът не се създаде", error.message);
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

function formatDate(value) {
  const date = new Date(value);
  return `${String(date.getDate()).padStart(2, "0")}.${String(date.getMonth() + 1).padStart(2, "0")} ${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
}

function notify(kind, title, message) {
  const toast = document.createElement("div");
  toast.className = `toast ${kind}`;
  toast.innerHTML = `<strong>${escapeHtml(title)}</strong><p>${escapeHtml(message)}</p>`;
  $("#toastStack").appendChild(toast);
  setTimeout(() => toast.remove(), 6000);
}
