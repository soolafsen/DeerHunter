const connectionStatus = document.querySelector("#connection-status");
const hostSummaryMeta = document.querySelector("#host-summary-meta");
const hostSummary = document.querySelector("#host-summary");
const processesMeta = document.querySelector("#processes-meta");
const processesArea = document.querySelector("#processes-area");
const eventsMeta = document.querySelector("#events-meta");
const eventsArea = document.querySelector("#events-area");
const actionsArea = document.querySelector("#actions-area");
const actionFeedback = document.querySelector("#action-feedback");
const refreshButton = document.querySelector("#refresh-button");
const autoRefreshIntervalMs = 2000;
const eventQueryTake = 20;
let refreshTimer = null;
let globalFeedbackMessage = "";
const processFeedbackById = new Map();

const statTemplate = document.querySelector("#stat-template");
const processTemplate = document.querySelector("#process-template");
const eventTemplate = document.querySelector("#event-template");

const hostActions = [
  { action: "pause", label: "Pause supervision", style: "secondary" },
  { action: "resume", label: "Resume supervision", style: "secondary" },
  { action: "reload", label: "Reload config", style: "secondary" },
  { action: "shutdown", label: "Request shutdown", style: "danger" }
];
const processActions = [
  { action: "start", label: "Start", style: "secondary" },
  { action: "stop", label: "Stop", style: "secondary" },
  { action: "restart", label: "Restart", style: "danger" }
];
const priorityOptions = ["Idle", "BelowNormal", "Normal", "AboveNormal", "High", "RealTime"];

refreshButton?.addEventListener("click", () => {
  void loadDashboard();
});

function formatValue(value) {
  if (value === null || value === undefined || value === "") {
    return "n/a";
  }

  if (typeof value === "boolean") {
    return value ? "Yes" : "No";
  }

  return String(value);
}

function formatDateTime(value) {
  if (!value) {
    return "n/a";
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return formatValue(value);
  }

  return parsed.toLocaleString();
}

function formatEventTarget(event) {
  if (event.processName) {
    return event.processName;
  }

  const hostTarget = event.details?.hostTarget || event.details?.target;
  if (hostTarget) {
    return hostTarget;
  }

  if (String(event.eventType || "").startsWith("host.")) {
    return "Host";
  }

  return "n/a";
}

function formatEventDetails(details) {
  if (!details || typeof details !== "object") {
    return "No extra details";
  }

  const entries = Object.entries(details)
    .filter(([, value]) => value !== null && value !== undefined && value !== "")
    .map(([key, value]) => `${key}: ${value}`);

  return entries.length ? entries.join(" | ") : "No extra details";
}

function formatDuration(seconds) {
  if (seconds === null || seconds === undefined || Number.isNaN(Number(seconds))) {
    return "n/a";
  }

  const totalSeconds = Math.max(0, Number(seconds));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const remainingSeconds = Math.floor(totalSeconds % 60);
  return `${hours}h ${minutes}m ${remainingSeconds}s`;
}

function formatError(error) {
  if (error instanceof Error && error.message) {
    return error.message;
  }

  return String(error ?? "Unknown error");
}

function setConnectionState(text, isHealthy) {
  if (!connectionStatus) {
    return;
  }

  connectionStatus.textContent = text;
  connectionStatus.style.background = isHealthy ? "rgba(47, 107, 79, 0.12)" : "rgba(139, 45, 45, 0.14)";
  connectionStatus.style.color = isHealthy ? "#1f4b36" : "#8b2d2d";
}

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    throw new Error(await readErrorMessage(response));
  }

  return response.json();
}

async function readErrorMessage(response) {
  const contentType = response.headers.get("content-type") || "";

  if (contentType.includes("application/json")) {
    try {
      const payload = await response.json();
      const errorCode = payload?.code ? `${payload.code}: ` : "";
      const message = payload?.message || response.statusText;
      return `${response.status} ${errorCode}${message}`;
    } catch {
      return `${response.status} ${response.statusText}`;
    }
  }

  const message = await response.text();
  return `${response.status} ${response.statusText}: ${message}`;
}

function renderStats(host) {
  const stats = [
    ["Supervision", host.supervisionState],
    ["Paused", host.supervisionPaused],
    ["Config", host.configPath],
    ["Started", formatDateTime(host.startedAtUtc)],
    ["Uptime", formatDuration(host.uptimeSeconds)],
    ["Processes", host.managedProcessCount],
    ["Helpers", host.helperCount],
    ["Buffered events", host.bufferedEventCount]
  ];

  hostSummary.replaceChildren();

  for (const [label, value] of stats) {
    const fragment = statTemplate.content.cloneNode(true);
    fragment.querySelector("dt").textContent = label;
    fragment.querySelector("dd").textContent = formatValue(value);
    hostSummary.append(fragment);
  }

  hostSummaryMeta.textContent = `Config: ${formatValue(host.configPath)}`;
}

