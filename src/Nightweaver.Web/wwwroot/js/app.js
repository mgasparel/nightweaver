const menuButton = document.querySelector(".nav-toggle");
const navigation = document.querySelector(".primary-nav");
const mobileLayout = window.matchMedia("(max-width: 52rem)");

if (menuButton && navigation) {
  const setExpanded = (expanded) => {
    document.documentElement.classList.toggle("nav-open", expanded);
    menuButton.setAttribute("aria-expanded", String(expanded));
  };

  document.documentElement.classList.add("js-ready");
  setExpanded(!mobileLayout.matches);

  menuButton.addEventListener("click", () => {
    setExpanded(menuButton.getAttribute("aria-expanded") !== "true");
  });

  mobileLayout.addEventListener("change", (event) => {
    setExpanded(!event.matches);
  });

  navigation.addEventListener("click", (event) => {
    if (event.target instanceof HTMLAnchorElement && mobileLayout.matches) {
      setExpanded(false);
    }
  });
}

const attentionCards = document.querySelector("#attention-cards");
const attentionStatus = document.querySelector("#attention-status");
const attentionError = document.querySelector("#attention-error");
const attentionErrorText = document.querySelector("#attention-error-text");
const attentionEmpty = document.querySelector("#attention-empty");
const attentionRefresh = document.querySelector("#attention-refresh");
const attentionRetry = document.querySelector("#attention-retry");

let attentionItems = [];
let attentionRequestGeneration = 0;
let activeAttentionRequest = null;
let attentionHasLoaded = false;
const attentionDrafts = new Map();
const attentionMessages = new Map();
const attentionActions = new Set();
const abortConfirmations = new Set();

function makeAttentionText(tagName, className, value) {
  const element = document.createElement(tagName);
  if (className) element.className = className;
  element.textContent = value;
  return element;
}

function makeAttentionButton(label, className, handler, disabled = false) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = className;
  button.textContent = label;
  button.disabled = disabled;
  button.addEventListener("click", handler);
  return button;
}

function formatRole(role) {
  return ({
    planner: "Planner Agent",
    plan_challenger: "Plan Challenger Agent",
    implementer: "Implementer Agent",
    reviewer: "Reviewer Agent",
    qa: "QA Agent",
    merger: "Merger Agent"
  })[role] ?? "Unknown";
}

function formatPauseTime(item) {
  if (item.pauseTimeSource !== "comment" || !item.pausedAt) return "Pause time: Unknown";
  const parsed = Date.parse(item.pausedAt);
  if (!Number.isFinite(parsed)) return "Pause time: Unknown";
  const elapsed = Math.max(0, Date.now() - parsed);
  const minutes = Math.floor(elapsed / 60000);
  const age = minutes < 1 ? "less than a minute ago"
    : minutes < 60 ? `${minutes} minute${minutes === 1 ? "" : "s"} ago`
      : minutes < 1440 ? `${Math.floor(minutes / 60)} hour${Math.floor(minutes / 60) === 1 ? "" : "s"} ago`
        : `${Math.floor(minutes / 1440)} day${Math.floor(minutes / 1440) === 1 ? "" : "s"} ago`;
  const date = new Date(parsed).toLocaleString();
  return `Selected pause comment: ${age} (${date})`;
}

function setAttentionError(message) {
  if (!attentionError || !attentionErrorText) return;
  attentionErrorText.textContent = message;
  attentionError.hidden = false;
}

