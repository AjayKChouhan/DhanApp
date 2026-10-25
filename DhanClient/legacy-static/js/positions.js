document.addEventListener("DOMContentLoaded", () => {
  initTopbar("positions");

  const loadBtn = document.getElementById("loadPositionsBtn");
  const statusEl = document.getElementById("positionsStatus");
  const container = document.getElementById("positionsContainer");

  async function loadPositions() {
    loadBtn.disabled = true;
    statusEl.textContent = "";
    container.innerHTML = `<div class="empty-state">Loading accounts...</div>`;

    try {
      const accounts = await DhanApi.getAccountsStatus();
      if (!accounts || accounts.length === 0) {
        container.innerHTML = `<div class="empty-state">No saved accounts yet. Add accounts and login first.</div>`;
        return;
      }

      container.innerHTML = "";
      const results = await Promise.all(
        accounts.map(async (account) => {
          try {
            const result = await DhanApi.getPositions(account.dhanClientId);
            return { account, result };
          } catch (err) {
            return { account, result: { success: false, errorMessage: err.message } };
          }
        })
      );

      for (const { account, result } of results) {
        const block = document.createElement("div");
        block.className = "account-block";
        const heading = `<h3>${DhanRender.escapeHtml(account.accountName)} <span class="muted">(${DhanRender.escapeHtml(account.dhanClientId)})</span></h3>`;

        if (!result.success) {
          block.innerHTML = `${heading}<div class="status-line error">${DhanRender.escapeHtml(result.errorMessage || "Failed to load positions.")}</div>`;
        } else {
          const rows = extractArray(result.positions);
          block.innerHTML = `${heading}${DhanRender.renderTable(rows)}`;
        }
        container.appendChild(block);
      }
    } catch (err) {
      statusEl.textContent = `Failed to load positions: ${err.message}`;
      statusEl.className = "status-line error";
      container.innerHTML = "";
    } finally {
      loadBtn.disabled = false;
    }
  }

  loadBtn.addEventListener("click", loadPositions);
});
