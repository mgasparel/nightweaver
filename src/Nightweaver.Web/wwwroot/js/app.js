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
const attentionActionBlocked = new Set();
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
  button.dataset.attentionFocus = label.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
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

function captureAttentionFocus() {
  const active = document.activeElement;
  if (!(active instanceof HTMLElement)) return null;
  const card = active.closest(".attention-card");
  const focusKey = active.dataset.attentionFocus;
  if (!card || !attentionCards?.contains(card) || !focusKey) return null;

  const state = { beadId: card.dataset.beadId, focusKey };
  if (active instanceof HTMLTextAreaElement) {
    attentionDrafts.set(state.beadId, active.value);
    state.selectionStart = active.selectionStart;
    state.selectionEnd = active.selectionEnd;
    state.selectionDirection = active.selectionDirection;
  }
  return state;
}

function restoreAttentionFocus(state) {
  if (!state) return;
  const card = cardById(state.beadId);
  const target = Array.from(card?.querySelectorAll("[data-attention-focus]") ?? [])
    .find(element => element.dataset.attentionFocus === state.focusKey);
  if (!target || target.disabled) return;
  target.focus();
  if (target instanceof HTMLTextAreaElement && Number.isInteger(state.selectionStart)
    && Number.isInteger(state.selectionEnd)) {
    const start = Math.min(state.selectionStart, target.value.length);
    const end = Math.min(state.selectionEnd, target.value.length);
    target.setSelectionRange(start, end, state.selectionDirection ?? "none");
  }
}

function renderAttention() {
  if (!attentionCards || !attentionEmpty) return;
  const focus = captureAttentionFocus();
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
    const workspaceLink = document.createElement("a");
    workspaceLink.className = "attention-workspace-link";
    workspaceLink.href = `#bead/${encodeURIComponent(item.beadId)}`;
    workspaceLink.dataset.beadWorkspaceLink = "true";
    workspaceLink.textContent = "Open Full Bead Workspace";
    card.append(workspaceLink);

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
    const blocked = busy || attentionActionBlocked.has(item.beadId);
    if (item.kind === "product-decision-required") {
      const form = document.createElement("div");
      form.className = "attention-guidance";
      const label = document.createElement("label");
      const textarea = document.createElement("textarea");
      const inputId = `guidance-${item.beadId.replace(/[^a-zA-Z0-9_-]/g, "-")}`;
      label.htmlFor = inputId;
      label.textContent = "Human guidance";
      textarea.id = inputId;
      textarea.dataset.attentionFocus = "guidance";
      textarea.rows = 4;
      textarea.maxLength = 16384;
      textarea.placeholder = "Write the decision or clarification for the agent…";
      textarea.value = attentionDrafts.get(item.beadId) ?? "";
      textarea.disabled = blocked || !item.canDecide;
      textarea.setAttribute("aria-describedby", `${inputId}-help`);
      textarea.addEventListener("input", () => attentionDrafts.set(item.beadId, textarea.value));
      const help = makeAttentionText("span", "field-help", "Outer whitespace is trimmed; maximum 16 KiB of UTF-8 text.");
      help.id = `${inputId}-help`;
      const actions = document.createElement("div");
      actions.className = "attention-actions";
      actions.append(
        makeAttentionButton("Save Note", "secondary-action", () => void submitAttentionAction(item, "note_only", textarea.value),
          blocked || !item.canDecide),
        makeAttentionButton("Send & Resume", "primary-action", () => void submitAttentionAction(item, "resume", textarea.value),
          blocked || !item.canDecide)
      );
      form.append(label, textarea, help, actions);
      card.append(form);
    } else if (item.kind === "needs-human") {
      const actions = document.createElement("div");
      actions.className = "attention-actions";
      actions.append(makeAttentionButton("Reset Budget & Retry", "primary-action",
        () => void submitAttentionAction(item, "retry"), blocked || !item.canRetry));
      const abortButton = makeAttentionButton("Abort & Close", "secondary-action", () => {
        abortConfirmations.add(item.beadId);
        renderAttention();
        cardById(item.beadId)?.querySelector(".attention-abort-cancel")?.focus();
      }, blocked || !item.canAbort);
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
          }, blocked),
          makeAttentionButton("Confirm Abort & Close", "danger-action", () => {
            abortConfirmations.delete(item.beadId);
            void submitAttentionAction(item, "abort");
          }, blocked)
        );
        card.append(confirmation);
      }
      abortButton.classList.add("attention-abort-trigger");
    }
    fragment.append(card);
  }
  attentionCards.replaceChildren(fragment);
  attentionEmpty.hidden = attentionItems.length !== 0 || !attentionHasLoaded;
  restoreAttentionFocus(focus);
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
    for (const beadId of attentionActionBlocked) {
      if (!attentionActions.has(beadId)) attentionActionBlocked.delete(beadId);
    }
    renderAttention();
    if (workspaceData) renderWorkspaceGuidanceActions(workspaceData);
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
  setAttentionLoading(false);
}

async function submitAttentionAction(item, action, instruction = undefined, { workspaceReadConfirmed = false } = {}) {
  if (attentionActions.has(item.beadId) || (!workspaceReadConfirmed && attentionActionBlocked.has(item.beadId))) return false;
  const focusedCardId = document.activeElement instanceof HTMLElement
    ? document.activeElement.closest(".attention-card")?.dataset.beadId : undefined;
  const actionStartedOnCard = focusedCardId === item.beadId;
  let focusAfterRender = null;
  let actionSucceeded = false;
  attentionActionBlocked.add(item.beadId);
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
  const expected = {
    stage: item.stage ?? null,
    pauseLabels: item.labels.filter(label => label === "needs-human" || label === "product-decision-required"),
    repairCycles: item.repairCycles ?? 0
  };
  if (action === "resume" || action === "note_only") {
    headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(action === "resume" ? { instruction, action, expected } : { instruction, action });
  } else if (action === "retry") {
    headers["Content-Type"] = "application/json";
    options.body = JSON.stringify({ expected });
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
      actionSucceeded = true;
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
    const attentionRefreshed = await loadAttention();
    if (attentionRefreshed) attentionActionBlocked.delete(item.beadId);
    await loadBeads();
    void loadDigest();
    if (action === "note_only")
      focusAfterRender = "guidance";
    else if (expectedFocusAfterAction(action, result))
      focusAfterRender = "status";
  } catch (error) {
    attentionMessages.set(item.beadId, error instanceof Error ? error.message : "The action could not be confirmed. Refresh before retrying.");
    if (attentionStatus) attentionStatus.textContent = `Action for ${item.beadId} needs attention.`;
    focusAfterRender = "message";
    const refreshResults = await Promise.allSettled([
      loadAttention(),
      loadBeads(),
      workspaceCurrentId === item.beadId ? loadWorkspace(item.beadId, { manual: true }) : Promise.resolve(false)
    ]);
    if (refreshResults[0]?.status === "fulfilled" && refreshResults[0].value === true)
      attentionActionBlocked.delete(item.beadId);
  } finally {
    const activeElement = document.activeElement;
    const activeCardId = activeElement instanceof HTMLElement
      ? activeElement.closest(".attention-card")?.dataset.beadId : undefined;
    const focusStillWithAction = activeCardId === item.beadId
      || (activeCardId === undefined && (activeElement === document.body || !activeElement?.isConnected));
    attentionActions.delete(item.beadId);
    renderAttention();
    if (actionStartedOnCard && focusStillWithAction) {
      if (focusAfterRender === "guidance") cardById(item.beadId)?.querySelector("textarea")?.focus();
      else if (focusAfterRender === "status") attentionStatus?.focus();
      else if (focusAfterRender === "message") {
        const message = cardById(item.beadId)?.querySelector(".attention-card-message");
        if (message) message.focus();
        else attentionStatus?.focus();
      }
    }
  }
  return actionSucceeded;
}

function expectedFocusAfterAction(action, result) {
  return action !== "note_only" && result?.resolved === true;
}

if (attentionCards) {
  attentionRefresh?.addEventListener("click", () => void loadAttention());
  attentionRetry?.addEventListener("click", () => void loadAttention());
  void loadAttention();
}

const digestSection = document.querySelector("#catch-up");
const digestCheckpointBadge = document.querySelector("#digest-checkpoint-badge");
const digestWindowButtons = Array.from(document.querySelectorAll(".digest-window"));
const digestMarkCaughtUp = document.querySelector("#digest-mark-caught-up");
const digestRefresh = document.querySelector("#digest-refresh");
const digestToggle = document.querySelector("#digest-toggle");
const digestStatus = document.querySelector("#digest-status");
const digestError = document.querySelector("#digest-error");
const digestErrorText = document.querySelector("#digest-error-text");
const digestRetry = document.querySelector("#digest-retry");
const digestCaughtUpBar = document.querySelector("#digest-caught-up-bar");
const digestCaughtUpText = document.querySelector("#digest-caught-up-text");
const digestView24h = document.querySelector("#digest-view-24h");
const digestBody = document.querySelector("#digest-body");
const digestEmpty = document.querySelector("#digest-empty");
const digestThreadsSection = document.querySelector("#digest-threads-section");
const digestThreads = document.querySelector("#digest-threads");
const digestStandaloneSection = document.querySelector("#digest-standalone-section");
const digestStandalone = document.querySelector("#digest-standalone");
const digestMetricCompleted = document.querySelector("#digest-metric-completed");
const digestMetricCompletedSub = document.querySelector("#digest-metric-completed-sub");
const digestMetricThreads = document.querySelector("#digest-metric-threads");
const digestMetricThreadsSub = document.querySelector("#digest-metric-threads-sub");
const digestMetricOpen = document.querySelector("#digest-metric-open");
const digestMetricOpenSub = document.querySelector("#digest-metric-open-sub");
const digestMetricWaiting = document.querySelector("#digest-metric-waiting");
const digestMetricWaitingSub = document.querySelector("#digest-metric-waiting-sub");

let selectedDigestWindow = "auto";
let currentDigest = null;
let digestRequestGeneration = 0;
let activeDigestRequest = null;
let digestManualCollapse = true;
let digestSubmittingCatchUp = false;
const DIGEST_INITIAL_THREADS = 5;
const DIGEST_INITIAL_THREAD_ITEMS = 4;
const DIGEST_INITIAL_STANDALONE = 6;
let digestAllThreads = false;
let digestAllStandalone = false;
const expandedDigestSummaries = new Set();
const expandedDigestThreads = new Set();

function digestNode(tagName, className, text) {
  const element = document.createElement(tagName);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}

