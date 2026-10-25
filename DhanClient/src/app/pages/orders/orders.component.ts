import { Component, OnInit } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { DhanApiService } from '../../core/dhan-api.service';
import { DhanAccountStatus, DhanOrder } from '../../core/models';
import { extractArray } from '../../core/extract-array';

interface OrderRow {
  time: string;
  side: 'BUY' | 'SELL';
  symbol: string;
  product: string;
  quantity: number;
  price: number;
  ltp: number | null;
  status: string;
}

interface AccountBlock {
  account: DhanAccountStatus;
  success: boolean;
  errorMessage?: string;
  rows: OrderRow[];
}

function toOrderRow(o: DhanOrder): OrderRow | null {
  if (!o) {
    return null;
  }
  return {
    time: o.createTime,
    side: o.transactionType,
    symbol: o.tradingSymbol,
    product: o.productType,
    quantity: o.quantity,
    price: o.price,
    ltp: o.filledQty > 0 ? o.averageTradedPrice : null,
    status: o.orderStatus
  };
}

@Component({
  selector: 'app-orders',
  imports: [DecimalPipe],
  templateUrl: './orders.component.html'
})
export class OrdersComponent implements OnInit {
  loading = false;
  statusMessage = '';
  statusIsError = false;
  blocks: AccountBlock[] = [];
  noAccounts = false;

  constructor(private readonly api: DhanApiService) {}

  ngOnInit(): void {
    this.loadOrders();
  }

  async loadOrders(): Promise<void> {
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
            const result = await this.api.getOrders(account.dhanClientId);
            const orders = extractArray(result?.orders) as unknown as DhanOrder[];
            const rows = orders.map(toOrderRow).filter((r): r is OrderRow => r !== null);
            return { account, success: true, rows };
          } catch (err) {
            return { account, success: false, errorMessage: (err as Error).message, rows: [] };
          }
        })
      );
    } catch (err) {
      this.statusMessage = `Failed to load orders: ${(err as Error).message}`;
      this.statusIsError = true;
    } finally {
      this.loading = false;
    }
  }
}

