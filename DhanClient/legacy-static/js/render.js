// Generic helpers for rendering dynamic (schema-less) data as HTML tables.
const DhanRender = (() => {
  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function formatCell(value) {
    if (value === null || value === undefined) return "";
    if (typeof value === "object") return escapeHtml(JSON.stringify(value));
    return escapeHtml(value);
  }

  /// Renders an array of plain objects as an HTML table, deriving columns
  /// from the union of keys across all rows (Dhan's raw API payloads vary).
  function renderTable(rows) {
    if (!Array.isArray(rows) || rows.length === 0) {
      return `<div class="empty-state">No records.</div>`;
    }

    const columns = [];
    const seen = new Set();
    for (const row of rows) {
      if (row && typeof row === "object") {
        for (const key of Object.keys(row)) {
          if (!seen.has(key)) {
            seen.add(key);
            columns.push(key);
          }
        }
      }
    }

    if (columns.length === 0) {
      return `<div class="empty-state">No records.</div>`;
    }

    const head = columns.map((c) => `<th>${escapeHtml(c)}</th>`).join("");
    const body = rows
      .map((row) => {
        const cells = columns.map((c) => `<td>${formatCell(row ? row[c] : "")}</td>`).join("");
        return `<tr>${cells}</tr>`;
      })
      .join("");

    return `<table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
  }

  function badge(success) {
    return success
      ? `<span class="badge ok">SUCCESS</span>`
      : `<span class="badge fail">FAILED</span>`;
  }

  /// Renders a table from rows whose values are already-safe HTML (e.g. built via
  /// `badge()` or `escapeHtml()` by the caller). Column order follows the first row.
  function renderRawTable(rows) {
    if (!Array.isArray(rows) || rows.length === 0) {
      return `<div class="empty-state">No records.</div>`;
    }

    const columns = Object.keys(rows[0]);
    const head = columns.map((c) => `<th>${escapeHtml(c)}</th>`).join("");
    const body = rows
      .map((row) => `<tr>${columns.map((c) => `<td>${row[c] ?? ""}</td>`).join("")}</tr>`)
      .join("");

    return `<table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
  }

  return { renderTable, renderRawTable, badge, escapeHtml, formatCell };
})();

// Extracts the first array found in a passthrough Dhan API payload, since the
// backend forwards Dhan's raw JSON as-is (shape can vary: a bare array, or an
// object wrapping one).
function extractArray(payload) {
  if (Array.isArray(payload)) return payload;
  if (payload && typeof payload === "object") {
    for (const value of Object.values(payload)) {
      if (Array.isArray(value)) return value;
    }
    return [payload];
  }
  return [];
}