function formatDigestRelativeTime(isoString) {
  if (!isoString) return "Unknown";
  const parsed = Date.parse(isoString);
  if (!Number.isFinite(parsed)) return "Unknown";
  const elapsed = Math.max(0, Date.now() - parsed);
  const minutes = Math.floor(elapsed / 60000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

function formatDigestWindowLabel(windowValue) {
  return ({
    since_caught_up: "Since Caught Up",
    "24h": "Last 24h",
    "7d": "Last 7d",
    "30d": "Last 30d",
    all: "All Time"
  })[windowValue] ?? windowValue;
}

function updateDigestCollapseState(expanded) {
  if (!digestBody || !digestToggle) return;
  digestSection.dataset.collapsed = String(!expanded);
  digestBody.hidden = !expanded;
  if (digestCaughtUpBar) digestCaughtUpBar.hidden = !expanded || !currentDigest?.isCaughtUp;
  digestToggle.setAttribute("aria-expanded", String(expanded));
  digestToggle.textContent = expanded ? "Collapse" : "Expand";
}

function updateDigestWindowControls(activeWindow) {
  for (const button of digestWindowButtons) {
    button.setAttribute("aria-pressed", String(button.dataset.digestWindow === activeWindow));
  }
}

function outcomePillClass(outcome) {
  if (outcome === "resolved") return "digest-pill digest-pill-resolved";
  if (outcome === "paused" || outcome === "aborted") return "digest-pill digest-pill-paused";
  if (outcome === "no_op") return "digest-pill digest-pill-noop";
  return "digest-pill";
}

function renderDigestItemRow(item, { isThreadRoot = false } = {}) {
  const row = digestNode("div", "digest-item-row");
  row.dataset.beadId = item.id;

  const meta = digestNode("div", "digest-item-meta");
  meta.append(digestNode("span", "digest-pill", `${item.type} · P${item.priority}`));

  if (item.activityKind === "context") {
    meta.append(digestNode("span", "digest-pill digest-pill-noop", `Root context (${item.status})`));
  } else {
    meta.append(digestNode("span", outcomePillClass(item.outcome), item.outcomeLabel));
  }

  if (!isThreadRoot && item.fromId) {
    const relationLabel = item.fromRelation === "parent-child" ? `child of ${item.fromId}` : `from ${item.fromId}`;
    meta.append(digestNode("span", "digest-pill digest-pill-lineage", `↳ ${relationLabel}`));
  }

  for (const sha of item.commits ?? []) {
    meta.append(digestNode("span", "digest-pill digest-pill-commit", sha));
  }
  for (const pr of item.pullRequests ?? []) {
    meta.append(digestNode("span", "digest-pill digest-pill-commit", pr));
  }

  const top = digestNode("div", "digest-item-top");
  const titleHeading = digestNode("h4", "digest-item-title");
  const titleLink = digestNode("a", "", `${item.id} · ${item.title}`);
  titleLink.href = `#bead/${encodeURIComponent(item.id)}`;
  titleLink.dataset.beadWorkspaceLink = "true";
  titleLink.dataset.digestFocus = `bead:${item.id}`;
  titleHeading.append(titleLink);

  const timeLabel = item.activityKind === "closed"
    ? `Closed ${formatDigestRelativeTime(item.activityAt)}`
    : item.activityKind === "opened"
      ? `Opened ${formatDigestRelativeTime(item.activityAt)}`
      : item.activityKind === "updated"
        ? `Updated ${formatDigestRelativeTime(item.activityAt)}`
        : `Context ${formatDigestRelativeTime(item.activityAt)}`;
  top.append(titleHeading, digestNode("span", "digest-item-time", timeLabel));

  row.append(meta, top);

  if (item.summary && (item.activityKind !== "context" || !isThreadRoot)) {
    const summaryKey = item.id;
    const isLong = item.summary.length > 140;
    const isExpanded = expandedDigestSummaries.has(summaryKey);
    const summary = digestNode("p", isLong && !isExpanded ? "digest-summary is-clamped" : "digest-summary", item.summary);
    row.append(summary);

    if (isLong) {
      const toggle = digestNode("button", "digest-inline-toggle", isExpanded ? "Show less" : "Show more");
      toggle.type = "button";
      toggle.dataset.digestFocus = `summary:${summaryKey}`;
      toggle.setAttribute("aria-expanded", String(isExpanded));
      summary.id = `digest-summary-${encodeURIComponent(summaryKey)}`;
      toggle.setAttribute("aria-controls", summary.id);
      toggle.addEventListener("click", () => {
        if (expandedDigestSummaries.has(summaryKey)) expandedDigestSummaries.delete(summaryKey);
        else expandedDigestSummaries.add(summaryKey);
        renderDigest();
      });
      row.append(toggle);
    }
  }

  return row;
}

function renderDigestThreadCard(thread) {
  const card = digestNode("article", "digest-thread-card");
  card.dataset.rootBeadId = thread.root.id;

  const header = digestNode("div", "digest-thread-header");
  const titleGroup = digestNode("div", "digest-thread-title-group");
  const meta = digestNode("div", "digest-thread-meta");

  meta.append(digestNode("span", "digest-pill", `${thread.root.type} · P${thread.root.priority}`));

  if (thread.root.activityKind === "context") {
    meta.append(digestNode("span", "digest-pill digest-pill-noop", `Origin (${thread.root.status})`));
  } else {
    meta.append(digestNode("span", outcomePillClass(thread.root.outcome), thread.root.outcomeLabel));
  }
  for (const sha of thread.root.commits ?? []) {
    meta.append(digestNode("span", "digest-pill digest-pill-commit", sha));
  }
  for (const pr of thread.root.pullRequests ?? []) {
    meta.append(digestNode("span", "digest-pill digest-pill-commit", pr));
  }

  const title = digestNode("h4", "digest-thread-title");
  const titleLink = digestNode("a", "", `${thread.root.id} · ${thread.root.title}`);
  titleLink.href = `#bead/${encodeURIComponent(thread.root.id)}`;
  titleLink.dataset.beadWorkspaceLink = "true";
  titleLink.dataset.digestFocus = `root:${thread.root.id}`;
  title.append(titleLink);
  titleGroup.append(meta, title);

  if (thread.root.activityKind !== "context" && thread.root.summary) {
    const summaryKey = thread.root.id;
    const isLong = thread.root.summary.length > 140;
    const isExpanded = expandedDigestSummaries.has(summaryKey);
    const rootSummary = digestNode("p", isLong && !isExpanded ? "digest-summary is-clamped" : "digest-summary", thread.root.summary);
    rootSummary.style.marginTop = "0.35rem";
    titleGroup.append(rootSummary);
    if (isLong) {
      const toggle = digestNode("button", "digest-inline-toggle", isExpanded ? "Show less" : "Show more");
      toggle.type = "button";
      toggle.dataset.digestFocus = `summary:${summaryKey}`;
      toggle.setAttribute("aria-expanded", String(isExpanded));
      rootSummary.id = `digest-summary-${encodeURIComponent(summaryKey)}`;
      toggle.setAttribute("aria-controls", rootSummary.id);
      toggle.style.marginTop = "0.25rem";
      toggle.addEventListener("click", () => {
        if (expandedDigestSummaries.has(summaryKey)) expandedDigestSummaries.delete(summaryKey);
        else expandedDigestSummaries.add(summaryKey);
        renderDigest();
      });
      titleGroup.append(toggle);
    }
  }

  const counts = digestNode("div", "digest-thread-counts");
  if (thread.completedCount > 0) {
    counts.append(digestNode("span", "digest-pill digest-pill-resolved", `${thread.completedCount} completed`));
  }
  if (thread.openCount > 0) {
    counts.append(digestNode("span", "digest-pill", `${thread.openCount} open/updated`));
  }
  header.append(titleGroup, counts);
  card.append(header);

  if (Array.isArray(thread.items) && thread.items.length > 0) {
    const itemsContainer = digestNode("div", "digest-thread-items");
    itemsContainer.id = `digest-thread-${encodeURIComponent(thread.root.id)}`;
    const isThreadExpanded = expandedDigestThreads.has(thread.root.id);
    const visibleItems = isThreadExpanded ? thread.items : thread.items.slice(0, DIGEST_INITIAL_THREAD_ITEMS);
    for (const item of visibleItems) {
      itemsContainer.append(renderDigestItemRow(item));
    }
    card.append(itemsContainer);

    if (thread.items.length > DIGEST_INITIAL_THREAD_ITEMS) {
      const hiddenCount = thread.items.length - DIGEST_INITIAL_THREAD_ITEMS;
      const moreBtn = digestNode("button", "digest-inline-toggle",
        isThreadExpanded ? "Show fewer in thread" : `Show ${hiddenCount} more in thread`);
      moreBtn.type = "button";
      moreBtn.dataset.digestFocus = `thread:${thread.root.id}`;
      moreBtn.setAttribute("aria-controls", itemsContainer.id);
      moreBtn.setAttribute("aria-expanded", String(isThreadExpanded));
      moreBtn.addEventListener("click", () => {
        if (expandedDigestThreads.has(thread.root.id)) expandedDigestThreads.delete(thread.root.id);
        else expandedDigestThreads.add(thread.root.id);
        renderDigest();
      });
      card.append(moreBtn);
    }
  }

  return card;
}

function digestCollectionToggle(container, total, limit, expanded, label, onToggle) {
  if (total <= limit) return;
  const button = digestNode("button", "digest-inline-toggle",
    expanded ? `Show fewer ${label}` : `Show ${total - limit} more ${label}`);
  button.type = "button";
  button.dataset.digestFocus = container.id;
  button.setAttribute("aria-controls", container.id);
  button.setAttribute("aria-expanded", String(expanded));
  button.addEventListener("click", () => { onToggle(); renderDigest(); });
  container.append(button);
}

function resetDigestCollectionBounds() {
  digestAllThreads = false;
  digestAllStandalone = false;
  expandedDigestThreads.clear();
}

function renderDigest() {
  if (!currentDigest || !digestSection) return;
  const focusKey = digestSection.contains(document.activeElement)
    ? document.activeElement.dataset.digestFocus : null;
  const d = currentDigest;
  updateDigestWindowControls(d.window);

  if (digestCheckpointBadge) {
    if (d.lastCaughtUpAt) {
      digestCheckpointBadge.title = new Date(d.lastCaughtUpAt).toLocaleString();
      digestCheckpointBadge.textContent = `Checkpoint · ${formatDigestRelativeTime(d.lastCaughtUpAt)}`;
    } else {
      const effectiveWindow = d.window === "since_caught_up" ? "24h" : d.window;
      digestCheckpointBadge.textContent = `No catch-up bookmark set · showing ${formatDigestWindowLabel(effectiveWindow)}`;
    }
  }

  if (digestMarkCaughtUp) {
    digestMarkCaughtUp.disabled = digestSubmittingCatchUp || Boolean(d.isCaughtUp);
    digestMarkCaughtUp.textContent = d.isCaughtUp ? "Caught Up" : "Mark Caught Up";
  }

  const m = d.metrics;
  if (digestMetricCompleted) digestMetricCompleted.textContent = String(m.completedTotal);
  if (digestMetricCompletedSub) {
    const parts = [];
    if (m.completedFeatures > 0) parts.push(`${m.completedFeatures} feature${m.completedFeatures === 1 ? "" : "s"}`);
    if (m.completedTasks > 0) parts.push(`${m.completedTasks} task${m.completedTasks === 1 ? "" : "s"}`);
    if (m.completedBugs > 0) parts.push(`${m.completedBugs} bug${m.completedBugs === 1 ? "" : "s"}`);
    digestMetricCompletedSub.textContent = parts.length > 0 ? parts.join(" · ") : `In ${formatDigestWindowLabel(d.window)}`;
  }
  if (digestMetricThreads) digestMetricThreads.textContent = String(m.activeThreads);
  if (digestMetricThreadsSub) {
    digestMetricThreadsSub.textContent = m.activeThreads === 1 ? "1 connected lineage chain" : `${m.activeThreads} connected lineage chains`;
  }
  if (digestMetricOpen) digestMetricOpen.textContent = String(m.openOrUpdatedTotal);
  if (digestMetricOpenSub) {
    digestMetricOpenSub.textContent = m.openOrUpdatedTotal === 0 ? "No newly opened items" : "Opened or updated in window";
  }
  if (digestMetricWaiting) digestMetricWaiting.textContent = String(m.waitingForGuidance);
  if (digestMetricWaitingSub) {
    digestMetricWaitingSub.textContent = m.waitingForGuidance === 0 ? "Attention Desk clear" : "Paused in Attention Desk";
  }

  if (digestCaughtUpBar && digestCaughtUpText) {
    digestCaughtUpBar.hidden = !d.isCaughtUp;
    if (d.isCaughtUp && d.lastCaughtUpAt) {
      digestCaughtUpText.textContent = `All caught up since ${new Date(d.lastCaughtUpAt).toLocaleString()} · 0 new changes since your bookmark.`;
    }
  }

  updateDigestCollapseState(!digestManualCollapse);

  const hasThreads = Array.isArray(d.threads) && d.threads.length > 0;
  const standaloneItems = [...(d.standaloneCompleted ?? []), ...(d.standaloneOpen ?? [])];
  const hasStandalone = standaloneItems.length > 0;

  if (digestEmpty) digestEmpty.hidden = hasThreads || hasStandalone;

  if (digestThreadsSection && digestThreads) {
    digestThreadsSection.hidden = !hasThreads;
    const threadFragment = document.createDocumentFragment();
    for (const thread of (digestAllThreads ? d.threads : d.threads?.slice(0, DIGEST_INITIAL_THREADS)) ?? []) {
      threadFragment.append(renderDigestThreadCard(thread));
    }
    digestThreads.replaceChildren(threadFragment);
    digestCollectionToggle(digestThreads, d.threads?.length ?? 0, DIGEST_INITIAL_THREADS,
      digestAllThreads, "threads", () => { digestAllThreads = !digestAllThreads; });
  }

  if (digestStandaloneSection && digestStandalone) {
    digestStandaloneSection.hidden = !hasStandalone;
    const standaloneFragment = document.createDocumentFragment();
    for (const item of digestAllStandalone ? standaloneItems : standaloneItems.slice(0, DIGEST_INITIAL_STANDALONE)) {
      standaloneFragment.append(renderDigestItemRow(item));
    }
    digestStandalone.replaceChildren(standaloneFragment);
    digestCollectionToggle(digestStandalone, standaloneItems.length, DIGEST_INITIAL_STANDALONE,
      digestAllStandalone, "standalone items", () => { digestAllStandalone = !digestAllStandalone; });
  }
  if (focusKey) {
    const replacement = Array.from(digestSection.querySelectorAll("[data-digest-focus]"))
      .find(element => element.dataset.digestFocus === focusKey);
    (replacement ?? digestToggle).focus({ preventScroll: true });
  }
}

async function loadDigest(windowOverride = undefined, expectedCheckpoint = undefined) {
  if (!digestSection || (digestSubmittingCatchUp && expectedCheckpoint === undefined)) return false;
  if (windowOverride !== undefined) selectedDigestWindow = windowOverride;
  activeDigestRequest?.abort();
  const generation = ++digestRequestGeneration;
  const controller = new AbortController();
  activeDigestRequest = controller;

  if (digestRefresh) digestRefresh.disabled = true;
  if (digestRetry) digestRetry.disabled = true;
  if (digestError) digestError.hidden = true;
  if (digestStatus) digestStatus.textContent = "Refreshing catch-up digest…";

  const params = new URLSearchParams();
  if (selectedDigestWindow && selectedDigestWindow !== "auto") {
    params.set("window", selectedDigestWindow);
  }
  const url = params.size > 0 ? `/api/digest?${params.toString()}` : "/api/digest";

  try {
    const response = await fetch(url, {
      cache: "no-store",
      headers: { Accept: "application/json" },
      signal: controller.signal
    });
    if (!response.ok) {
      let detail = "The Catch-Up Digest could not be refreshed.";
      try {
        const problem = await response.json();
        if (typeof problem?.detail === "string" && problem.detail.trim()) detail = problem.detail;
      } catch { /* Fallback to generic message */ }
      throw new Error(detail);
    }
    const payload = await response.json();
    if (generation !== digestRequestGeneration) return false;
    if (!payload || typeof payload !== "object" || !payload.metrics) {
      throw new Error("The Catch-Up Digest response was invalid.");
    }
    if (expectedCheckpoint !== undefined && Date.parse(payload.lastCaughtUpAt) !== Date.parse(expectedCheckpoint)) {
      throw new Error("The saved catch-up checkpoint could not be confirmed. Refresh to try again.");
    }
    const validThreads = new Set((payload.threads ?? []).map(thread => thread.root.id));
    for (const id of expandedDigestThreads) if (!validThreads.has(id)) expandedDigestThreads.delete(id);
    const validSummaries = new Set([
      ...(payload.threads ?? []).flatMap(thread => [thread.root.id, ...thread.items.map(item => item.id)]),
      ...[...(payload.standaloneCompleted ?? []), ...(payload.standaloneOpen ?? [])].map(item => item.id)
    ]);
    for (const id of expandedDigestSummaries) if (!validSummaries.has(id)) expandedDigestSummaries.delete(id);
    currentDigest = payload;
    selectedDigestWindow = payload.window;
    renderDigest();
    if (digestStatus) digestStatus.textContent = "";
    return true;
  } catch (error) {
    if (error?.name === "AbortError" || generation !== digestRequestGeneration) return false;
    if (digestError && digestErrorText) {
      digestErrorText.textContent = error instanceof Error ? error.message : "The Catch-Up Digest is unavailable.";
      digestError.hidden = false;
    }
    if (digestStatus) {
      digestStatus.textContent = currentDigest
        ? "Showing the last confirmed digest; current tracker state is unavailable."
        : "Catch-Up Digest data is unavailable.";
    }
    return false;
  } finally {
    if (generation === digestRequestGeneration) {
      activeDigestRequest = null;
      if (digestRefresh) digestRefresh.disabled = false;
      if (digestRetry) digestRetry.disabled = false;
    }
  }
}

async function submitMarkCaughtUp() {
  if (!currentDigest?.generatedAt || digestSubmittingCatchUp) return;
  const caughtUpAt = currentDigest.generatedAt;
  activeDigestRequest?.abort();
  ++digestRequestGeneration;
  digestSubmittingCatchUp = true;
  if (digestMarkCaughtUp) digestMarkCaughtUp.disabled = true;
  if (digestRefresh) digestRefresh.disabled = true;
  if (digestRetry) digestRetry.disabled = true;
  for (const button of digestWindowButtons) button.disabled = true;
  if (digestView24h) digestView24h.disabled = true;
  if (digestStatus) digestStatus.textContent = "Saving catch-up bookmark…";
  if (digestError) digestError.hidden = true;

  try {
    const response = await fetch("/api/digest/catch-up", {
      method: "POST",
      cache: "no-store",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        "X-Nightweaver-Request": "console-v1"
      },
      body: JSON.stringify({ caughtUpAt })
    });
    if (!response.ok) {
      let detail = "The catch-up bookmark could not be saved.";
      try {
        const problem = await response.json();
        if (typeof problem?.detail === "string" && problem.detail.trim()) detail = problem.detail;
      } catch { /* Fallback */ }
      throw new Error(detail);
    }
    const saved = await response.json();
    if (typeof saved?.lastCaughtUpAt !== "string" || Date.parse(saved.lastCaughtUpAt) !== Date.parse(caughtUpAt)) {
      throw new Error("The catch-up bookmark response could not be verified.");
    }
    digestManualCollapse = true;
    resetDigestCollectionBounds();
    selectedDigestWindow = "since_caught_up";
    await loadDigest("since_caught_up", caughtUpAt);
  } catch (error) {
    if (digestError && digestErrorText) {
      digestErrorText.textContent = error instanceof Error ? error.message : "The catch-up bookmark could not be saved.";
      digestError.hidden = false;
    }
    if (digestStatus) digestStatus.textContent = "";
  } finally {
    digestSubmittingCatchUp = false;
    if (digestRefresh) digestRefresh.disabled = false;
    if (digestRetry) digestRetry.disabled = false;
    for (const button of digestWindowButtons) button.disabled = false;
    if (digestView24h) digestView24h.disabled = false;
    renderDigest();
  }
}

if (digestSection) {
  for (const button of digestWindowButtons) {
    button.addEventListener("click", () => {
      if (digestSubmittingCatchUp) return;
      resetDigestCollectionBounds();
      const targetWindow = button.dataset.digestWindow ?? "24h";
      digestManualCollapse = false;
      updateDigestCollapseState(true);
      void loadDigest(targetWindow);
    });
  }
  digestMarkCaughtUp?.addEventListener("click", () => void submitMarkCaughtUp());
  digestRefresh?.addEventListener("click", () => void loadDigest());
  digestRetry?.addEventListener("click", () => void loadDigest());
  digestView24h?.addEventListener("click", () => {
    if (digestSubmittingCatchUp) return;
    resetDigestCollectionBounds();
    digestManualCollapse = false;
    updateDigestCollapseState(true);
    void loadDigest("24h");
  });
  digestToggle?.addEventListener("click", () => {
    const currentlyExpanded = digestToggle.getAttribute("aria-expanded") === "true";
    digestManualCollapse = currentlyExpanded;
    updateDigestCollapseState(!currentlyExpanded);
  });
  void loadDigest();
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

const EXPLORER_PAGE_SIZE = 15;
const beadsRange = document.querySelector("#beads-range");
const beadsPage = document.querySelector("#beads-page");
const previousPage = document.querySelector("#beads-prev-page");
const nextPage = document.querySelector("#beads-next-page");
let selectedView = "ready";
let explorerPage = 1;
let explorerResults = [];
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
      : empty ? `${beadsEmpty.textContent} ${beadsRange.textContent}. ${beadsPage.textContent}.`
        : noMatches ? `No beads match your search. ${beadsRange.textContent}`
          : `${beadsRange.textContent}. ${beadsPage.textContent}.`;
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

function beadLinkCell(value, className, beadId) {
  const cell = document.createElement("td");
  if (className) cell.className = className;
  const link = document.createElement("a");
  link.href = `#bead/${encodeURIComponent(beadId)}`;
  link.dataset.beadWorkspaceLink = "true";
  link.textContent = value;
  cell.append(link);
  return cell;
}

function restoreExplorerPageFocus(control) {
  const target = !control.disabled ? control
    : [previousPage, nextPage].find(button => !button.disabled) ?? beadsRange;
  target.focus({ preventScroll: true });
}

function renderBeads(beads) {
  const focusedControl = [previousPage, nextPage].includes(document.activeElement) ? document.activeElement : null;
  const pages = Math.max(1, Math.ceil(beads.length / EXPLORER_PAGE_SIZE));
  explorerPage = Math.max(1, Math.min(explorerPage, pages));
  const start = (explorerPage - 1) * EXPLORER_PAGE_SIZE;
  beadsRange.textContent = `Showing ${beads.length ? start + 1 : 0}–${Math.min(start + EXPLORER_PAGE_SIZE, beads.length)} of ${beads.length} beads`;
  beadsPage.textContent = `Page ${explorerPage} of ${pages}`;
  previousPage.disabled = explorerPage === 1;
  nextPage.disabled = explorerPage === pages;
  const rows = document.createDocumentFragment();
  for (const bead of beads.slice(start, start + EXPLORER_PAGE_SIZE)) {
    const row = document.createElement("tr");
    row.append(
      beadLinkCell(bead.id, "bead-id", bead.id),
      beadLinkCell(bead.title, "bead-title", bead.id),
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
  if (focusedControl) restoreExplorerPageFocus(focusedControl);
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
  const focusedPageControl = [previousPage, nextPage].includes(document.activeElement) ? document.activeElement : null;
  explorerResults = [];
  beadsBody.replaceChildren();
  previousPage.disabled = true;
  nextPage.disabled = true;
  beadsRange.textContent = "Loading results…";
  beadsPage.textContent = "";
  setExplorerMessage({ loading: true });
  // Keep focus visible during the request, with a stable fallback if both directions disappear.
  if (focusedPageControl) beadsRange.focus({ preventScroll: true });

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
    explorerResults = beads;
    if (pendingCreatedCheck && selectedView === "ready" && query.length === 0) {
      const index = beads.findIndex(bead => bead.id === pendingCreatedCheck);
      const found = index >= 0;
      if (found) explorerPage = Math.floor(index / EXPLORER_PAGE_SIZE) + 1;
      setCreatedMessage(found
        ? `Created ${pendingCreatedCheck}. It is in the Ready backlog.`
        : `Created ${pendingCreatedCheck}. Its state may have changed since creation.`, !found);
      pendingCreatedCheck = null;
    }
    renderBeads(beads);
    if (beads.length === 0) {
      beadsEmpty.textContent = selectedView === "all" ? "No beads are in the tracker yet." : `No beads are in the ${selectedView} view.`;
      setExplorerMessage({ empty: query.length === 0, noMatches: query.length > 0 });
    } else {
      setExplorerMessage({});
    }
  } catch (error) {
    if (error?.name === "AbortError" || generation !== requestGeneration) return;
    beadsBody.replaceChildren();
    beadsRange.textContent = "Results unavailable";
    beadsPage.textContent = "";
    setExplorerMessage({ error: true });
    if (pendingCreatedCheck) {
      setCreatedMessage(`Created ${pendingCreatedCheck}. The Ready list could not be refreshed; use Retry to check its current state.`);
    }
  } finally {
    if (generation === requestGeneration) {
      activeRequest = null;
      // A user who moved elsewhere during the request keeps their chosen focus.
      if (focusedPageControl && document.activeElement === beadsRange) {
        restoreExplorerPageFocus(focusedPageControl);
      }
    }
  }
}

function queueSearch() {
  explorerPage = 1;
  if (searchTimer !== null) window.clearTimeout(searchTimer);
  activeRequest?.abort();
  requestGeneration += 1;
  beadsBody.replaceChildren();
  explorerResults = [];
  previousPage.disabled = true;
  nextPage.disabled = true;
  beadsRange.textContent = "Loading results…";
  beadsPage.textContent = "";
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
      explorerPage = 1;
      updateFilterControls();
      void loadBeads();
    });
  }
  retryButton?.addEventListener("click", () => void loadBeads());
  for (const [button, direction] of [[previousPage, -1], [nextPage, 1]]) {
    button?.addEventListener("click", () => {
      explorerPage += direction;
      renderBeads(explorerResults);
      setExplorerMessage({});
    });
  }
  showCreatedButton?.addEventListener("click", () => {
    if (!createdBeadId) return;
    selectedView = "all";
    explorerPage = 1;
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
          explorerPage = 1;
          beadsSearch.value = "";
          updateFilterControls();
          void loadBeads();
        }
        void loadDigest();
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

const consoleDeck = document.querySelector("#console-deck");
const beadWorkspace = document.querySelector("#bead-workspace");
const workspaceStatus = document.querySelector("#workspace-status");
const workspaceError = document.querySelector("#workspace-error");
const workspaceStale = document.querySelector("#workspace-stale");
const workspaceId = document.querySelector("#workspace-id");
const workspaceTitle = document.querySelector("#workspace-title");
const workspaceMeta = document.querySelector("#workspace-meta");
const workspacePlanning = document.querySelector("#workspace-planning");
const workspaceSpecs = document.querySelector("#workspace-specs");
const workspaceHistory = document.querySelector("#workspace-history");
const workspaceEnvironment = document.querySelector("#workspace-environment");
const workspaceRelations = document.querySelector("#workspace-relations");
const workspaceGuidance = document.querySelector("#workspace-guidance");
const workspaceGuidanceInput = document.querySelector("#workspace-guidance-input");
const workspaceGuidanceContext = document.querySelector("#workspace-guidance-context");
const workspaceGuidanceActions = document.querySelector("#workspace-guidance-actions");
const workspaceActionMessage = document.querySelector("#workspace-action-message");
const workspaceRefresh = document.querySelector("#workspace-refresh");
const workspaceExpandHistory = document.querySelector("#workspace-expand-history");
const workspaceCollapseHistory = document.querySelector("#workspace-collapse-history");
const workspaceOlderExpanded = new Map();
let workspaceCommentElements = new Map();
let workspaceCurrentId = null;
let workspaceData = null;
let workspaceRequest = null;
let workspaceGeneration = 0;
let workspaceOriginLink = null;
let workspaceDeckScroll = 0;
let workspaceIsStale = false;
let workspaceActionBlocked = false;
let workspaceAbortConfirmation = null;
let markdownEngine = null;

function workspaceText(tagName, className, value) {
  const element = document.createElement(tagName);
  if (className) element.className = className;
  element.textContent = value ?? "";
  return element;
}

function workspaceButton(label, className, handler, disabled = false) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = className;
  button.textContent = label;
  button.disabled = disabled;
  button.addEventListener("click", handler);
  return button;
}

function safeWorkspaceId(value) {
  return typeof value === "string" && /^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(value);
}

function markdownSafeFragment(source) {
  if (typeof window.markdownit !== "function" || !window.DOMPurify) {
    throw new Error("The local Markdown renderer is unavailable.");
  }
  if (!markdownEngine) {
    markdownEngine = window.markdownit({ html: false, linkify: false, typographer: false });
    markdownEngine.renderer.rules.image = (tokens, index) =>
      markdownEngine.utils.escapeHtml(tokens[index].content || tokens[index].attrGet("alt") || "[image]");
  }
  const rendered = markdownEngine.render(source);
  const fragment = window.DOMPurify.sanitize(rendered, {
    RETURN_DOM_FRAGMENT: true,
    ALLOWED_TAGS: ["a", "blockquote", "br", "code", "del", "em", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "li", "ol", "p", "pre", "strong", "table", "tbody", "td", "th", "thead", "tr", "ul"],
    ALLOWED_ATTR: ["href", "title"]
  });
  for (const anchor of fragment.querySelectorAll("a")) {
    const href = anchor.getAttribute("href") ?? "";
    if (href.startsWith("#")) {
      const targetId = href.slice(1);
      const target = /^workspace-comment-[0-9]+$/.test(targetId)
        ? document.getElementById(targetId) : null;
      if (!target || !beadWorkspace?.contains(target)) {
        unwrapElement(anchor);
        continue;
      }
      anchor.dataset.workspaceAnchor = "true";
      continue;
    }
    if (!/^https?:\/\//i.test(href)) {
      unwrapElement(anchor);
      continue;
    }
    let url;
    try { url = new URL(href); }
    catch {
      unwrapElement(anchor);
      continue;
    }
    if ((url.protocol !== "http:" && url.protocol !== "https:")
      || !url.hostname || url.username || url.password) {
      unwrapElement(anchor);
      continue;
    }
    anchor.setAttribute("target", "_blank");
    anchor.setAttribute("rel", "noopener noreferrer");
  }
  return fragment;
}

function unwrapElement(element) {
  const contents = document.createDocumentFragment();
  while (element.firstChild) contents.append(element.firstChild);
  element.replaceWith(contents);
}

function renderWorkspaceMarkdown(container, source) {
  try {
    container.replaceChildren(markdownSafeFragment(source));
    return true;
  } catch {
    const fallback = document.createElement("pre");
    fallback.className = "workspace-markdown-fallback";
    fallback.textContent = source;
    const note = workspaceText("p", "field-help", "Formatted Markdown is unavailable; the exact source is shown.");
    container.replaceChildren(fallback, note);
    return false;
  }
}

function renderWorkspaceSpecifications(bead) {
  const sections = [
    ["Description", bead.description],
    ["Design", bead.design],
    ["Acceptance criteria", bead.acceptanceCriteria],
    ["Notes", bead.notes]
  ].filter(([, source]) => typeof source === "string" && source.length > 0);
  workspaceSpecs.replaceChildren();
  if (sections.length === 0) {
    workspaceSpecs.append(workspaceText("p", "workspace-empty", "No specification text is recorded for this Bead."));
    return;
  }

  for (const [label, source] of sections) {
    const section = document.createElement("article");
    section.className = "workspace-spec-section";
    const heading = document.createElement("div");
    heading.className = "workspace-spec-heading";
    heading.append(workspaceText("h3", "", label));
    const content = document.createElement("div");
    content.className = "workspace-markdown";
    renderWorkspaceMarkdown(content, source);
    let sourceVisible = false;
    const toggle = workspaceButton("View source", "secondary-action workspace-source-toggle", () => {
      sourceVisible = !sourceVisible;
      toggle.textContent = sourceVisible ? "View formatted" : "View source";
      if (sourceVisible) {
        const pre = document.createElement("pre");
        pre.className = "workspace-source-text";
        pre.textContent = source;
        content.replaceChildren(pre);
      } else {
        renderWorkspaceMarkdown(content, source);
      }
    });
    heading.append(toggle);
    section.append(heading, content);
    workspaceSpecs.append(section);
  }
}

function formatWorkspaceDate(value) {
  if (typeof value !== "string") return "Unknown";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "Unknown" : date.toLocaleString();
}

function renderWorkspaceMeta(bead) {
  workspaceMeta.replaceChildren();
  const values = [
    `${bead.type} · P${bead.priority}`,
    bead.status,
    bead.stage || "No stage label",
    bead.assignee || "Unassigned",
    `Created ${formatWorkspaceDate(bead.createdAt)}`,
    `Updated ${formatWorkspaceDate(bead.updatedAt)}`
  ];
  if (bead.closedAt) values.push(`Closed ${formatWorkspaceDate(bead.closedAt)}`);
  for (const value of values) {
    const chip = workspaceText("span", "workspace-meta-chip", value);
    workspaceMeta.append(chip);
  }
}

function extractHumanInstruction(comment) {
  if (comment.kind !== "human-guidance") return null;
  try {
    const value = JSON.parse(comment.body);
    return value?.schema === "nightweaver-human-guidance-v1" && typeof value.instruction === "string"
      ? value.instruction : null;
  } catch {
    return null;
  }
}

function renderWorkspaceComment(comment, index, activeCommentId) {
  const article = document.createElement("article");
  article.className = `workspace-comment${comment.id === activeCommentId ? " workspace-comment-active" : ""}`;
  article.id = `workspace-comment-${index}`;
  article.tabIndex = -1;
  article.dataset.commentKind = comment.kind;
  const heading = document.createElement("div");
  heading.className = "workspace-comment-heading";
  const author = workspaceText("strong", "workspace-comment-author", comment.author || "Unknown author");
  const time = document.createElement("time");
  time.className = "workspace-comment-time";
  time.dateTime = comment.createdAt;
  time.textContent = formatWorkspaceDate(comment.createdAt);
  const kind = workspaceText("span", "workspace-comment-kind", comment.kind || "ordinary");
  heading.append(author, time, kind);
  if (comment.role || comment.stage) {
    const context = [comment.role, comment.stage].filter(Boolean).join(" · ");
    heading.append(workspaceText("span", "workspace-comment-context", context));
  }
  const instruction = extractHumanInstruction(comment);
  if (instruction !== null) article.append(workspaceText("p", "workspace-comment-guidance", instruction));
  if (instruction !== null) {
    const raw = document.createElement("details");
    raw.className = "workspace-comment-raw";
    const summary = document.createElement("summary");
    summary.textContent = "View exact stored comment";
    const body = document.createElement("pre");
    body.textContent = comment.body;
    raw.append(summary, body);
    article.append(heading, raw);
  } else {
    const body = document.createElement("pre");
    body.className = "workspace-comment-body";
    body.textContent = comment.body;
    article.append(heading, body);
  }
  return article;
}

function renderWorkspaceHistory(data) {
  workspaceHistory.replaceChildren();
  workspaceCommentElements = new Map();
  const commentsById = new Map(data.comments.map((comment, index) => [comment.id, { comment, index }]));
  const older = data.history.olderCommentIds.map(id => commentsById.get(id)).filter(Boolean);
  const recent = data.history.recentCommentIds.map(id => commentsById.get(id)).filter(Boolean);
  if (older.length + recent.length === 0) {
    workspaceHistory.append(workspaceText("p", "workspace-empty", "No comments are recorded for this Bead."));
    workspaceExpandHistory.hidden = true;
    workspaceCollapseHistory.hidden = true;
    return;
  }

  const renderRows = rows => {
    const fragment = document.createDocumentFragment();
    for (const { comment, index } of rows) {
      const element = renderWorkspaceComment(comment, index, data.history.activeCommentId);
      workspaceCommentElements.set(comment.id, element);
      fragment.append(element);
    }
    return fragment;
  };

  if (older.length > 0) {
    const details = document.createElement("details");
    details.className = "workspace-older-history";
    details.open = workspaceOlderExpanded.get(data.bead.id) ?? false;
    const summary = document.createElement("summary");
    summary.textContent = `${older.length} older comment${older.length === 1 ? "" : "s"}`;
    details.append(summary, renderRows(older));
    details.addEventListener("toggle", () => workspaceOlderExpanded.set(data.bead.id, details.open));
    workspaceHistory.append(details);
    workspaceExpandHistory.hidden = false;
    workspaceCollapseHistory.hidden = false;
  } else {
    workspaceExpandHistory.hidden = true;
    workspaceCollapseHistory.hidden = true;
  }
  workspaceHistory.append(renderRows(recent));
}

function addEnvironmentPair(fragment, label, value, className = "") {
  const pair = document.createElement("div");
  pair.className = "workspace-environment-pair";
  pair.append(workspaceText("dt", "", label), workspaceText("dd", className, value));
  fragment.append(pair);
}

function renderWorkspaceEnvironment(environment) {
  const fragment = document.createDocumentFragment();
  addEnvironmentPair(fragment, "State", environment.state || "unavailable");
  addEnvironmentPair(fragment, "Worktree", environment.worktreePath || "No verified local checkout", "workspace-path");
  addEnvironmentPair(fragment, "Branch", environment.branch || "Unknown");
  addEnvironmentPair(fragment, "HEAD", environment.head || "Unknown");
  addEnvironmentPair(fragment, "Checkout", environment.detached === true ? "Detached HEAD" : environment.detached === false ? "Attached" : "Unknown");
  addEnvironmentPair(fragment, "Git status", environment.gitStatus || "unknown");
  addEnvironmentPair(fragment, "Agent process PID", Number.isInteger(environment.processId) ? String(environment.processId) : "Unavailable");
  addEnvironmentPair(fragment, "Process state", environment.processState || "unavailable");
  addEnvironmentPair(fragment, "Observed", formatWorkspaceDate(environment.observedAt));
  if (environment.runtimeObservedAt || environment.runtimeSource)
    addEnvironmentPair(fragment, "Runtime source", `${environment.runtimeSource || "Unknown"} · ${formatWorkspaceDate(environment.runtimeObservedAt)}`);
  if (environment.warning) {
    const warning = workspaceText("p", "workspace-environment-warning", environment.warning);
    fragment.append(warning);
  }
  workspaceEnvironment.replaceChildren(fragment);
}

function renderWorkspaceRelations(relations) {
  workspaceRelations.replaceChildren();
  if (!Array.isArray(relations) || relations.length === 0) {
    workspaceRelations.append(workspaceText("p", "workspace-empty", "No tracker relations are recorded."));
    return;
  }
  const list = document.createElement("ul");
  list.className = "workspace-relation-list";
  for (const relation of relations) {
    const item = document.createElement("li");
    const description = `${relation.direction || "related"} · ${relation.type || "relation"}`;
    item.append(workspaceText("span", "workspace-relation-type", description));
    if (safeWorkspaceId(relation.id)) {
      const link = document.createElement("a");
      link.href = `#bead/${encodeURIComponent(relation.id)}`;
      link.dataset.beadWorkspaceLink = "true";
      link.textContent = `${relation.id}${relation.title ? ` · ${relation.title}` : ""}`;
      item.append(link);
    } else {
      item.append(workspaceText("span", "workspace-relation-literal", relation.id || "Unknown ID"));
    }
    if (relation.status) item.append(workspaceText("span", "workspace-relation-status", relation.status));
    list.append(item);
  }
  workspaceRelations.append(list);
}

function renderWorkspaceActiveContext(data) {
  workspaceGuidanceContext.replaceChildren();
  const attention = data.attention;
  if (!attention) {
    const paused = data.bead.labels.some(label => label === "product-decision-required" || label === "needs-human");
    workspaceGuidanceContext.append(workspaceText("p", paused ? "workspace-context-warning" : "workspace-context-muted",
      paused ? "A pause label is present, but no current question or diagnostic could be attributed to a comment. Review the history before acting."
        : "No active human pause is available for this Bead."));
    return;
  }
  const isDecision = attention.kind === "product-decision-required";
  workspaceGuidanceContext.append(workspaceText("p", "workspace-context-label",
    isDecision ? "Current clarification context" : "Current diagnostic"));
  const text = isDecision ? attention.question : attention.diagnostic;
  const context = document.createElement("pre");
  context.className = "workspace-active-context";
  context.textContent = text || (isDecision ? "No attributable question is available." : "No valid diagnostic is available.");
  workspaceGuidanceContext.append(context);
  const active = data.history.activeCommentId ? workspaceCommentElements.get(data.history.activeCommentId) : null;
  if (active) {
    const jump = workspaceButton(isDecision ? "See current question in history" : "See diagnostic in history",
      "workspace-active-jump", () => {
        active.scrollIntoView({ block: "center", behavior: "smooth" });
        active.focus({ preventScroll: true });
      });
    workspaceGuidanceContext.append(jump);
  }
}

function renderWorkspaceGuidanceActions(data) {
  const attention = data.attention;
  const beadId = data.bead.id;
  const busy = attentionActions.has(beadId);
  const blocked = workspaceIsStale || workspaceActionBlocked || busy;
  const decision = attention?.kind === "product-decision-required";
  const needsHuman = attention?.kind === "needs-human";
  const guidanceLabel = document.querySelector('label[for="workspace-guidance-input"]');
  const guidanceHelp = document.querySelector("#workspace-guidance-help");
  const templateChips = document.querySelector(".workspace-template-chips");
  workspaceGuidanceInput.hidden = needsHuman;
  guidanceLabel.hidden = needsHuman;
  guidanceHelp.hidden = needsHuman;
  templateChips.hidden = needsHuman;
  workspaceGuidanceInput.disabled = blocked || !decision || !attention.canDecide;
  workspaceGuidanceActions.replaceChildren();

  workspacePlanning.replaceChildren();
  workspacePlanning.hidden = !data.planning;
  if (data.planning) {
    const terminal = data.bead.status === "closed" || data.bead.labels.includes("planned");
    const summary = document.createElement("div");
    summary.append(workspaceText("p", "eyebrow", "Planning"), workspaceText("p", "",
      terminal ? "Planning authorization is no longer available for this Product."
        : data.planning.ready ? "Ready for Planning. Scheduling and pause rules still apply."
          : data.planning.canAuthorize ? "Planning backlog: awaiting explicit authorization."
            : "Planning authorization requires an open, unclaimed, unpaused Product."));
    workspacePlanning.append(summary);
    if (!terminal && !data.planning.ready) workspacePlanning.append(workspaceButton("Ready for Planning", "primary-action",
      () => void authorizeWorkspacePlanning(data), blocked || !data.planning.canAuthorize));
    if (!attention && attentionMessages.has(beadId)) {
      const message = workspaceText("p", "field-help workspace-planning-message", attentionMessages.get(beadId));
      message.setAttribute("role", "status");
      workspacePlanning.append(message);
    }
  }
  if (decision) {
    workspaceGuidanceActions.append(
      workspaceButton("Save Note", "secondary-action", () => void submitWorkspaceAction("note_only"), blocked || !attention.canDecide),
      workspaceButton("Send & Resume", "primary-action", () => void submitWorkspaceAction("resume"), blocked || !attention.canDecide)
    );
  } else if (needsHuman) {
    workspaceGuidanceActions.append(workspaceButton("Reset Budget & Retry", "primary-action",
      () => void submitWorkspaceAction("retry"), blocked || !attention.canRetry));
    const abortTrigger = workspaceButton("Abort & Close", "secondary-action", () => {
      workspaceAbortConfirmation = beadId;
      renderWorkspaceGuidanceActions(data);
      workspaceGuidanceActions.querySelector(".workspace-abort-cancel")?.focus();
    }, blocked || !attention.canAbort);
    abortTrigger.classList.add("workspace-abort-trigger");
    workspaceGuidanceActions.append(abortTrigger);
    if (workspaceAbortConfirmation === beadId) {
      const confirmation = document.createElement("div");
      confirmation.className = "workspace-abort-confirmation";
      confirmation.append(workspaceText("p", "", `Close ${beadId} as an abort? This keeps the complete history and does not delete a remote branch or pull request.`));
      confirmation.append(
        workspaceButton("Cancel", "secondary-action workspace-abort-cancel", () => {
          workspaceAbortConfirmation = null;
          renderWorkspaceGuidanceActions(data);
          workspaceGuidanceActions.querySelector(".workspace-abort-trigger")?.focus();
        }),
        workspaceButton("Confirm Abort & Close", "danger-action", () => {
          workspaceAbortConfirmation = null;
          void submitWorkspaceAction("abort");
        })
      );
      workspaceGuidanceActions.append(confirmation);
    }
  } else {
    workspaceGuidanceActions.append(workspaceText("p", "workspace-action-unavailable",
      attention?.unavailableReason || "Actions are available only for a valid, unclaimed human pause."));
  }

  const message = data.planning && !attention ? null : attentionMessages.get(beadId);
  if (message) {
    workspaceActionMessage.textContent = message;
    workspaceActionMessage.hidden = false;
  } else {
    workspaceActionMessage.textContent = "";
    workspaceActionMessage.hidden = true;
  }
}

function renderWorkspace(data) {
  workspaceId.textContent = data.bead.id;
  workspaceTitle.textContent = data.bead.title;
  renderWorkspaceMeta(data.bead);
  renderWorkspaceHistory(data);
  renderWorkspaceSpecifications(data.bead);
  renderWorkspaceEnvironment(data.environment);
  renderWorkspaceRelations(data.relations);
  workspaceGuidanceInput.value = attentionDrafts.get(data.bead.id) ?? "";
  renderWorkspaceActiveContext(data);
  renderWorkspaceGuidanceActions(data);
}

function showWorkspaceLoading(id, message = "Loading Bead workspace…") {
  workspaceId.textContent = id;
  workspaceTitle.textContent = "Loading workspace…";
  workspaceMeta.replaceChildren();
  workspacePlanning.hidden = true;
  workspacePlanning.replaceChildren();
  workspaceStatus.textContent = message;
  workspaceError.hidden = true;
  workspaceStale.hidden = true;
  workspaceSpecs.replaceChildren();
  workspaceHistory.replaceChildren();
  workspaceEnvironment.replaceChildren();
  workspaceRelations.replaceChildren();
  workspaceGuidanceContext.replaceChildren();
  workspaceGuidanceActions.replaceChildren();
  workspaceActionMessage.hidden = true;
  workspaceGuidanceInput.value = attentionDrafts.get(id) ?? "";
  workspaceGuidanceInput.disabled = true;
}

async function loadWorkspace(id, { manual = false } = {}) {
  if (!beadWorkspace || id !== workspaceCurrentId) return false;
  const previous = workspaceData?.bead.id === id ? workspaceData : null;
  workspaceRequest?.abort();
  const generation = ++workspaceGeneration;
  const controller = new AbortController();
  workspaceRequest = controller;
  workspaceIsStale = false;
  workspaceStale.hidden = true;
  workspaceError.hidden = true;
  if (workspaceRefresh) workspaceRefresh.disabled = true;
  if (previous) workspaceStatus.textContent = "Refreshing Bead workspace…";
  else showWorkspaceLoading(id);

  if (!safeWorkspaceId(id)) {
    workspaceError.textContent = "This Bead ID contains unsupported characters.";
    workspaceError.hidden = false;
    workspaceStatus.textContent = "Invalid Bead ID.";
    if (workspaceRefresh) workspaceRefresh.disabled = false;
    return false;
  }

  try {
    const response = await fetch(`/api/beads/${encodeURIComponent(id)}`, {
      cache: "no-store",
      headers: { Accept: "application/json" },
      signal: controller.signal
    });
    if (!response.ok) {
      let detail = response.status === 404 ? "No exact Bead ID was found in the configured tracker."
        : response.status === 409 ? "The Bead changed while the workspace was being read. Refresh to try again."
          : response.status === 400 ? "This Bead ID is invalid."
            : "The Bead workspace is unavailable. Refresh to try again.";
      try {
        const problem = await response.json();
        if (typeof problem.detail === "string" && problem.detail.trim()) detail = problem.detail;
      } catch { /* Retain a safe local status message. */ }
      throw Object.assign(new Error(detail), { status: response.status });
    }
    const data = await response.json();
    if (generation !== workspaceGeneration || id !== workspaceCurrentId) return false;
    if (!data?.bead || data.bead.id !== id || !Array.isArray(data.comments) || !data.history) {
      throw new Error("The Bead workspace response was incomplete.");
    }
    workspaceData = data;
    workspaceIsStale = false;
    workspaceActionBlocked = false;
    workspaceAbortConfirmation = null;
    renderWorkspace(data);
    workspaceStatus.textContent = `Workspace refreshed for ${id}.`;
    if (!manual) workspaceTitle.focus({ preventScroll: true });
    return true;
  } catch (error) {
    if (error?.name === "AbortError" || generation !== workspaceGeneration || id !== workspaceCurrentId) return false;
    workspaceError.textContent = error instanceof Error ? error.message : "The Bead workspace is unavailable.";
    workspaceError.hidden = false;
    if (previous) {
      workspaceData = previous;
      workspaceIsStale = true;
      workspaceStale.hidden = false;
      workspaceStatus.textContent = "Showing a stale snapshot. Actions are disabled until refresh succeeds.";
      renderWorkspace(previous);
      workspaceGuidanceActions.querySelectorAll("button").forEach(button => { button.disabled = true; });
      workspaceGuidanceInput.disabled = true;
    } else {
      workspaceStatus.textContent = "Workspace data is unavailable.";
    }
    return false;
  } finally {
    if (generation === workspaceGeneration) {
      workspaceRequest = null;
      if (workspaceRefresh) workspaceRefresh.disabled = false;
    }
  }
}

async function authorizeWorkspacePlanning(data) {
  if (workspaceIsStale || workspaceActionBlocked || workspaceCurrentId !== data.bead.id || !data.planning?.canAuthorize) return;
  workspaceActionBlocked = true;
  renderWorkspaceGuidanceActions(data);
  try {
    const response = await fetch(`/api/beads/${encodeURIComponent(data.bead.id)}/ready-for-planning`, {
      method: "POST", headers: { "Content-Type": "application/json", "X-Nightweaver-Request": "console-v1" },
      body: JSON.stringify({ expected: data.planning.expected })
    });
    const result = await response.json();
    if (!response.ok || result.beadId !== data.bead.id || result.stateChanged !== true)
      throw new Error(result.detail || "Planning authorization could not be confirmed. Refresh before retrying.");
    attentionMessages.set(data.bead.id, "Planning readiness saved. Normal scheduling rules still apply.");
    await loadBeads();
  } catch (error) {
    attentionMessages.set(data.bead.id, error instanceof Error ? error.message : "Refresh before retrying.");
    if (workspaceCurrentId === data.bead.id) renderWorkspaceGuidanceActions(workspaceData);
    return;
  }
  if (workspaceCurrentId === data.bead.id) await loadWorkspace(data.bead.id, { manual: true });
}

async function submitWorkspaceAction(action) {
  const data = workspaceData;
  if (!data || data.bead.id !== workspaceCurrentId || !data.attention
    || workspaceIsStale || workspaceActionBlocked || attentionActions.has(data.bead.id)) return;
  const beadId = data.bead.id;
  const item = data.attention;
  let instruction;
  if (action === "resume" || action === "note_only") {
    instruction = workspaceGuidanceInput.value;
    attentionDrafts.set(beadId, instruction);
  }
  workspaceGuidanceActions.setAttribute("aria-busy", "true");
  workspaceGuidanceActions.querySelectorAll("button").forEach(button => { button.disabled = true; });
  workspaceGuidanceInput.disabled = true;
  const accepted = await submitAttentionAction(item, action, instruction, { workspaceReadConfirmed: true });
  if (workspaceCurrentId !== beadId) return;
  workspaceGuidanceActions.removeAttribute("aria-busy");
  if (!accepted) {
    workspaceActionBlocked = true;
    renderWorkspaceGuidanceActions(workspaceData);
    workspaceGuidanceActions.querySelectorAll("button").forEach(button => { button.disabled = true; });
    workspaceGuidanceInput.disabled = true;
    workspaceActionMessage.textContent = attentionMessages.get(beadId) || "Refresh before retrying this action.";
    workspaceActionMessage.hidden = false;
    return;
  }
  await loadWorkspace(beadId, { manual: true });
}

function routeToWorkspace(hashChange = false) {
  if (!beadWorkspace || !consoleDeck) return;
  const hash = window.location.hash;
  if (!hash.startsWith("#bead/")) {
    if (workspaceCurrentId !== null) {
      workspaceRequest?.abort();
      workspaceRequest = null;
      workspaceGeneration += 1;
      workspaceCurrentId = null;
      workspaceData = null;
      workspaceIsStale = false;
      workspaceActionBlocked = false;
      workspaceAbortConfirmation = null;
      beadWorkspace.hidden = true;
      consoleDeck.hidden = false;
      if (hash === "#main-content") window.scrollTo(0, workspaceDeckScroll);
      if (workspaceOriginLink?.isConnected) {
        const origin = workspaceOriginLink;
        workspaceOriginLink = null;
        requestAnimationFrame(() => origin.focus({ preventScroll: true }));
      }
    }
    return;
  }

  let id;
  try { id = decodeURIComponent(hash.slice("#bead/".length)); }
  catch {
    id = "";
  }
  if (workspaceCurrentId === id && !beadWorkspace.hidden) return;
  if (workspaceCurrentId === null) workspaceDeckScroll = window.scrollY;
  workspaceCurrentId = id;
  workspaceData = null;
  workspaceIsStale = false;
  workspaceActionBlocked = false;
  workspaceAbortConfirmation = null;
  workspaceRequest?.abort();
  workspaceGeneration += 1;
  consoleDeck.hidden = true;
  beadWorkspace.hidden = false;
  showWorkspaceLoading(id, hashChange ? "Loading Bead workspace…" : "Loading Bead workspace…");
  void loadWorkspace(id);
}

document.addEventListener("click", event => {
  const link = event.target instanceof Element ? event.target.closest("a[data-bead-workspace-link]") : null;
  if (link instanceof HTMLAnchorElement && !event.defaultPrevented
    && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey && event.button === 0) {
    workspaceOriginLink = link;
  }
});

beadWorkspace?.addEventListener("click", event => {
  const anchor = event.target instanceof Element ? event.target.closest('a[data-workspace-anchor="true"]') : null;
  if (!(anchor instanceof HTMLAnchorElement)) return;
  event.preventDefault();
  const target = document.getElementById(decodeURIComponent(anchor.getAttribute("href").slice(1)));
  if (target && beadWorkspace.contains(target)) target.scrollIntoView({ block: "center", behavior: "smooth" });
});

workspaceGuidanceInput?.addEventListener("input", () => {
  if (workspaceCurrentId !== null) attentionDrafts.set(workspaceCurrentId, workspaceGuidanceInput.value);
});

for (const chip of document.querySelectorAll("[data-guidance-template]")) {
  chip.addEventListener("click", () => {
    if (!workspaceGuidanceInput || workspaceGuidanceInput.disabled) return;
    const template = chip.dataset.guidanceTemplate ?? "";
    const start = workspaceGuidanceInput.selectionStart;
    const end = workspaceGuidanceInput.selectionEnd;
    workspaceGuidanceInput.setRangeText(template, start, end, "end");
    workspaceGuidanceInput.focus();
    if (workspaceCurrentId !== null) attentionDrafts.set(workspaceCurrentId, workspaceGuidanceInput.value);
  });
}

workspaceRefresh?.addEventListener("click", () => {
  if (workspaceCurrentId !== null) void loadWorkspace(workspaceCurrentId, { manual: true });
});
workspaceExpandHistory?.addEventListener("click", () => {
  if (workspaceCurrentId !== null) workspaceOlderExpanded.set(workspaceCurrentId, true);
  beadWorkspace?.querySelectorAll(".workspace-older-history").forEach(details => { details.open = true; });
});
workspaceCollapseHistory?.addEventListener("click", () => {
  if (workspaceCurrentId !== null) workspaceOlderExpanded.set(workspaceCurrentId, false);
  beadWorkspace?.querySelectorAll(".workspace-older-history").forEach(details => { details.open = false; });
});
window.addEventListener("hashchange", () => routeToWorkspace(true));
window.addEventListener("popstate", () => routeToWorkspace(true));
routeToWorkspace(false);

const factoryStatus = document.querySelector("#factory-status");
const deliverySlots = document.querySelector("#delivery-slots");
const mergerSlots = document.querySelector("#merger-slots");
const deliveryPoolStatus = document.querySelector("#delivery-pool-status");
const mergerPoolStatus = document.querySelector("#merger-pool-status");
const invocationDialog = document.querySelector("#invocation-dialog");
const invocationTitle = document.querySelector("#invocation-title");
const invocationStatus = document.querySelector("#invocation-status");
const invocationMetadata = document.querySelector("#invocation-metadata");
const invocationOutput = document.querySelector("#invocation-output");
const invocationClose = document.querySelector("#invocation-close");
const invocationPause = document.querySelector("#invocation-pause");
const invocationLatest = document.querySelector("#invocation-latest");
const invocationTruncation = document.querySelector("#invocation-truncation");

let factoryRequest = null;
let factoryGeneration = 0;
let lastFactoryVersion = null;
let factoryHasSnapshot = false;
let telemetrySource = null;
let telemetryGeneration = 0;
let telemetryFollow = true;
let telemetryCursor = 0;
let telemetryText = "";
let telemetryFocusSlot = null;
let telemetryFocusRuntime = null;
let telemetryFocusInvocation = null;
let telemetryTruncated = false;
let telemetrySourceEnded = false;

function factoryText(tag, className, value) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  node.textContent = value;
  return node;
}

