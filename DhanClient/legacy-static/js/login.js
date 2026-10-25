document.addEventListener("DOMContentLoaded", () => {
  initTopbar("login");

  const accountRows = document.getElementById("accountRows");
  const addRowBtn = document.getElementById("addRowBtn");
  const saveAccountsBtn = document.getElementById("saveAccountsBtn");
  const saveAccountsStatus = document.getElementById("saveAccountsStatus");
  const refreshStatusBtn = document.getElementById("refreshStatusBtn");
  const statusTable = document.getElementById("statusTable");
  const loginBtn = document.getElementById("loginBtn");
  const loginResultsTable = document.getElementById("loginResultsTable");

  function addAccountRow(values = {}) {
    const row = document.createElement("div");
    row.className = "account-row";
    row.innerHTML = `
      <div class="field">
        <label>Account Name</label>
        <input type="text" class="acc-name" placeholder="e.g. John's Account" value="${DhanRender.escapeHtml(values.accountName || "")}" />
      </div>
      <div class="field">
        <label>Dhan Client ID</label>
        <input type="text" class="acc-clientid" placeholder="1000000001" value="${DhanRender.escapeHtml(values.dhanClientId || "")}" />
      </div>
      <div class="field">
        <label>PIN</label>
        <input type="password" class="acc-pin" placeholder="PIN" />
      </div>
      <div class="field">
        <label>TOTP Secret</label>
        <input type="text" class="acc-totp" placeholder="Base32 secret" />
      </div>
      <button type="button" class="secondary remove-row-btn">Remove</button>
    `;
    row.querySelector(".remove-row-btn").addEventListener("click", () => row.remove());
    accountRows.appendChild(row);
  }

  addRowBtn.addEventListener("click", () => addAccountRow());

  async function loadExistingAccountNames() {
    try {
      const existing = await DhanApi.getAccountsStatus();
      if (Array.isArray(existing) && existing.length > 0) {
        existing.forEach((a) => addAccountRow({ accountName: a.accountName, dhanClientId: a.dhanClientId }));
        return;
      }
    } catch {
      // Ignore - likely no accounts saved yet / API unreachable.
    }
    addAccountRow();
  }

  saveAccountsBtn.addEventListener("click", async () => {
    const rows = Array.from(accountRows.querySelectorAll(".account-row"));
    const accounts = rows.map((row) => ({
      accountName: row.querySelector(".acc-name").value.trim(),
      dhanClientId: row.querySelector(".acc-clientid").value.trim(),
      pin: row.querySelector(".acc-pin").value.trim(),
      totpSecret: row.querySelector(".acc-totp").value.trim(),
    })).filter((a) => a.dhanClientId);

    if (accounts.length === 0) {
      saveAccountsStatus.textContent = "Add at least one account with a Dhan Client ID.";
      saveAccountsStatus.className = "status-line error";
      return;
    }

    saveAccountsBtn.disabled = true;
    saveAccountsStatus.textContent = "Saving...";
    saveAccountsStatus.className = "status-line";
    try {
      await DhanApi.saveAccounts(accounts);
      saveAccountsStatus.textContent = `Saved ${accounts.length} account(s).`;
      saveAccountsStatus.className = "status-line success";
      await refreshStatus();
    } catch (err) {
      saveAccountsStatus.textContent = `Failed to save accounts: ${err.message}`;
      saveAccountsStatus.className = "status-line error";
    } finally {
      saveAccountsBtn.disabled = false;
    }
  });

  async function refreshStatus() {
    statusTable.innerHTML = `<div class="empty-state">Loading...</div>`;
    try {
      const accounts = await DhanApi.getAccountsStatus();
      const rows = (accounts || []).map((a) => ({
        "Account Name": DhanRender.escapeHtml(a.accountName),
        "Dhan Client ID": DhanRender.escapeHtml(a.dhanClientId),
        Connected: DhanRender.badge(a.isConnected),
        "Token Expiry (UTC)": a.tokenExpiryUtc ? DhanRender.escapeHtml(new Date(a.tokenExpiryUtc).toLocaleString()) : "-",
      }));
      statusTable.innerHTML = rows.length
        ? DhanRender.renderRawTable(rows)
        : `<div class="empty-state">No accounts saved yet.</div>`;
    } catch (err) {
      statusTable.innerHTML = `<div class="status-line error">Failed to load status: ${DhanRender.escapeHtml(err.message)}</div>`;
    }
  }

  refreshStatusBtn.addEventListener("click", refreshStatus);

  loginBtn.addEventListener("click", async () => {
    loginBtn.disabled = true;
    loginResultsTable.innerHTML = `<div class="empty-state">Logging in...</div>`;
    try {
      const results = await DhanApi.login();
      const rows = (results || []).map((r) => ({
        "Account Name": DhanRender.escapeHtml(r.accountName),
        "Dhan Client ID": DhanRender.escapeHtml(r.dhanClientId),
        Result: DhanRender.badge(r.success),
        "Expiry (UTC)": r.expiryUtc ? DhanRender.escapeHtml(new Date(r.expiryUtc).toLocaleString()) : "-",
        Error: DhanRender.escapeHtml(r.errorMessage || ""),
      }));
      loginResultsTable.innerHTML = DhanRender.renderRawTable(rows);
      await refreshStatus();
    } catch (err) {
      loginResultsTable.innerHTML = `<div class="status-line error">Login failed: ${DhanRender.escapeHtml(err.message)}</div>`;
    } finally {
      loginBtn.disabled = false;
    }
  });

  loadExistingAccountNames();
  refreshStatus();
});
