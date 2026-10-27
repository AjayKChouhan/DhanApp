import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CopyTradingDashboard, CopyTradingOrder, CopyTradingSettings, DhanAccountStatus } from '../../core/models';
import { DhanApiService } from '../../core/dhan-api.service';

interface ChildAccountRow {
  account: DhanAccountStatus;
  enabled: boolean;
}

@Component({
  selector: 'app-copy-trading',
  imports: [FormsModule, DatePipe, DecimalPipe],
  templateUrl: './copy-trading.component.html'
})
export class CopyTradingComponent implements OnInit, OnDestroy {
  accounts: DhanAccountStatus[] = [];
  childRows: ChildAccountRow[] = [];
  orders: CopyTradingOrder[] = [];
  masterClientId = '';
  enabled = false;
  listenerConnected = false;
  listenerMessage = '';
  loading = false;
  saving = false;
  settingsDirty = false;
  statusMessage = '';
  statusIsError = false;
  private refreshTimer: ReturnType<typeof setInterval> | null = null;

  constructor(private readonly api: DhanApiService) {}

  get enabledChildCount(): number {
    return this.childRows.filter((row) => row.enabled).length;
  }

  get masterAccounts(): DhanAccountStatus[] {
    return this.accounts.filter((account) => account.broker?.toUpperCase() === 'DHAN');
  }

  get childAccounts(): DhanAccountStatus[] {
    return this.accounts.filter((account) => account.dhanClientId !== this.masterClientId);
  }

  get canConfigure(): boolean {
    return this.masterAccounts.length > 0 && this.accounts.length > 1;
  }

  ngOnInit(): void {
    void this.loadDashboard();
    this.refreshTimer = setInterval(() => void this.loadDashboard(), 5000);
  }

  ngOnDestroy(): void {
    if (this.refreshTimer) {
      clearInterval(this.refreshTimer);
    }
  }

  async loadDashboard(showLoading = true): Promise<void> {
    if (showLoading) {
      this.loading = true;
    }
    try {
      const dashboard = await this.api.getCopyTradingDashboard();
      this.applyDashboard(dashboard);
      if (showLoading || this.statusMessage.startsWith('Failed to load copy-trading status:')) {
        this.statusMessage = '';
        this.statusIsError = false;
      }
    } catch (err) {
      this.statusMessage = `Failed to load copy-trading status: ${(err as Error).message}`;
      this.statusIsError = true;
    } finally {
      this.loading = false;
    }
  }

  onMasterChange(): void {
    this.settingsDirty = true;
    this.childRows = this.childAccounts
      .map((account) => ({
        account,
        enabled: this.childRows.find((row) => row.account.dhanClientId === account.dhanClientId)?.enabled ?? false
      }));
  }

  markSettingsDirty(): void {
    this.settingsDirty = true;
  }

  async saveSettings(): Promise<void> {
    if (!this.masterClientId) {
      this.statusMessage = 'Select a master account.';
      this.statusIsError = true;
      return;
    }
    if (this.enabled && !this.childRows.some((row) => row.enabled)) {
      this.statusMessage = 'Enable at least one child account before turning on copy trading.';
      this.statusIsError = true;
      return;
    }

    const settings: CopyTradingSettings = {
      enabled: this.enabled,
      masterClientId: this.masterClientId,
      children: this.childRows.map((row) => ({
        dhanClientId: row.account.dhanClientId,
        enabled: row.enabled
      }))
    };

    this.saving = true;
    this.statusMessage = '';
    this.statusIsError = false;
    try {
      await this.api.saveCopyTradingSettings(settings);
      this.settingsDirty = false;
      this.statusMessage = this.enabled
        ? 'Copy-trading settings saved. The order listener is connecting.'
        : 'Copy trading is paused. Your settings have been saved.';
      await this.loadDashboard(false);
    } catch (err) {
      this.statusMessage = `Failed to save settings: ${(err as Error).message}`;
      this.statusIsError = true;
    } finally {
      this.saving = false;
    }
  }

  async retryChildOrder(order: CopyTradingOrder, childClientId: string): Promise<void> {
    try {
      await this.api.retryCopyOrder(order.masterClientId, order.masterOrderId, childClientId);
      this.statusMessage = 'Retry submitted. Refreshing the child order status.';
      this.statusIsError = false;
      await this.loadDashboard(false);
    } catch (err) {
      this.statusMessage = `Could not retry child order: ${(err as Error).message}`;
      this.statusIsError = true;
    }
  }

  childStatusClass(copyState: string, orderStatus?: string | null): string {
    if (copyState === 'FAILED' || orderStatus?.toUpperCase() === 'REJECTED') {
      return 'copy-status-failed';
    }
    if (copyState === 'SUBMITTED') {
      return 'copy-status-submitted';
    }
    if (copyState === 'SKIPPED') {
      return 'copy-status-skipped';
    }
    return 'copy-status-pending';
  }

  trackOrder(_: number, order: CopyTradingOrder): string {
    return `${order.masterClientId}-${order.masterOrderId}`;
  }

  private applyDashboard(dashboard: CopyTradingDashboard): void {
    const previousChildSettings = new Map(this.childRows.map((row) => [row.account.dhanClientId, row.enabled]));
    this.accounts = dashboard.accounts || [];
    if (!this.settingsDirty) {
      this.masterClientId = dashboard.settings?.masterClientId || '';
      this.enabled = dashboard.settings?.enabled || false;
      this.childRows = this.childAccounts
        .map((account) => ({
          account,
          enabled: dashboard.settings?.children?.find((child) => child.dhanClientId === account.dhanClientId)?.enabled
            ?? previousChildSettings.get(account.dhanClientId)
            ?? false
        }));
    } else {
      this.childRows = this.childRows.map((row) => ({
        ...row,
        account: this.accounts.find((account) => account.dhanClientId === row.account.dhanClientId) ?? row.account
      }));
    }
    this.orders = dashboard.orders || [];
    this.listenerConnected = dashboard.listenerConnected;
    this.listenerMessage = dashboard.listenerMessage || '';
  }
}