let renderedFactoryRuntime = null;
let retainedFactorySnapshot = null;
let lastFactoryAvailability = null;

function factoryTime(value) {
  return value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString() : "Unavailable";
}

function appendFactoryInvocation(card, slot, runtimeId, poolName) {
  if (!slot.invocation) return;
  const open = factoryText("button", "secondary-action", slot.status === "running" ? "Open live invocation" : "Open retained invocation");
  open.type = "button";
  open.dataset.openTelemetrySlot = slot.slot_id;
  open.dataset.invocationId = slot.invocation.invocation_id;
  open.dataset.runtimeId = runtimeId;
  open.addEventListener("click", () => openTelemetry(runtimeId, poolName.toLowerCase(), slot));
  card.append(open);
}

function renderFactorySlot(slot, runtimeId, poolName, availability) {
  const card = document.createElement("article");
  card.className = "factory-slot";
  card.dataset.status = slot.status;
  card.dataset.slotId = slot.slot_id;
  const retained = availability !== "live";
  if (slot.status === "idle") {
    card.classList.add("factory-slot-idle");
    card.append(factoryText("p", "", slot.slot_id + " · ◌ Idle — "
      + (retained ? "Last observed idle" : "Available for admission")));
    appendFactoryInvocation(card, slot, runtimeId, poolName);
    return card;
  }
  const states = { running: "● Running", preparing: "◐ Preparing", reserved: "◐ Reserved", settling: "◐ Settling",
    failed: "✕ Failed", timed_out: "✕ Timed Out", paused: "⏸ Paused", completed: "✓ Completed" };
  const outcome = { failed: "✕ Failed", timed_out: "✕ Timed Out", launch_failed: "✕ Launch Failed",
    launch_denied: "✕ Launch Denied", preparation_failed: "✕ Preparation Failed" }[slot.invocation?.status];
  if (outcome) card.dataset.outcome = slot.invocation.status;
  const heading = factoryText("div", "slot-heading", "");
  heading.append(factoryText("span", "slot-identifier", poolName + " · " + slot.slot_id),
    factoryText("span", "slot-state", (retained ? "⚠ Last observed · " : "")
      + (states[slot.status] ?? "— " + (slot.status ?? "Unavailable")) + (outcome ? " · " + outcome : "")));
  card.append(heading);
  const title = factoryText("h4", "slot-bead", "");
  if (slot.bead_id) {
    const link = factoryText("a", "", slot.bead_id);
    link.href = "#bead/" + encodeURIComponent(slot.bead_id);
    link.dataset.beadWorkspaceLink = "true";
    title.append(link);
  } else title.textContent = "Bead unavailable";
  card.append(title, factoryText("p", "slot-activity", (slot.role ?? "Role unavailable") + " · " + (slot.stage ?? "Stage unavailable")));
  card.append(factoryText("p", "slot-timing", "Started: " + factoryTime(slot.invocation?.started_at)
    + " · Deadline: " + factoryTime(slot.invocation?.deadline_at)));
  const metrics = factoryText("p", "slot-metrics", "");
  if (slot.repair_cycles != null || slot.max_repair_cycles != null)
    metrics.append(factoryText("span", "slot-repairs", "Repairs: " + (slot.repair_cycles ?? "Unavailable") + " / " + (slot.max_repair_cycles ?? "Unavailable")));
  if (slot.diff?.status === "available")
    metrics.append(factoryText("span", "slot-diff", "+" + (slot.diff.added ?? "Unavailable") + " / −" + (slot.diff.deleted ?? "Unavailable")));
  if (metrics.childElementCount) card.append(metrics);
  const details = document.createElement("details");
  details.className = "slot-diagnostics";
  details.append(factoryText("summary", "", "Diagnostics"));
  const fields = [
    ["Invocation ID", slot.invocation?.invocation_id], ["CLI", slot.invocation?.cli],
    ["Configured model", slot.invocation?.configured_model], ["Actual model", slot.invocation?.actual_model],
    ["Branch", slot.branch], ["Worktree path", slot.worktree_path],
    ["Invocation outcome", slot.invocation?.status],
    ["Diff", slot.diff?.status === "available"
      ? "Binary: " + (slot.diff.binary ?? "Unavailable") + " · Untracked: " + (slot.diff.untracked ?? "Unavailable")
      : "Unavailable"]
  ];
  const metadata = document.createElement("dl");
  for (const [label, value] of fields)
    metadata.append(factoryText("dt", "", label), factoryText("dd", "", value ?? "Unavailable"));
  details.append(metadata);
  card.append(details);
  appendFactoryInvocation(card, slot, runtimeId, poolName);
  return card;
}

