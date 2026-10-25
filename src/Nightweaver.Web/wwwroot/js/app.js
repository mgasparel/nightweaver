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

if (beadsSearch && beadsBody && beadFilters.length > 0) {
  let selectedView = "all";
  let requestGeneration = 0;
  let activeRequest = null;
  let searchTimer = null;

  function setExplorerMessage({ loading = false, empty = false, noMatches = false, error = false }) {
    beadsLoading.hidden = !loading;
    beadsEmpty.hidden = !empty;
    beadsNoMatches.hidden = !noMatches;
    beadsError.hidden = !error;
    beadsLive.textContent = loading ? "Loading beads."
      : error ? "The Beads tracker is unavailable."
        : empty ? beadsEmpty.textContent
          : noMatches ? "No beads match your search." : "Beads list updated.";
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

  beadsSearch.addEventListener("input", queueSearch);
  for (const button of beadFilters) {
    button.addEventListener("click", () => {
      selectedView = button.dataset.view;
      for (const filter of beadFilters) filter.setAttribute("aria-pressed", String(filter === button));
      void loadBeads();
    });
  }
  retryButton?.addEventListener("click", () => void loadBeads());
  void loadBeads();
}
