import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DhanApiService } from '../../core/dhan-api.service';
import { DhanAccountStatus, FuturesInstrument, OrderResult } from '../../core/models';

interface TradeRow {
  instrument: FuturesInstrument;
  lots: number;
  transactionType: 'BUY' | 'SELL';
  orderType: 'MARKET' | 'LIMIT';
  price: number | null;
  loadingPrice: boolean;
  selected: boolean;
}

interface OrderResultRow extends OrderResult {
  symbol: string;
}

export type ExchangeOption = 'NSE' | 'BSE' | 'MCX';

@Component({
  selector: 'app-trade',
  imports: [FormsModule],
  templateUrl: './trade.component.html'
})
export class TradeComponent implements OnInit {
  readonly exchanges: ExchangeOption[] = ['NSE', 'BSE', 'MCX'];
  selectedExchange: ExchangeOption = 'NSE';

  instruments: FuturesInstrument[] = [];
  instrumentsLoading = false;
  searchTerm = '';
  searchResults: FuturesInstrument[] = [];
  showSuggestions = false;

  rows: TradeRow[] = [];

  accounts: DhanAccountStatus[] = [];
  accountsLoading = false;
  accountsError = '';
  selectedAccountIds: string[] = [];

  placingOrder = false;
  tradeStatusMessage = '';
  tradeStatusIsError = false;
  orderResults: OrderResultRow[] = [];

  constructor(private readonly api: DhanApiService) {}

  async ngOnInit(): Promise<void> {
    await Promise.all([this.loadInstruments(), this.loadAccounts()]);
  }

  async loadAccounts(): Promise<void> {
    this.accountsLoading = true;
    this.accountsError = '';
    try {
      this.accounts = (await this.api.getAccountsStatus()) || [];
      this.selectedAccountIds = this.selectedAccountIds.filter((id) =>
        this.accounts.some((account) => account.dhanClientId === id)
      );
    } catch (err) {
      this.accountsError = `Failed to load accounts: ${(err as Error).message}`;
      this.accounts = [];
      this.selectedAccountIds = [];
    } finally {
      this.accountsLoading = false;
    }
  }

  isAccountSelected(dhanClientId: string): boolean {
    return this.selectedAccountIds.includes(dhanClientId);
  }

  setAccountSelected(dhanClientId: string, selected: boolean): void {
    this.selectedAccountIds = selected
      ? [...new Set([...this.selectedAccountIds, dhanClientId])]
      : this.selectedAccountIds.filter((id) => id !== dhanClientId);
  }

  selectAllAccounts(): void {
    this.selectedAccountIds = this.accounts.map((account) => account.dhanClientId);
  }

  clearAccountSelection(): void {
    this.selectedAccountIds = [];
  }

  async onExchangeChange(): Promise<void> {
    // Switching exchange invalidates any in-progress search against the old list.
    this.searchTerm = '';
    this.searchResults = [];
    this.showSuggestions = false;
    await this.loadInstruments();
  }

  private async loadInstruments(): Promise<void> {
    this.instrumentsLoading = true;
    this.instruments = [];
    try {
      this.instruments = (await this.api.getInstruments(this.selectedExchange)) || [];
      // The user may have typed a search term while the instrument list was
      // still loading; re-run the filter now that data is available.
      this.onSearchChange();
    } catch (err) {
      this.tradeStatusMessage = `Failed to load ${this.selectedExchange} instruments: ${(err as Error).message}`;
      this.tradeStatusIsError = true;
    } finally {
      this.instrumentsLoading = false;
    }
  }

  describeInstrument(i: FuturesInstrument): string {
    const expiry = i.expiryDate ? new Date(i.expiryDate).toLocaleDateString() : '';
    return `${i.underlyingSymbol || i.tradingSymbol} ${expiry ? `(${expiry})` : ''} - Lot ${i.lotSize}`.trim();
  }

  onSearchChange(): void {
    const term = this.searchTerm.trim().toLowerCase();
    if (!term) {
      this.searchResults = [];
      this.showSuggestions = false;
      return;
    }
    this.searchResults = this.instruments
      .filter(
        (i) =>
          (i.underlyingSymbol || '').toLowerCase().includes(term) ||
          (i.tradingSymbol || '').toLowerCase().includes(term) ||
          (i.displayName || '').toLowerCase().includes(term)
      )
      .slice(0, 20);
    this.showSuggestions = this.searchResults.length > 0;
  }