function renderFactory(snapshot, availability) {
  const sameRuntime = renderedFactoryRuntime === snapshot.runtime_id;
  const active = document.activeElement;
  const focusedCard = active?.closest(".factory-slot");
  const openSlots = new Set(sameRuntime ? [...document.querySelectorAll(".slot-diagnostics[open]")]
    .map(details => details.closest(".factory-slot").dataset.slotId) : []);
  const focusSlot = focusedCard?.dataset.slotId;
  const focusKind = active?.matches("summary") ? "summary" : active?.matches("a") ? "a" : "button";
  const focusIdentity = active?.dataset.invocationId ?? active?.getAttribute("href");
  for (const [pool, container, status, name] of [
    [snapshot.delivery, deliverySlots, deliveryPoolStatus, "Delivery"],
    [snapshot.merger, mergerSlots, mergerPoolStatus, "Merger"]
  ]) {
    const occupied = pool.slots.filter(slot => slot.status !== "idle").length;
    const idle = pool.slots.filter(slot => slot.status === "idle").length;
    status.textContent = (availability !== "live" ? "Last observed: " : "") + occupied + " of " + pool.capacity
      + (pool.capacity === 1 ? " slot active" : " slots active") + " · "
      + (pool.capacity === 0 ? "Pool disabled" : idle + " idle");
    container.replaceChildren(...pool.slots.map(slot => {
      const card = renderFactorySlot(slot, snapshot.runtime_id, name, availability);
      const details = card.querySelector("details");
      if (details) details.open = openSlots.has(slot.slot_id);
      return card;
    }));
  }
  if (focusedCard) {
    const replacement = sameRuntime ? [...document.querySelectorAll(".factory-slot")]
      .find(card => card.dataset.slotId === focusSlot)?.querySelector(focusKind) : null;
    if (replacement && (focusKind === "summary" || (replacement.dataset.invocationId ?? replacement.getAttribute("href")) === focusIdentity))
      replacement.focus({ preventScroll: true });
    else document.querySelector("#factory-title").focus({ preventScroll: true });
  }
  renderedFactoryRuntime = snapshot.runtime_id;
  lastFactoryAvailability = availability;
  if (invocationDialog.open && !telemetrySourceEnded) {
    const selected = [...snapshot.delivery.slots, ...snapshot.merger.slots].find(slot => slot.slot_id === telemetryFocusSlot);
    if (snapshot.runtime_id !== telemetryFocusRuntime || selected?.invocation?.invocation_id !== telemetryFocusInvocation)
      invocationStatus.textContent = "— Selected invocation is no longer current. Showing retained output; following only the selected stream.";
  }
}

