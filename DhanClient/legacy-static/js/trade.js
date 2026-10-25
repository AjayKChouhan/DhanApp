document.addEventListener("DOMContentLoaded", () => {
  initTopbar("trade");

  const filterInput = document.getElementById("instrumentFilter");
  const selectEl = document.getElementById("instrumentSelect");
  const lotSizeInput = document.getElementById("lotSizeInput");
  const lotsInput = document.getElementById("lotsInput");
  const buyBtn = document.getElementById("buyBtn");
  const sellBtn = document.getElementById("sellBtn");
  const placeOrderBtn = document.getElementById("placeOrderBtn");
  const tradeAccounts = document.getElementById("tradeAccounts");
  const selectAllAccountsBtn = document.getElementById("selectAllAccountsBtn");
  const clearAccountsBtn = document.getElementById("clearAccountsBtn");
  const selectedAccountCount = document.getElementById("selectedAccountCount");
  const tradeStatus = document.getElementById("tradeStatus");
  const orderResultsTable = document.getElementById("orderResultsTable");

  let instruments = [];
  let accounts = [];
  let transactionType = "BUY";

  function updateSelectedAccountCount() {
    const count = tradeAccounts.querySelectorAll('input[type="checkbox"]:checked').length;
    selectedAccountCount.textContent = `${count} selected`;
  }

  function renderAccounts() {
    tradeAccounts.replaceChildren();
    updateSelectedAccountCount();
    if (accounts.length === 0) {
      tradeAccounts.innerHTML = `<div class="empty-state">No accounts saved yet. Add accounts before placing orders.</div>`;
      return;
    }

    accounts.forEach((account) => {
      const label = document.createElement("label");
      label.className = "trade-account-option";
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.value = account.dhanClientId;
      const details = document.createElement("span");
      details.className = "trade-account-details";
      const name = document.createElement("span");
      name.className = "trade-account-name";
      name.textContent = account.accountName;
      const clientId = document.createElement("span");
      clientId.className = "trade-account-id";
      clientId.textContent = account.dhanClientId;
      details.append(name, clientId);
      const status = document.createElement("span");
      status.className = `trade-account-status${account.isConnected ? " connected" : ""}`;
      const statusDot = document.createElement("span");
      statusDot.className = "trade-status-dot";
      status.append(statusDot, document.createTextNode(account.isConnected ? "Connected" : "Offline"));
      label.append(checkbox, details, status);
      checkbox.addEventListener("change", () => {
        label.classList.toggle("selected", checkbox.checked);
        updateSelectedAccountCount();
      });
      tradeAccounts.appendChild(label);
    });
    updateSelectedAccountCount();
  }

  function describeInstrument(i) {
    const expiry = i.expiryDate ? new Date(i.expiryDate).toLocaleDateString() : "";
    return `${i.underlyingSymbol || i.tradingSymbol} ${expiry ? `(${expiry})` : ""} - Lot ${i.lotSize}`.trim();
  }

  function renderOptions(list) {
    selectEl.innerHTML = list
      .map((i, idx) => `<option value="${idx}">${DhanRender.escapeHtml(describeInstrument(i))}</option>`)
      .join("");
    onInstrumentChange();
  }

  function onInstrumentChange() {
    const idx = Number(selectEl.value);
    const instrument = instruments[idx];
    lotSizeInput.value = instrument ? instrument.lotSize : "";
  }

  filterInput.addEventListener("input", () => {
    const term = filterInput.value.trim().toLowerCase();
    const filtered = term
      ? instruments.filter(
          (i) =>
            (i.underlyingSymbol || "").toLowerCase().includes(term) ||
            (i.tradingSymbol || "").toLowerCase().includes(term) ||
            (i.displayName || "").toLowerCase().includes(term)
        )
      : instruments;
    renderOptions(filtered);
  });

  selectEl.addEventListener("change", onInstrumentChange);

  function setTransactionType(type) {
    transactionType = type;
    buyBtn.classList.toggle("active", type === "BUY");
    sellBtn.classList.toggle("active", type === "SELL");
  }

  buyBtn.addEventListener("click", () => setTransactionType("BUY"));
  sellBtn.addEventListener("click", () => setTransactionType("SELL"));
  selectAllAccountsBtn.addEventListener("click", () => {
    tradeAccounts.querySelectorAll('input[type="checkbox"]').forEach((checkbox) => {
      checkbox.checked = true;
      checkbox.closest(".trade-account-option").classList.add("selected");
    });
    updateSelectedAccountCount();
  });
  clearAccountsBtn.addEventListener("click", () => {
    tradeAccounts.querySelectorAll('input[type="checkbox"]').forEach((checkbox) => {
      checkbox.checked = false;
      checkbox.closest(".trade-account-option").classList.remove("selected");
    });
    updateSelectedAccountCount();
  });

  placeOrderBtn.addEventListener("click", async () => {
    const idx = Number(selectEl.value);
    const instrument = instruments[idx];
    const lots = Number(lotsInput.value);
    const accountClientIds = Array.from(tradeAccounts.querySelectorAll('input[type="checkbox"]:checked'))
      .map((checkbox) => checkbox.value);

    if (accountClientIds.length === 0) {
      tradeStatus.textContent = "Select at least one account to place orders.";
      tradeStatus.className = "status-line error";
      return;
    }
    if (!instrument) {
      tradeStatus.textContent = "Select an instrument first.";
      tradeStatus.className = "status-line error";
      return;
    }
    if (!lots || lots <= 0) {
      tradeStatus.textContent = "Lots must be greater than zero.";
      tradeStatus.className = "status-line error";
      return;
    }

    placeOrderBtn.disabled = true;
    tradeStatus.textContent = `Placing order for ${accountClientIds.length} selected account(s)...`;
    tradeStatus.className = "status-line";
    orderResultsTable.innerHTML = "";

    try {
      const results = await DhanApi.placeOrder({
        accountClientIds,
        securityId: instrument.securityId,
        tradingSymbol: instrument.tradingSymbol,
        lotSize: instrument.lotSize,
        lots,
        transactionType,
      });

      const rows = (results || []).map((r) => ({
        "Account Name": DhanRender.escapeHtml(r.accountName),
        "Dhan Client ID": DhanRender.escapeHtml(r.dhanClientId),
        Result: DhanRender.badge(r.success),
        "Order Id": DhanRender.escapeHtml(r.orderId || ""),
        Status: DhanRender.escapeHtml(r.orderStatus || ""),
        Error: DhanRender.escapeHtml(r.errorMessage || ""),
      }));

      orderResultsTable.innerHTML = DhanRender.renderRawTable(rows);

      tradeStatus.textContent = `Order submitted for ${rows.length} selected account(s).`;
      tradeStatus.className = "status-line success";
    } catch (err) {
      tradeStatus.textContent = `Failed to place order: ${err.message}`;
      tradeStatus.className = "status-line error";
    } finally {
      placeOrderBtn.disabled = false;
    }
  });

  (async function init() {
    await Promise.all([
      (async () => {
        try {
          accounts = (await DhanApi.getAccountsStatus()) || [];
          renderAccounts();
        } catch (err) {
          tradeAccounts.innerHTML = `<div class="status-line error">Failed to load accounts: ${DhanRender.escapeHtml(err.message)}</div>`;
        }
      })(),
      (async () => {
        try {
          instruments = (await DhanApi.getInstruments()) || [];
          renderOptions(instruments);
          setTransactionType("BUY");
        } catch (err) {
          tradeStatus.textContent = `Failed to load instruments: ${err.message}`;
          tradeStatus.className = "status-line error";
        }
      })(),
    ]);
  })();
});
