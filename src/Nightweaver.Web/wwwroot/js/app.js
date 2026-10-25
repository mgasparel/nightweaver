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