function renderFactoryHealth(snapshot, availability, reason) {
  const idle = snapshot && [...snapshot.delivery.slots, ...snapshot.merger.slots].every(slot => slot.status === "idle");
  const state = availability === "live" ? (idle ? "idle" : "live") : availability;
  const labels = { live: "● Live", idle: "◌ Idle · Fresh runtime", stale: "⚠ Stale",
    disconnected: "✕ Disconnected", unavailable: "— Unavailable" };
  factoryStatus.dataset.state = state;
  const summary = factoryText("strong", "health-state", labels[state] ?? labels.unavailable);
  const details = [];
  if (availability === "unavailable") details.push("No active runtime observations (" + (reason ?? "unknown") + ")");
  if (snapshot) {
    details.push("Project " + (snapshot.project ?? "Unavailable"), "Runtime " + snapshot.runtime_id,
      (availability === "live" ? "Observed " : "Last heartbeat ") + factoryTime(snapshot.observed_at),
      "Target Repository · " + (snapshot.target_repository ?? "Unavailable"));
    if (availability !== "live") details.push("Showing last validated snapshot");
  }
  factoryStatus.replaceChildren(summary, ...details.map(value => factoryText("span", "health-detail", value)));
}

async function refreshFactory() {
  if (factoryRequest) return;
  const generation = ++factoryGeneration;
  const controller = new AbortController();
  factoryRequest = controller;
  try {
    const response = await fetch("/api/factory/state", {
      headers: { Accept: "application/json" },
      signal: controller.signal
    });
    if (!response.ok) throw new Error("factory state unavailable");
    const result = await response.json();
    if (generation !== factoryGeneration) return;
    const snapshot = result.snapshot;
    // Malformed observations must not replace the last validated snapshot.
    const valid = snapshot && typeof snapshot.runtime_id === "string" && snapshot.runtime_id.length > 0
      && Number.isFinite(Date.parse(snapshot.observed_at)) && Number.isSafeInteger(snapshot.revision)
      && [snapshot.delivery, snapshot.merger].every(pool => pool && Number.isInteger(pool.capacity)
        && Array.isArray(pool.slots) && pool.slots.every(slot => typeof slot.slot_id === "string" && typeof slot.status === "string"));
    const availability = valid && ["live", "stale"].includes(result.availability) ? result.availability : "unavailable";
    if (valid && ["live", "stale", "stopped"].includes(result.availability)) {
      const key = snapshot.runtime_id + ":" + snapshot.revision;
      if (!factoryHasSnapshot || key !== lastFactoryVersion || availability !== lastFactoryAvailability)
        renderFactory(snapshot, availability);
      retainedFactorySnapshot = snapshot;
      lastFactoryVersion = key;
      factoryHasSnapshot = true;
    } else if (retainedFactorySnapshot) renderFactory(retainedFactorySnapshot, availability);
    renderFactoryHealth(retainedFactorySnapshot, availability, result.reason
      ?? (result.availability === "stopped" ? "runtime stopped" : "unknown"));
  } catch (error) {
    if (error.name !== "AbortError") {
      const availability = factoryHasSnapshot ? "disconnected" : "unavailable";
      if (retainedFactorySnapshot) renderFactory(retainedFactorySnapshot, availability);
      renderFactoryHealth(retainedFactorySnapshot, availability, "connection failed");
    }
  } finally {
    if (factoryRequest === controller) factoryRequest = null;
  }
}