  selectInstrument(instrument: FuturesInstrument): void {
    this.rows.push({
      instrument,
      lots: 1,
      transactionType: 'BUY',
      orderType: 'MARKET',
      price: null,
      loadingPrice: false,
      selected: true
    });
    this.searchTerm = '';
    this.searchResults = [];
    this.showSuggestions = false;
  }

  removeRow(row: TradeRow): void {
    this.rows = this.rows.filter((r) => r !== row);
  }

  quantityFor(row: TradeRow): number {
    return (row.lots || 0) * row.instrument.lotSize;
  }

  setTransactionType(row: TradeRow, type: 'BUY' | 'SELL'): void {
    row.transactionType = type;
  }

  async setOrderType(row: TradeRow, type: 'MARKET' | 'LIMIT'): Promise<void> {
    row.orderType = type;
    if (type === 'MARKET') {
      row.price = null;
      return;
    }

    row.loadingPrice = true;
    try {
      const result = await this.api.getLtp(row.instrument.securityId, row.instrument.exchangeSegment || 'NSE_FNO');
      row.price = result.success && result.lastPrice != null ? result.lastPrice : null;
      if (!result.success) {
        this.tradeStatusMessage = `Could not fetch market price for ${row.instrument.tradingSymbol}: ${result.errorMessage}`;
        this.tradeStatusIsError = true;
      }
    } catch (err) {
      row.price = null;
      this.tradeStatusMessage = `Could not fetch market price for ${row.instrument.tradingSymbol}: ${(err as Error).message}`;
      this.tradeStatusIsError = true;
    } finally {
      row.loadingPrice = false;
    }
  }

  async placeAllOrders(): Promise<void> {
    if (this.rows.length === 0) {
      this.tradeStatusMessage = 'Add at least one instrument to the grid first.';
      this.tradeStatusIsError = true;
      return;
    }

    if (this.selectedAccountIds.length === 0) {
      this.tradeStatusMessage = 'Select at least one account to place orders.';
      this.tradeStatusIsError = true;
      return;
    }
    const accountClientIds = [...this.selectedAccountIds];

    const rowsToPlace = this.rows.filter((r) => r.selected);
    if (rowsToPlace.length === 0) {
      this.tradeStatusMessage = 'Select at least one instrument (checkbox) to place orders.';
      this.tradeStatusIsError = true;
      return;
    }

    for (const row of rowsToPlace) {
      if (!row.lots || row.lots <= 0) {
        this.tradeStatusMessage = `Lots must be greater than zero for ${row.instrument.tradingSymbol}.`;
        this.tradeStatusIsError = true;
        return;
      }
      if (row.orderType === 'LIMIT' && (!row.price || row.price <= 0)) {
        this.tradeStatusMessage = `Price must be greater than zero for ${row.instrument.tradingSymbol}.`;
        this.tradeStatusIsError = true;
        return;
      }
    }

    this.placingOrder = true;
    this.tradeStatusMessage = `Placing orders for ${accountClientIds.length} selected account(s)...`;
    this.tradeStatusIsError = false;
    this.orderResults = [];

    try {
      const allResults: OrderResultRow[] = [];
      for (const row of rowsToPlace) {
        const results = await this.api.placeOrder({
          accountClientIds,
          securityId: row.instrument.securityId,
          tradingSymbol: row.instrument.tradingSymbol,
          lotSize: row.instrument.lotSize,
          lots: row.lots,
          transactionType: row.transactionType,
          orderType: row.orderType,
          price: row.orderType === 'LIMIT' ? row.price : null
        });
        allResults.push(...(results || []).map((r) => ({ ...r, symbol: row.instrument.tradingSymbol })));
      }

      this.orderResults = allResults;
      this.tradeStatusMessage = `Orders submitted for ${rowsToPlace.length} instrument(s) across ${accountClientIds.length} selected account(s).`;
      this.tradeStatusIsError = false;

      // Remove only the rows that were just submitted; keep unselected ones in the grid.
      this.rows = this.rows.filter((r) => !r.selected);
    } catch (err) {
      this.tradeStatusMessage = `Failed to place orders: ${(err as Error).message}`;
      this.tradeStatusIsError = true;
    } finally {
      this.placingOrder = false;
    }
  }
}