function renderAttention() {
  if (!attentionCards || !attentionEmpty) return;
  const fragment = document.createDocumentFragment();
  for (const item of attentionItems) {
    const card = document.createElement("article");
    card.className = `surface attention-card attention-card-${item.kind}`;
    card.dataset.beadId = item.beadId;
    card.setAttribute("aria-busy", String(attentionActions.has(item.beadId)));

    const heading = document.createElement("div");
    heading.className = "attention-card-heading";
    const headingText = document.createElement("div");
    headingText.append(
      makeAttentionText("p", "eyebrow", item.kind === "product-decision-required"
        ? "Product decision required" : item.kind === "needs-human" ? "Needs human" : "Invalid workflow"),
      makeAttentionText("h3", "attention-title", item.title)
    );
    const badge = makeAttentionText("span", "attention-kind", `P${item.priority}`);
    heading.append(headingText, badge);
    card.append(heading);

    const metadata = document.createElement("dl");
    metadata.className = "attention-metadata";
    for (const [label, value] of [
      ["Bead", item.beadId],
      ["Type", item.type || "Unknown"],
      ["Status", item.status || "Unknown"],
      ["Stage", item.stage || "Unknown"],
      ["Role", formatRole(item.fromRole)],
      ["Repair cycles", Number.isInteger(item.repairCycles)
        ? `${item.repairCycles} / ${item.maxRepairCycles}` : `Unknown / ${item.maxRepairCycles}`]
    ]) {
      const pair = document.createElement("div");
      pair.className = "attention-meta-pair";
      pair.append(makeAttentionText("dt", "", label), makeAttentionText("dd", "", value));
      metadata.append(pair);
    }
    card.append(metadata, makeAttentionText("p", "attention-pause-time", formatPauseTime(item)));

    if (item.kind === "product-decision-required") {
      card.append(makeAttentionText("p", "attention-context-label", "Latest comment/context (legacy selection; no shared question schema)"));
      card.append(makeAttentionText("p", "attention-question", item.question || "No attributable clarification comment is available."));
      card.append(makeAttentionText("p", "attention-source", `Comment author: ${item.sourceAuthor || "Unknown"}`));
    } else {
      card.append(makeAttentionText("p", "attention-context-label", "Failure diagnostic"));
      const diagnostic = document.createElement("pre");
      diagnostic.className = "attention-diagnostic";
      diagnostic.textContent = item.diagnostic || "No valid diagnostic is available.";
      card.append(diagnostic);
    }

    if (item.unavailableReason) card.append(makeAttentionText("p", "attention-unavailable", item.unavailableReason));

    const message = attentionMessages.get(item.beadId);
    if (message) {
      const result = makeAttentionText("p", "attention-card-message", message);
      result.setAttribute("role", "status");
      result.setAttribute("aria-live", "polite");
      result.tabIndex = -1;
      card.append(result);
    }

    const busy = attentionActions.has(item.beadId);
    if (item.kind === "product-decision-required") {
      const form = document.createElement("div");
      form.className = "attention-guidance";
      const label = document.createElement("label");
      const textarea = document.createElement("textarea");
      const inputId = `guidance-${item.beadId.replace(/[^a-zA-Z0-9_-]/g, "-")}`;
      label.htmlFor = inputId;
      label.textContent = "Human guidance";
      textarea.id = inputId;
      textarea.rows = 4;
      textarea.maxLength = 16384;
      textarea.placeholder = "Write the decision or clarification for the agent…";
      textarea.value = attentionDrafts.get(item.beadId) ?? "";
      textarea.disabled = busy || !item.canDecide;
      textarea.setAttribute("aria-describedby", `${inputId}-help`);
      textarea.addEventListener("input", () => attentionDrafts.set(item.beadId, textarea.value));
      const help = makeAttentionText("span", "field-help", "Outer whitespace is trimmed; maximum 16 KiB of UTF-8 text.");
      help.id = `${inputId}-help`;
      const actions = document.createElement("div");
      actions.className = "attention-actions";
      actions.append(
        makeAttentionButton("Save Note", "secondary-action", () => void submitAttentionAction(item, "note_only", textarea.value),
          busy || !item.canDecide),
        makeAttentionButton("Send & Resume", "primary-action", () => void submitAttentionAction(item, "resume", textarea.value),
          busy || !item.canDecide)
      );
      form.append(label, textarea, help, actions);
      card.append(form);
    } else if (item.kind === "needs-human") {
      const actions = document.createElement("div");
      actions.className = "attention-actions";
      actions.append(makeAttentionButton("Reset Budget & Retry", "primary-action",
        () => void submitAttentionAction(item, "retry"), busy || !item.canRetry));
      const abortButton = makeAttentionButton("Abort & Close", "secondary-action", () => {
        abortConfirmations.add(item.beadId);
        renderAttention();
        cardById(item.beadId)?.querySelector(".attention-abort-cancel")?.focus();
      }, busy || !item.canAbort);
      actions.append(abortButton);
      card.append(actions);

      if (abortConfirmations.has(item.beadId)) {
        const confirmation = document.createElement("div");
        confirmation.className = "attention-abort-confirmation";
        confirmation.tabIndex = -1;
        confirmation.addEventListener("keydown", event => {
          if (event.key === "Escape") {
            event.preventDefault();
            abortConfirmations.delete(item.beadId);
            renderAttention();
            cardById(item.beadId)?.querySelector(".attention-abort-trigger")?.focus();
          }
        });
        confirmation.append(makeAttentionText("p", "", `Close ${item.beadId} as an abort? This does not merge or delete its remote branch or pull request.`));
        confirmation.append(
          makeAttentionButton("Cancel", "secondary-action attention-abort-cancel", () => {
            abortConfirmations.delete(item.beadId);
            renderAttention();
            cardById(item.beadId)?.querySelector(".attention-abort-trigger")?.focus();
          }, busy),
          makeAttentionButton("Confirm Abort & Close", "danger-action", () => {
            abortConfirmations.delete(item.beadId);
            void submitAttentionAction(item, "abort");
          }, busy)
        );
        card.append(confirmation);
      }
      abortButton.classList.add("attention-abort-trigger");
    }
    fragment.append(card);
  }
  attentionCards.replaceChildren(fragment);
  attentionEmpty.hidden = attentionItems.length !== 0 || !attentionHasLoaded;
}