function appendTelemetryText(value) {
  const safe = value.replace(/\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))/g, "")
    .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, "");
  telemetryText += safe;
  const lines = telemetryText.split("\n");
  if (lines.length > 2000) {
    telemetryText = lines.slice(-2000).join("\n");
    telemetryTruncated = true;
  }
  const bytes = new TextEncoder().encode(telemetryText);
  if (bytes.length > 256 * 1024) {
    let start = bytes.length - 256 * 1024;
    while ((bytes[start] & 0xc0) === 0x80) start++;
    telemetryText = new TextDecoder().decode(bytes.subarray(start));
    telemetryTruncated = true;
  }
  const scrollTop = invocationOutput.scrollTop;
  invocationOutput.textContent = telemetryText;
  invocationTruncation.hidden = !telemetryTruncated;
  if (!telemetryFollow) invocationOutput.scrollTop = scrollTop;
  if (telemetryFollow) invocationOutput.scrollTop = invocationOutput.scrollHeight;
}

function closeTelemetry(restoreFocus = true) {
  telemetryGeneration += 1;
  telemetrySource?.close();
  telemetrySource = null;
  telemetrySourceEnded = true;
  if (invocationDialog.open) invocationDialog.close();
  if (restoreFocus && telemetryFocusSlot) {
    const trigger = Array.from(document.querySelectorAll("[data-open-telemetry-slot]"))
      .find(node => node.dataset.openTelemetrySlot === telemetryFocusSlot
        && node.dataset.runtimeId === telemetryFocusRuntime && node.dataset.invocationId === telemetryFocusInvocation);
    (trigger ?? document.querySelector("#factory-title")).focus({ preventScroll: true });
  }
  telemetryFocusSlot = null;
  invocationPause.disabled = true;
  invocationLatest.disabled = true;
}

