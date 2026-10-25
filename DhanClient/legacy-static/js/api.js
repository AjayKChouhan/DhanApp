// Minimal fetch wrapper that talks to the DhanAPI backend.
const DhanApi = (() => {
  function headers(extra) {
    const h = { "Content-Type": "application/json", ...extra };
    const apiKey = DhanConfig.getApiKey();
    if (apiKey) {
      h["X-Dhan-Api-Key"] = apiKey;
    }
    return h;
  }

  async function handle(response) {
    const text = await response.text();
    let data = null;
    if (text) {
      try {
        data = JSON.parse(text);
      } catch {
        data = text;
      }
    }

    if (!response.ok) {
      const message = (data && data.title) || (typeof data === "string" ? data : null) || `Request failed (${response.status})`;
      throw new Error(message);
    }

    return data;
  }

  async function get(path) {
    const response = await fetch(`${DhanConfig.getBaseUrl()}${path}`, {
      method: "GET",
      headers: headers(),
    });
    return handle(response);
  }

  async function post(path, body) {
    const response = await fetch(`${DhanConfig.getBaseUrl()}${path}`, {
      method: "POST",
      headers: headers(),
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    return handle(response);
  }

  return {
    getAccountsStatus: () => get("/accounts/status"),
    saveAccounts: (accounts) => post("/accounts", { accounts }),
    login: () => post("/login"),
    getPositions: (dhanClientId) => get(`/accounts/${encodeURIComponent(dhanClientId)}/positions`),
    getOrders: (dhanClientId) => get(`/accounts/${encodeURIComponent(dhanClientId)}/orders`),
    getInstruments: () => get("/instruments"),
    placeOrder: (order) => post("/order", order),
  };
})();