function cardById(beadId) {
  return Array.from(attentionCards?.children ?? []).find(card => card.dataset.beadId === beadId) ?? null;
}

function setAttentionLoading(loading) {
  if (attentionRefresh) attentionRefresh.disabled = loading;
  if (attentionRetry) attentionRetry.disabled = loading;
  if (attentionStatus) attentionStatus.textContent = loading ? "Refreshing paused Beads…" : "";
}

async function loadAttention() {
  if (!attentionCards) return false;
  activeAttentionRequest?.abort();
  const generation = ++attentionRequestGeneration;
  const controller = new AbortController();
  activeAttentionRequest = controller;
  setAttentionLoading(true);
  if (attentionError) attentionError.hidden = true;
  try {
    const response = await fetch("/api/attention", {
      cache: "no-store",
      headers: { Accept: "application/json" },
      signal: controller.signal
    });
    if (!response.ok) {
      let detail = "The Attention Desk could not be refreshed. The last confirmed list remains visible.";
      try {
        const problem = await response.json();
        if (typeof problem.detail === "string" && problem.detail.trim()) detail = problem.detail;
      } catch { /* Use the safe local fallback when a response is not Problem Details JSON. */ }
      throw new Error(detail);
    }
    const items = await response.json();
    if (generation !== attentionRequestGeneration) return false;
    if (!Array.isArray(items)) throw new Error("The Attention Desk response was invalid.");
    attentionItems = items;
    attentionHasLoaded = true;
    renderAttention();
    if (attentionStatus) attentionStatus.textContent = items.length === 0
      ? "The Attention Desk is clear." : `${items.length} Bead${items.length === 1 ? "" : "s"} need human attention.`;
    return true;
  } catch (error) {
    if (error?.name === "AbortError" || generation !== attentionRequestGeneration) return false;
    setAttentionError(error instanceof Error ? error.message : "The Attention Desk could not be refreshed.");
    if (attentionStatus) attentionStatus.textContent = attentionHasLoaded
      ? "Showing the last confirmed list; its current state is unavailable." : "Attention data is unavailable.";
    return false;
  } finally {
    if (generation === attentionRequestGeneration) {
      activeAttentionRequest = null;
      if (attentionRefresh) attentionRefresh.disabled = false;
      if (attentionRetry) attentionRetry.disabled = false;
    }
  }
}