function openTelemetry(runtimeId, pool, slot) {
  closeTelemetry(false);
  telemetryFocusSlot = slot.slot_id;
  telemetryFocusRuntime = runtimeId;
  telemetryFocusInvocation = slot.invocation.invocation_id;
  telemetryTruncated = false;
  telemetryGeneration += 1;
  const generation = telemetryGeneration;
  telemetryFollow = true;
  telemetryCursor = 0;
  telemetryText = "";
  telemetrySourceEnded = false;
  invocationOutput.textContent = "";
  invocationTruncation.hidden = true;
  invocationPause.disabled = false;
  invocationPause.textContent = "Pause follow";
  invocationLatest.disabled = false;
  invocationTitle.textContent = (slot.role ?? "Retained invocation") + " · " + (slot.invocation.bead_id ?? slot.bead_id ?? "Unknown task");
  invocationStatus.textContent = "◐ Connecting to " + pool + " " + slot.slot_id + " · invocation " + slot.invocation.invocation_id;
  invocationMetadata.replaceChildren();
  const values = [
    ["Runtime", runtimeId], ["Slot", slot.slot_id], ["Invocation", slot.invocation.invocation_id],
    ["Bead", slot.invocation.bead_id ?? slot.bead_id ?? "Unavailable"], ["CLI", slot.invocation.cli],
    ["Configured model", slot.invocation.configured_model ?? "Unavailable"],
    ["Actual model", slot.invocation.actual_model ?? "Unavailable"],
    ["Worktree", slot.worktree_path ?? "Unavailable"], ["Branch", slot.branch ?? "Unavailable"]
  ];
  for (const [label, value] of values) {
    const row = document.createElement("div");
    if (label === "Worktree" || label === "Branch") row.className = "telemetry-path";
    row.append(factoryText("dt", "", label), factoryText("dd", "", value ?? "Unavailable"));
    invocationMetadata.append(row);
  }
  if (!invocationDialog.open) invocationDialog.showModal();
  const query = new URLSearchParams({
    runtime_id: runtimeId,
    slot_id: slot.slot_id,
    invocation_id: slot.invocation.invocation_id
  });
  const source = new EventSource("/api/telemetry/stream?" + query);
  telemetrySource = source;
  source.addEventListener("status", event => {
    if (generation !== telemetryGeneration) return;
    try {
      const data = JSON.parse(event.data);
      const labels = { running: "● Running", completed: "✓ Exited · completed", failed: "✕ Failed", timed_out: "✕ Timed Out" };
      invocationStatus.textContent = data.availability === "live"
        ? (labels[data.status] ?? "◐ " + data.status) : "⚠ " + data.availability + " · " + data.status;
    } catch { invocationStatus.textContent = "Invocation status is unavailable."; }
  });
  source.addEventListener("reset", () => {
    if (generation !== telemetryGeneration) return;
    telemetryText = "";
    telemetryTruncated = false;
    telemetryCursor = 0;
    invocationOutput.textContent = "";
    invocationTruncation.hidden = true;
  });
  source.addEventListener("log", event => {
    if (generation !== telemetryGeneration) return;
    try {
      const data = JSON.parse(event.data);
      if (!Number.isSafeInteger(data.cursor) || data.cursor <= telemetryCursor) return;
      telemetryCursor = data.cursor;
      appendTelemetryText(data.text ?? "");
    } catch { invocationStatus.textContent = "A malformed log event was ignored."; }
  });
  source.addEventListener("error", event => {
    if (generation !== telemetryGeneration) return;
    if (event instanceof MessageEvent) {
      try {
        const data = JSON.parse(event.data);
        invocationStatus.textContent = "— Log stream unavailable: " + (data.reason ?? "unknown");
      } catch { invocationStatus.textContent = "— Log stream unavailable."; }
    } else if (!telemetrySourceEnded) invocationStatus.textContent = "✕ Disconnected · Reconnecting… Agent execution may still be running.";
  });
  source.addEventListener("end", event => {
    if (generation !== telemetryGeneration) return;
    telemetrySourceEnded = true;
    source.close();
    if (telemetrySource === source) telemetrySource = null;
    try {
      const data = JSON.parse(event.data);
      const outcome = data.status ?? data.reason ?? "complete";
      const knownOutcome = ["completed", "failed", "timed_out", "launch_failed", "launch_denied", "preparation_failed", "cancelled"].includes(data.status);
      const glyph = data.status === "completed" ? "✓ "
        : knownOutcome && data.status !== "cancelled" ? "✕ " : "— ";
      invocationStatus.textContent = glyph + "Stream ended: " + outcome
        + (!knownOutcome ? " · Agent outcome unavailable; retained output remains readable." : "");
    } catch { invocationStatus.textContent = "Stream ended."; }
  });
}

