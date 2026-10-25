// Shared configuration for the DhanClient static app.
// Values are persisted to localStorage so they survive page navigation/reloads.
const DhanConfig = (() => {
  const BASE_URL_KEY = "dhanai.apiBaseUrl";
  const API_KEY_KEY = "dhanai.apiKey";
  const DEFAULT_BASE_URL = "http://localhost:5050/api/dhantrading";

  function getBaseUrl() {
    return localStorage.getItem(BASE_URL_KEY) || DEFAULT_BASE_URL;
  }

  function setBaseUrl(value) {
    localStorage.setItem(BASE_URL_KEY, value || DEFAULT_BASE_URL);
  }

  function getApiKey() {
    return localStorage.getItem(API_KEY_KEY) || "";
  }

  function setApiKey(value) {
    localStorage.setItem(API_KEY_KEY, value || "");
  }

  return { getBaseUrl, setBaseUrl, getApiKey, setApiKey, DEFAULT_BASE_URL };
})();