function invalidateAttentionRead() {
  activeAttentionRequest?.abort();
  activeAttentionRequest = null;
  attentionRequestGeneration += 1;
}

async function submitAttentionAction(item, action, instruction = undefined) {
  if (attentionActions.has(item.beadId)) return;
  let focusAfterRender = null;
  attentionActions.add(item.beadId);
  invalidateAttentionRead();
  const card = cardById(item.beadId);
  card?.setAttribute("aria-busy", "true");
  card?.querySelectorAll("button, textarea").forEach(control => { control.disabled = true; });
  if (attentionStatus) attentionStatus.textContent = `Submitting ${action.replaceAll("_", " ")} for ${item.beadId}…`;

  const headers = {
    Accept: "application/json",
    "X-Nightweaver-Request": "console-v1"
  };
  const options = { method: "POST", headers, cache: "no-store" };
  if (action === "resume" || action === "note_only") {
    headers["Content-Type"] = "application/json";
    options.body = JSON.stringify({ instruction, action });
  }

  try {
    const response = await fetch(`/api/beads/${encodeURIComponent(item.beadId)}/${action === "resume" || action === "note_only" ? "decide" : action}`, options);
    let result;
    if (response.ok) {
      result = await response.json();
      const expectedResolved = action !== "note_only";
      if (result.beadId !== item.beadId || result.action !== action || result.resolved !== expectedResolved
        || result.commentRecorded !== true || result.stateChanged !== expectedResolved)
        throw new Error("The action response could not be verified. Refresh before retrying.");
    } else {
      let detail = "The action could not be confirmed. Refresh before retrying.";
      try {
        const problem = await response.json();
        if (typeof problem.detail === "string" && problem.detail.trim()) detail = problem.detail;
        if (Object.hasOwn(problem, "commentRecorded") || Object.hasOwn(problem, "stateChanged")) {
          const comment = problem.commentRecorded === true ? "recorded" : problem.commentRecorded === false ? "not recorded" : "unverified";
          const state = problem.stateChanged === true ? "changed" : problem.stateChanged === false ? "unchanged" : "unverified";
          detail += ` Comment: ${comment}. Workflow state: ${state}.`;
        }
      } catch { /* Keep the safe fallback when the response is not readable. */ }
      throw new Error(detail);
    }

    if (action === "note_only") {
      attentionMessages.set(item.beadId, "Note saved. This Bead remains paused.");
    } else {
      attentionDrafts.delete(item.beadId);
      attentionMessages.delete(item.beadId);
      abortConfirmations.delete(item.beadId);
    }
    await loadAttention();
    await loadBeads();
    if (action === "note_only")
      focusAfterRender = "guidance";
    else if (expectedFocusAfterAction(action, result))
      focusAfterRender = "status";
  } catch (error) {
    attentionMessages.set(item.beadId, error instanceof Error ? error.message : "The action could not be confirmed. Refresh before retrying.");
    if (attentionStatus) attentionStatus.textContent = `Action for ${item.beadId} needs attention.`;
    focusAfterRender = "message";
  } finally {
    attentionActions.delete(item.beadId);
    renderAttention();
    if (focusAfterRender === "guidance") cardById(item.beadId)?.querySelector("textarea")?.focus();
    else if (focusAfterRender === "status") attentionStatus?.focus();
    else if (focusAfterRender === "message") {
      const message = cardById(item.beadId)?.querySelector(".attention-card-message");
      if (message) message.focus();
      else attentionStatus?.focus();
    }
  }
}

function expectedFocusAfterAction(action, result) {
  return action !== "note_only" && result?.resolved === true;
}

