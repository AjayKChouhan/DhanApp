import { Component, OnInit } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DhanApiService } from '../../core/dhan-api.service';
import { DhanAccountInput, DhanAccountStatus, DhanLoginResult } from '../../core/models';

interface AccountRow {
  accountName: string;
  broker: 'DHAN' | 'ANGELONE';
  dhanClientId: string;
  clientCode: string;
  pin: string;
  password: string;
  apiKey: string;
  totpSecret: string;
}

@Component({
  selector: 'app-login',
  imports: [FormsModule, DatePipe],
  templateUrl: './login.component.html'
})
export class LoginComponent implements OnInit {
  accounts: AccountRow[] = [];
  saving = false;
  saveStatusMessage = '';
  saveStatusIsError = false;

  statusRows: DhanAccountStatus[] = [];
  statusLoading = false;
  statusError = '';

  loggingIn = false;
  loginResults: DhanLoginResult[] = [];
  loginError = '';

  constructor(private readonly api: DhanApiService) {}

  ngOnInit(): void {
    this.loadExistingAccounts();
    this.refreshStatus();
  }

  addRow(): void {
    this.accounts.push({
      accountName: '',
      broker: 'DHAN',
      dhanClientId: '',
      clientCode: '',
      pin: '',
      password: '',
      apiKey: '',
      totpSecret: ''
    });
  }

  removeRow(index: number): void {
    this.accounts.splice(index, 1);
  }

  private async loadExistingAccounts(): Promise<void> {
    try {
      const existing = await this.api.getAccountsStatus();
      if (Array.isArray(existing) && existing.length > 0) {
        this.accounts = existing.map((a) => ({
          accountName: a.accountName,
          broker: a.broker?.toUpperCase() === 'ANGELONE' ? 'ANGELONE' : 'DHAN',
          dhanClientId: a.dhanClientId,
          clientCode: a.broker?.toUpperCase() === 'ANGELONE' ? a.dhanClientId : '',
          pin: '',
          password: '',
          apiKey: '',
          totpSecret: ''
        }));
        return;
      }
    } catch {
      // Ignore - likely no accounts saved yet / API unreachable.
    }
    this.addRow();
  }

  async saveAccounts(): Promise<void> {
    const payload: DhanAccountInput[] = this.accounts
      .map((a) => ({
        accountName: a.accountName.trim(),
        broker: a.broker,
        dhanClientId: a.broker === 'DHAN' ? a.dhanClientId.trim() : '',
        clientCode: a.broker === 'ANGELONE' ? a.clientCode.trim() : '',
        pin: a.pin.trim(),
        password: a.password.trim(),
        apiKey: a.apiKey.trim(),
        totpSecret: a.totpSecret.trim()
      }))
      .filter((a) => a.broker === 'DHAN' ? !!a.dhanClientId : !!a.clientCode);

    if (payload.length === 0) {
      this.saveStatusMessage = 'Add at least one Dhan or Angel One account.';
      this.saveStatusIsError = true;
      return;
    }

    this.saving = true;
    this.saveStatusMessage = 'Saving...';
    this.saveStatusIsError = false;
    try {
      await this.api.saveAccounts(payload);
      this.saveStatusMessage = `Saved ${payload.length} account(s).`;
      this.saveStatusIsError = false;
      await this.refreshStatus();
    } catch (err) {
      this.saveStatusMessage = `Failed to save accounts: ${(err as Error).message}`;
      this.saveStatusIsError = true;
    } finally {
      this.saving = false;
    }
  }

  async refreshStatus(): Promise<void> {
    this.statusLoading = true;
    this.statusError = '';
    try {
      this.statusRows = (await this.api.getAccountsStatus()) || [];
    } catch (err) {
      this.statusError = `Failed to load status: ${(err as Error).message}`;
      this.statusRows = [];
    } finally {
      this.statusLoading = false;
    }
  }

  async loginAll(): Promise<void> {
    this.loggingIn = true;
    this.loginError = '';
    this.loginResults = [];
    try {
      this.loginResults = (await this.api.login()) || [];
      await this.refreshStatus();
    } catch (err) {
      this.loginError = `Login failed: ${(err as Error).message}`;
    } finally {
      this.loggingIn = false;
    }
  }
}