function renderActions(host) {
  actionsArea.replaceChildren();

  for (const item of hostActions) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "action-button";
    button.dataset.style = item.style;
    button.textContent = item.label;
    button.disabled =
      (item.action === "pause" && host.supervisionPaused) ||
      (item.action === "resume" && !host.supervisionPaused);

    button.addEventListener("click", async () => {
      actionFeedback.textContent = `Running ${item.label.toLowerCase()}...`;

      try {
        await fetchJson(`/api/host/actions/${item.action}`, { method: "POST" });
        globalFeedbackMessage = `${item.label} accepted.`;
        actionFeedback.textContent = globalFeedbackMessage;
        await loadDashboard();
      } catch (error) {
        globalFeedbackMessage = `Action failed: ${formatError(error)}`;
        actionFeedback.textContent = globalFeedbackMessage;
      }
    });

    actionsArea.append(button);
  }
}

function renderProcesses(processes) {
  processesArea.replaceChildren();

  if (!processes.length) {
    processesArea.append(createEmptyState("No managed processes are configured."));
    processesMeta.textContent = "0 processes";
    return;
  }

  processesMeta.textContent = `${processes.length} process snapshot${processes.length === 1 ? "" : "s"}`;

  for (const item of processes) {
    const fragment = processTemplate.content.cloneNode(true);
    const snapshot = item.snapshot || {};
    const role = snapshot.isHelper ? "Helper" : "Managed process";
    const summaryParts = [
      `${role} status: ${formatValue(snapshot.lifecycle)}`,
      `Condition: ${formatValue(snapshot.condition)}`
    ];

    if (snapshot.isHelper && snapshot.helperFor) {
      summaryParts.push(`For: ${snapshot.helperFor}`);
    }

    fragment.querySelector(".process-name").textContent = snapshot.name || item.id || "Unnamed process";
    fragment.querySelector(".process-description").textContent = item.name || snapshot.description || item.id || "";
    fragment.querySelector(".process-role").textContent = role;
    fragment.querySelector(".process-lifecycle").textContent = formatValue(snapshot.lifecycle);
    fragment.querySelector(".process-lifecycle").dataset.state = String(snapshot.lifecycle || "").toLowerCase();
    fragment.querySelector(".process-summary").textContent = summaryParts.join(" | ");

    const processFeedbackElement = fragment.querySelector(".process-feedback");
    const processActionsElement = fragment.querySelector(".process-actions");
    const processId = item.id || snapshot.name || "unknown";
    renderProcessActions(processId, snapshot, processActionsElement, processFeedbackElement);
    processFeedbackElement.textContent = processFeedbackById.get(processId) || "";

    const details = [
      ["Condition", snapshot.condition],
      ["Priority", snapshot.priority],
      ["Helper", snapshot.isHelper],
      ["Helper for", snapshot.helperFor],
      ["Auto start", snapshot.autoStart],
      ["Restart attempts", snapshot.restartAttempts],
      ["Last started", formatDateTime(snapshot.lastStartedAtUtc)],
      ["Last stopped", formatDateTime(snapshot.lastStoppedAtUtc)],
      ["Pid", snapshot.processId],
      ["Last exit", snapshot.lastExitCode],
      ["Last error", snapshot.lastError]
    ];

    const list = fragment.querySelector(".process-details");
    for (const [label, value] of details) {
      const container = document.createElement("div");
      const term = document.createElement("dt");
      const description = document.createElement("dd");
      term.textContent = label;
      description.textContent = formatValue(value);
      container.append(term, description);
      list.append(container);
    }

    processesArea.append(fragment);
  }
}