if (attentionCards) {
  attentionRefresh?.addEventListener("click", () => void loadAttention());
  attentionRetry?.addEventListener("click", () => void loadAttention());
  void loadAttention();
}

const beadsSearch = document.querySelector("#beads-search");
const beadFilters = Array.from(document.querySelectorAll(".bead-filter"));
const beadsBody = document.querySelector("#beads-table-body");
const beadsLoading = document.querySelector("#beads-loading");
const beadsEmpty = document.querySelector("#beads-empty");
const beadsNoMatches = document.querySelector("#beads-no-matches");
const beadsError = document.querySelector("#beads-error");
const beadsLive = document.querySelector("#beads-live");
const retryButton = document.querySelector("#beads-retry");
const createdMessage = document.querySelector("#beads-create-success");
const createdMessageText = document.querySelector("#beads-create-success-text");
const showCreatedButton = document.querySelector("#beads-show-created");

let selectedView = "all";
let requestGeneration = 0;
let activeRequest = null;
let searchTimer = null;
let createdBeadId = null;
let pendingCreatedCheck = null;

function setExplorerMessage({ loading = false, empty = false, noMatches = false, error = false }) {
  if (!beadsLoading) return;
  beadsLoading.hidden = !loading;
  beadsEmpty.hidden = !empty;
  beadsNoMatches.hidden = !noMatches;
  beadsError.hidden = !error;
  beadsLive.textContent = loading ? "Loading beads."
    : error ? "The Beads tracker is unavailable."
      : empty ? beadsEmpty.textContent
        : noMatches ? "No beads match your search." : "Beads list updated.";
}

function setCreatedMessage(message, showInAll = false) {
  if (!createdMessage || !createdMessageText || !showCreatedButton) return;
  createdMessageText.textContent = message;
  showCreatedButton.hidden = !showInAll;
  createdMessage.hidden = false;
}

function updateFilterControls() {
  for (const button of beadFilters) {
    button.setAttribute("aria-pressed", String(button.dataset.view === selectedView));
  }
}

function textCell(value, className = "") {
  const cell = document.createElement("td");
  if (className) cell.className = className;
  cell.textContent = value;
  return cell;
}

function renderBeads(beads) {
  const rows = document.createDocumentFragment();
  for (const bead of beads) {
    const row = document.createElement("tr");
    row.append(
      textCell(bead.id, "bead-id"),
      textCell(bead.title, "bead-title"),
      textCell(bead.type),
      textCell(`P${bead.priority}`, "bead-priority"),
      textCell(bead.status)
    );

    const labelsCell = document.createElement("td");
    const labels = document.createElement("div");
    labels.className = "bead-labels";
    for (const label of bead.labels) {
      const tag = document.createElement("span");
      tag.className = "bead-label";
      tag.textContent = label;
      labels.append(tag);
    }
    if (bead.labels.length === 0) labels.textContent = "—";
    labelsCell.append(labels);
    row.append(labelsCell, textCell(bead.assignee ?? "Unassigned"));
    rows.append(row);
  }
  beadsBody.replaceChildren(rows);
}

