import { Component, OnInit } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DhanApiService } from '../../core/dhan-api.service';
import { DhanIpStatus, DhanSetIpResult } from '../../core/models';

@Component({
  selector: 'app-ip-setup',
  imports: [FormsModule, DatePipe],
  templateUrl: './ip-setup.component.html'
})
export class IpSetupComponent implements OnInit {
  ip = '';
  ipFlag: 'PRIMARY' | 'SECONDARY' = 'PRIMARY';

  detecting = false;
  detectError = '';

  setting = false;
  setStatusMessage = '';
  setStatusIsError = false;
  setResults: DhanSetIpResult[] = [];

  statusLoading = false;
  statusError = '';
  statusRows: DhanIpStatus[] = [];

  constructor(private readonly api: DhanApiService) {}

  ngOnInit(): void {
    this.detectMyIp();
    this.refreshIpStatus();
  }

  async detectMyIp(): Promise<void> {
    this.detecting = true;
    this.detectError = '';
    try {
      this.ip = await this.api.getMyIp();
    } catch (err) {
      this.detectError = `Could not auto-detect your IP: ${(err as Error).message}. You can type it manually below.`;
    } finally {
      this.detecting = false;
    }
  }

  async setStaticIpForAll(): Promise<void> {
    if (!this.ip.trim()) {
      this.setStatusMessage = 'Could not determine an IP address. Click "Detect My IP" or enter one manually.';
      this.setStatusIsError = true;
      return;
    }

    this.setting = true;
    this.setStatusMessage = 'Setting IP across all accounts...';
    this.setStatusIsError = false;
    this.setResults = [];

    try {
      this.setResults = (await this.api.setStaticIp(this.ip.trim(), this.ipFlag)) || [];
      const failed = this.setResults.filter((r) => !r.success).length;
      if (failed === 0) {
        this.setStatusMessage = `IP set successfully for all ${this.setResults.length} account(s).`;
        this.setStatusIsError = false;
      } else {
        this.setStatusMessage = `${this.setResults.length - failed} succeeded, ${failed} failed. See results below (often means the 7-day lock is active - check "when next I can set" in IP Status).`;
        this.setStatusIsError = true;
      }
      await this.refreshIpStatus();
    } catch (err) {
      this.setStatusMessage = `Failed to set IP: ${(err as Error).message}`;
      this.setStatusIsError = true;
    } finally {
      this.setting = false;
    }
  }

  async refreshIpStatus(): Promise<void> {
    this.statusLoading = true;
    this.statusError = '';
    try {
      this.statusRows = (await this.api.getIpStatus()) || [];
    } catch (err) {
      this.statusError = `Failed to load IP status: ${(err as Error).message}`;
      this.statusRows = [];
    } finally {
      this.statusLoading = false;
    }
  }
}
