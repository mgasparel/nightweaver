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