async function loadBeads() {
  if (!beadsSearch || !beadsBody || beadFilters.length === 0) return;
  if (searchTimer !== null) {
    window.clearTimeout(searchTimer);
    searchTimer = null;
  }
  activeRequest?.abort();
  const generation = ++requestGeneration;
  const controller = new AbortController();
  activeRequest = controller;
  beadsBody.replaceChildren();
  setExplorerMessage({ loading: true });

  const parameters = new URLSearchParams();
  parameters.set("view", selectedView);
  const query = beadsSearch.value.trim();
  if (query) parameters.set("q", query);

  try {
    const response = await fetch(`/api/beads?${parameters.toString()}`, {
      headers: { Accept: "application/json" },
      signal: controller.signal
    });
    if (!response.ok) throw new Error("Beads request failed.");
    const beads = await response.json();
    if (generation !== requestGeneration) return;
    if (!Array.isArray(beads)) throw new Error("Beads response was invalid.");
    renderBeads(beads);
    if (pendingCreatedCheck && selectedView === "ready" && query.length === 0) {
      const found = beads.some(bead => bead.id === pendingCreatedCheck);
      setCreatedMessage(found
        ? `Created ${pendingCreatedCheck}. It is in the Ready backlog.`
        : `Created ${pendingCreatedCheck}. Its state may have changed since creation.`, !found);
      pendingCreatedCheck = null;
    }
    if (beads.length === 0) {
      beadsEmpty.textContent = selectedView === "all" ? "No beads are in the tracker yet." : `No beads are in the ${selectedView} view.`;
      setExplorerMessage({ empty: query.length === 0, noMatches: query.length > 0 });
    } else {
      setExplorerMessage({});
    }
  } catch (error) {
    if (error?.name === "AbortError" || generation !== requestGeneration) return;
    beadsBody.replaceChildren();
    setExplorerMessage({ error: true });
    if (pendingCreatedCheck) {
      setCreatedMessage(`Created ${pendingCreatedCheck}. The Ready list could not be refreshed; use Retry to check its current state.`);
    }
  } finally {
    if (generation === requestGeneration) activeRequest = null;
  }
}

function queueSearch() {
  if (searchTimer !== null) window.clearTimeout(searchTimer);
  activeRequest?.abort();
  requestGeneration += 1;
  beadsBody.replaceChildren();
  setExplorerMessage({ loading: true });
  searchTimer = window.setTimeout(() => {
    searchTimer = null;
    void loadBeads();
  }, 180);
}

if (beadsSearch && beadsBody && beadFilters.length > 0) {
  beadsSearch.addEventListener("input", queueSearch);
  for (const button of beadFilters) {
    button.addEventListener("click", () => {
      selectedView = button.dataset.view;
      updateFilterControls();
      void loadBeads();
    });
  }
  retryButton?.addEventListener("click", () => void loadBeads());
  showCreatedButton?.addEventListener("click", () => {
    if (!createdBeadId) return;
    selectedView = "all";
    updateFilterControls();
    beadsSearch.value = createdBeadId;
    pendingCreatedCheck = null;
    showCreatedButton.hidden = true;
    void loadBeads();
  });
  void loadBeads();
}

const intakeTrigger = document.querySelector("#new-bead-trigger");
const intakeDialog = document.querySelector("#bead-intake-dialog");
const intakeForm = document.querySelector("#bead-intake-form");
const intakeTitle = document.querySelector("#intake-title-input");
const intakeError = document.querySelector("#intake-error");
const intakeProgress = document.querySelector("#intake-progress");
const intakeFields = {
  type: document.querySelector("#intake-type"),
  priority: document.querySelector("#intake-priority"),
  title: intakeTitle,
  description: document.querySelector("#intake-description-input"),
  acceptanceCriteria: document.querySelector("#intake-acceptance")
};

