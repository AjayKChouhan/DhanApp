import { Component, Input } from '@angular/core';

// Renders an array of plain objects as an HTML table, deriving columns from
// the union of keys across all rows (Dhan's raw API payloads vary by endpoint).
@Component({
  selector: 'app-dynamic-table',
  imports: [],
  templateUrl: './dynamic-table.component.html',
  styleUrl: './dynamic-table.component.css'
})
export class DynamicTableComponent {
  private _rows: Record<string, unknown>[] = [];
  columns: string[] = [];

  @Input()
  set rows(value: Record<string, unknown>[] | null | undefined) {
    this._rows = Array.isArray(value) ? value : [];
    this.columns = this.deriveColumns(this._rows);
  }
  get rows(): Record<string, unknown>[] {
    return this._rows;
  }

  private deriveColumns(rows: Record<string, unknown>[]): string[] {
    const seen = new Set<string>();
    const columns: string[] = [];
    for (const row of rows) {
      if (row && typeof row === 'object') {
        for (const key of Object.keys(row)) {
          if (!seen.has(key)) {
            seen.add(key);
            columns.push(key);
          }
        }
      }
    }
    return columns;
  }

  formatCell(row: Record<string, unknown>, column: string): string {
    const value = row ? row[column] : undefined;
    if (value === null || value === undefined) return '';
    if (typeof value === 'object') return JSON.stringify(value);
    return String(value);
  }
}
