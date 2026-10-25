// Wires up the shared topbar (nav highlighting + settings panel) on every page.
function initTopbar(activePage) {
  document.querySelectorAll("nav.main-nav a").forEach((a) => {
    if (a.dataset.page === activePage) {
      a.classList.add("active");
    }
  });

  const toggle = document.getElementById("settingsToggle");
  const panel = document.getElementById("settingsPanel");
  const baseUrlInput = document.getElementById("apiBaseUrlInput");
  const apiKeyInput = document.getElementById("apiKeyInput");
  const saveBtn = document.getElementById("saveSettingsBtn");

  if (baseUrlInput) baseUrlInput.value = DhanConfig.getBaseUrl();
  if (apiKeyInput) apiKeyInput.value = DhanConfig.getApiKey();

  if (toggle && panel) {
    toggle.addEventListener("click", () => panel.classList.toggle("open"));
  }

  if (saveBtn) {
    saveBtn.addEventListener("click", () => {
      DhanConfig.setBaseUrl(baseUrlInput.value.trim());
      DhanConfig.setApiKey(apiKeyInput.value.trim());
      panel.classList.remove("open");
    });
  }
}