if (intakeTrigger && intakeDialog instanceof HTMLDialogElement && intakeForm instanceof HTMLFormElement) {
  const setPending = (pending) => {
    for (const control of intakeForm.querySelectorAll("input, select, textarea, button")) {
      control.disabled = pending;
    }
    intakeProgress.textContent = pending ? "Submitting Product Bead…" : "";
  };

  const clearFieldErrors = () => {
    for (const [name, field] of Object.entries(intakeFields)) {
      const error = document.querySelector(`#intake-${name.replace(/[A-Z]/g, match => `-${match.toLowerCase()}`)}-error`);
      field.removeAttribute("aria-invalid");
      if (error) {
        error.textContent = "";
        error.hidden = true;
      }
    }
    intakeError.textContent = "";
    intakeError.hidden = true;
  };

  const focusErrorSummary = (message) => {
    intakeError.textContent = message;
    intakeError.hidden = false;
    intakeError.focus();
  };

  const showProblem = (problem, status) => {
    clearFieldErrors();
    const errors = problem && typeof problem.errors === "object" && problem.errors !== null ? problem.errors : null;
    let firstInvalid = null;
    if (errors) {
      for (const [name, messages] of Object.entries(errors)) {
        const field = intakeFields[name];
        if (!field || !Array.isArray(messages) || messages.length === 0) continue;
        const errorId = `intake-${name.replace(/[A-Z]/g, match => `-${match.toLowerCase()}`)}-error`;
        const error = document.getElementById(errorId);
        field.setAttribute("aria-invalid", "true");
        if (error) {
          error.textContent = messages.join(" ");
          error.hidden = false;
        }
        firstInvalid ??= field;
      }
    }

    const title = typeof problem?.title === "string" ? problem.title : "Could not create the Product Bead.";
    const detail = typeof problem?.detail === "string" ? problem.detail : `Request failed with status ${status}.`;
    const fieldMessages = errors ? Object.values(errors).flat().filter(message => typeof message === "string").join(" ") : "";
    intakeError.textContent = `${title} ${fieldMessages || detail}`;
    intakeError.hidden = false;
    return firstInvalid ?? intakeError;
  };

  const openDialog = () => {
    clearFieldErrors();
    if (!intakeDialog.open) intakeDialog.showModal();
    intakeTitle.focus();
  };

  intakeTrigger.addEventListener("click", openDialog);
  intakeDialog.addEventListener("close", () => intakeTrigger.focus());
  intakeDialog.addEventListener("cancel", (event) => {
    if (intakeForm.querySelector("button[type='submit']")?.disabled) event.preventDefault();
  });

  document.querySelector("#intake-cancel").addEventListener("click", () => intakeDialog.close());
  document.querySelector("#intake-close").addEventListener("click", () => intakeDialog.close());

  intakeForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (intakeForm.querySelector("button[type='submit']")?.disabled) return;
    clearFieldErrors();
    setPending(true);
    let confirmed = false;
    let focusAfterRestore = null;
    try {
      const payload = {
        type: intakeFields.type.value,
        priority: Number(intakeFields.priority.value),
        title: intakeFields.title.value,
        description: intakeFields.description.value,
        acceptanceCriteria: intakeFields.acceptanceCriteria.value
      };
      const response = await fetch("/api/beads", {
        method: "POST",
        headers: {
          Accept: "application/json, application/problem+json",
          "Content-Type": "application/json"
        },
        body: JSON.stringify(payload)
      });

      if (response.status === 201) {
        const created = await response.json();
        if (!created || typeof created.id !== "string" || !created.id.trim()
          || created.type !== payload.type || created.priority !== payload.priority || created.status !== "open") {
          focusErrorSummary("Creation could not be confirmed. The request may have created a Product Bead. Check the backlog before resubmitting.");
          return;
        }

        const id = created.id;
        intakeForm.reset();
        intakeFields.type.value = "feature";
        intakeFields.priority.value = "2";
        clearFieldErrors();
        setPending(false);
        intakeDialog.close();
        createdBeadId = id;
        pendingCreatedCheck = id;
        setCreatedMessage(`Created ${id}. Checking the Ready backlog.`);
        if (beadsSearch && beadsBody && beadFilters.length > 0) {
          selectedView = "ready";
          beadsSearch.value = "";
          updateFilterControls();
          void loadBeads();
        }
        confirmed = true;
        return;
      }

      let problem = null;
      try { problem = await response.json(); } catch { /* Use the safe generic failure below. */ }
      focusAfterRestore = showProblem(problem, response.status);
    } catch {
      focusErrorSummary("The connection ended before the result was confirmed. The request may have created a Product Bead. Check the backlog before manually resubmitting.");
    } finally {
      if (!confirmed) {
        setPending(false);
        focusAfterRestore?.focus();
      }
    }
  });
}