function renderProcessActions(processId, snapshot, container, feedbackElement) {
  for (const item of processActions) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "action-button";
    button.dataset.style = item.style;
    button.textContent = item.label;
    button.addEventListener("click", async () => {
      const pendingMessage = `${item.label} requested...`;
      processFeedbackById.set(processId, pendingMessage);
      feedbackElement.textContent = pendingMessage;

      try {
        await fetchJson(`/api/processes/${encodeURIComponent(processId)}/actions/${item.action}`, { method: "POST" });
        const successMessage = `${item.label} accepted for ${processId}.`;
        processFeedbackById.set(processId, successMessage);
        feedbackElement.textContent = successMessage;
        globalFeedbackMessage = successMessage;
        actionFeedback.textContent = globalFeedbackMessage;
        await loadDashboard();
      } catch (error) {
        const message = formatError(error);
        const failureMessage = `Action failed: ${message}`;
        processFeedbackById.set(processId, failureMessage);
        feedbackElement.textContent = failureMessage;
        globalFeedbackMessage = `${processId}: ${message}`;
        actionFeedback.textContent = globalFeedbackMessage;
      }
    });

    container.append(button);
  }

  const prioritySelect = document.createElement("select");
  prioritySelect.className = "priority-select";
  prioritySelect.setAttribute("aria-label", `${processId} priority`);

  for (const value of priorityOptions) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = value;
    option.selected = value === snapshot.priority;
    prioritySelect.append(option);
  }

  const applyButton = document.createElement("button");
  applyButton.type = "button";
  applyButton.className = "action-button";
  applyButton.textContent = "Apply priority";
  applyButton.addEventListener("click", async () => {
    const selectedPriority = prioritySelect.value;
    const pendingMessage = `Changing priority to ${selectedPriority}...`;
    processFeedbackById.set(processId, pendingMessage);
    feedbackElement.textContent = pendingMessage;

    try {
      await fetchJson(`/api/processes/${encodeURIComponent(processId)}/priority`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({ priority: selectedPriority })
      });
      const successMessage = `Priority changed to ${selectedPriority} for ${processId}.`;
      processFeedbackById.set(processId, successMessage);
      feedbackElement.textContent = successMessage;
      globalFeedbackMessage = successMessage;
      actionFeedback.textContent = globalFeedbackMessage;
      await loadDashboard();
    } catch (error) {
      const message = formatError(error);
      const failureMessage = `Priority update failed: ${message}`;
      processFeedbackById.set(processId, failureMessage);
      feedbackElement.textContent = failureMessage;
      globalFeedbackMessage = `${processId}: ${message}`;
      actionFeedback.textContent = globalFeedbackMessage;
    }
  });

  container.append(prioritySelect, applyButton);
}

function renderEvents(events) {
  eventsArea.replaceChildren();

  if (!events.length) {
    eventsArea.append(createEmptyState("No recent events are buffered."));
    eventsMeta.textContent = "0 recent events";
    return;
  }

  eventsMeta.textContent = `${events.length} recent event${events.length === 1 ? "" : "s"}`;

  const orderedEvents = [...events].reverse();

  for (const event of orderedEvents) {
    const fragment = eventTemplate.content.cloneNode(true);
    fragment.querySelector(".event-type").textContent = formatValue(event.eventType);
    fragment.querySelector(".event-time").textContent = formatDateTime(event.timestampUtc);
    fragment.querySelector(".event-message").textContent = formatValue(event.message);
    fragment.querySelector(".event-meta").textContent =
      `${formatValue(event.source)} | ${formatEventTarget(event)} | ${formatEventDetails(event.details)}`;
    eventsArea.append(fragment);
  }
}

function createEmptyState(message) {
  const element = document.createElement("div");
  element.className = "empty-state";
  element.textContent = message;
  return element;
}

async function loadDashboard() {
  setConnectionState("Refreshing", true);

  const [hostResult, processesResult, eventsResult] = await Promise.allSettled([
    fetchJson("/api/host/status"),
    fetchJson("/api/processes"),
    fetchJson(`/api/events?take=${eventQueryTake}`)
  ]);

  const hostLoaded = hostResult.status === "fulfilled";
  const processesLoaded = processesResult.status === "fulfilled";
  const eventsLoaded = eventsResult.status === "fulfilled" && Array.isArray(eventsResult.value);

  try {
    if (!hostLoaded) {
      throw hostResult.reason;
    }

    if (!processesLoaded) {
      throw processesResult.reason;
    }

    const host = hostResult.value;
    const processes = processesResult.value;
    renderStats(host);
    renderActions(host);
    renderProcesses(processes);
    setConnectionState(eventsLoaded ? "Connected" : "Connected with event feed issue", eventsLoaded);
    actionFeedback.textContent = globalFeedbackMessage || (eventsLoaded ? "" : `Events refresh failed: ${formatError(eventsResult.reason)}`);
  } catch (error) {
    hostSummary.replaceChildren(createEmptyState("Dashboard could not load local API data."));
    processesArea.replaceChildren(createEmptyState("Processes are unavailable."));
    eventsArea.replaceChildren(createEmptyState("Events are unavailable."));
    actionsArea.replaceChildren(createEmptyState("Actions are unavailable while the API is unreachable."));
    hostSummaryMeta.textContent = "Local API request failed";
    processesMeta.textContent = "Unavailable";
    eventsMeta.textContent = "Unavailable";
    globalFeedbackMessage = formatError(error);
    actionFeedback.textContent = globalFeedbackMessage;
    setConnectionState("Offline", false);
    return;
  }

  if (!eventsLoaded) {
    eventsArea.replaceChildren(createEmptyState("Recent events could not be loaded. Try refresh again."));
    eventsMeta.textContent = "Event feed unavailable";
    return;
  }

  renderEvents(eventsResult.value);
}

function startAutoRefresh() {
  if (refreshTimer !== null) {
    window.clearInterval(refreshTimer);
  }

  refreshTimer = window.setInterval(() => {
    void loadDashboard();
  }, autoRefreshIntervalMs);
}

startAutoRefresh();
void loadDashboard();
