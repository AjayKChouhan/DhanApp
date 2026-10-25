import { Component, OnInit } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { DhanApiService } from '../../core/dhan-api.service';
import { DhanAccountStatus, DhanPosition } from '../../core/models';
import { extractArray } from '../../core/extract-array';

// A single row ready for display in the positions grid, derived from Dhan's
// raw position payload (which has no direct LTP/% change field).
interface PositionRow {
  side: 'BUY' | 'SELL';
  symbol: string;
  product: string;
  quantity: number;
  avgPrice: number;
  ltp: number | null;
  pnl: number;
  pctChange: number | null;
}

interface AccountBlock {
  account: DhanAccountStatus;
  success: boolean;
  errorMessage?: string;
  rows: PositionRow[];
}

function toPositionRow(p: DhanPosition): PositionRow | null {
  if (!p || p.netQty === 0 || p.positionType === 'CLOSED') {
    return null;
  }

  const side: 'BUY' | 'SELL' = p.positionType === 'SHORT' ? 'SELL' : 'BUY';
  const quantity = Math.abs(p.netQty);
  const avgPrice = p.costPrice;
  // Dhan's positions API doesn't return LTP directly; derive it from
  // unrealizedProfit = (ltp - costPrice) * netQty.
  const ltp = avgPrice ? avgPrice + p.unrealizedProfit / p.netQty : null;
  const pctChange = ltp !== null && avgPrice ? ((ltp - avgPrice) / avgPrice) * 100 : null;

  return {
    side,
    symbol: p.tradingSymbol,
    product: p.productType,
    quantity,
    avgPrice,
    ltp,
    pnl: p.unrealizedProfit,
    pctChange
  };
}

@Component({
  selector: 'app-positions',
  imports: [DecimalPipe],
  templateUrl: './positions.component.html'
})
export class PositionsComponent implements OnInit {
  loading = false;
  statusMessage = '';
  statusIsError = false;
  blocks: AccountBlock[] = [];
  noAccounts = false;

  constructor(private readonly api: DhanApiService) {}

  ngOnInit(): void {
    this.loadPositions();
  }

  async loadPositions(): Promise<void> {
    this.loading = true;
    this.statusMessage = '';
    this.statusIsError = false;
    this.blocks = [];
    this.noAccounts = false;

    try {
      const accounts = (await this.api.getAccountsStatus()) || [];
      if (accounts.length === 0) {
        this.noAccounts = true;
        return;
      }

      this.blocks = await Promise.all(
        accounts.map(async (account): Promise<AccountBlock> => {
          try {
            const result = await this.api.getPositions(account.dhanClientId);
            const positions = extractArray(result?.positions) as unknown as DhanPosition[];
            const rows = positions.map(toPositionRow).filter((r): r is PositionRow => r !== null);
            return { account, success: true, rows };
          } catch (err) {
            return { account, success: false, errorMessage: (err as Error).message, rows: [] };
          }
        })
      );
    } catch (err) {
      this.statusMessage = `Failed to load positions: ${(err as Error).message}`;
      this.statusIsError = true;
    } finally {
      this.loading = false;
    }
  }
}