invocationOutput.addEventListener("scroll", () => {
  const nearBottom = invocationOutput.scrollHeight - invocationOutput.scrollTop - invocationOutput.clientHeight < 48;
  if (telemetryFollow && !nearBottom) {
    telemetryFollow = false;
    invocationPause.textContent = "Resume follow";
  }
});
invocationPause.addEventListener("click", () => {
  telemetryFollow = !telemetryFollow;
  invocationPause.textContent = telemetryFollow ? "Pause follow" : "Resume follow";
  if (telemetryFollow) invocationOutput.scrollTop = invocationOutput.scrollHeight;
});
invocationLatest.addEventListener("click", () => {
  telemetryFollow = true;
  invocationPause.textContent = "Pause follow";
  invocationOutput.scrollTop = invocationOutput.scrollHeight;
});
invocationDialog.addEventListener("keydown", event => {
  if (event.key !== "Tab") return;
  const controls = [...invocationDialog.querySelectorAll("button:not(:disabled), [tabindex='0']")]
    .filter(node => node.getClientRects().length);
  const first = controls[0];
  const last = controls.at(-1);
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last?.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first?.focus();
  }
});
invocationClose.addEventListener("click", () => closeTelemetry());
invocationDialog.addEventListener("cancel", event => { event.preventDefault(); closeTelemetry(); });
// Native close events are queued: an earlier close must not dismiss a newly opened inspection.
invocationDialog.addEventListener("close", () => { if (!invocationDialog.open && telemetryFocusSlot) closeTelemetry(); });
window.addEventListener("hashchange", () => { if (invocationDialog.open) closeTelemetry(false); }, { capture: true });
window.addEventListener("beforeunload", () => {
  factoryRequest?.abort();
  closeTelemetry(false);
});
void refreshFactory();
window.setInterval(() => void refreshFactory(), 2000);
