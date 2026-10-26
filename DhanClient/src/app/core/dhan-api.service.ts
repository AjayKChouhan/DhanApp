import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { catchError, firstValueFrom } from 'rxjs';
import { DhanConfigService } from './dhan-config.service';
import {
  DhanAccountInput,
  DhanAccountStatus,
  DhanIpStatus,
  DhanLoginResult,
  DhanLtpResult,
  DhanSetIpResult,
  CopyTradingDashboard,
  CopyTradingSettings,
  FuturesInstrument,
  OrderResult,
  PlaceOrderRequest
} from './models';

// Thin HTTP wrapper that talks to the DhanAPI backend.
@Injectable({
  providedIn: 'root'
})
export class DhanApiService {
  constructor(private readonly http: HttpClient, private readonly config: DhanConfigService) {}

  private headers(): HttpHeaders {
    let headers = new HttpHeaders({ 'Content-Type': 'application/json' });
    const apiKey = this.config.getApiKey();
    if (apiKey) {
      headers = headers.set('X-Dhan-Api-Key', apiKey);
    }
    return headers;
  }

  private toErrorMessage(err: HttpErrorResponse): Error {
    const body = err.error;
    const message = (body && body.title) || (typeof body === 'string' ? body : null) || `Request failed (${err.status})`;
    return new Error(message);
  }

  private get<T>(path: string): Promise<T> {
    return firstValueFrom(
      this.http.get<T>(`${this.config.getBaseUrl()}${path}`, { headers: this.headers() }).pipe(
        catchError((err) => {
          throw this.toErrorMessage(err);
        })
      )
    );
  }

  private getText(path: string): Promise<string> {
    return firstValueFrom(
      this.http
        .get(`${this.config.getBaseUrl()}${path}`, { headers: this.headers(), responseType: 'text' })
        .pipe(
          catchError((err) => {
            throw this.toErrorMessage(err);
          })
        )
    );
  }

  private post<T>(path: string, body?: unknown): Promise<T> {
    return firstValueFrom(
      this.http.post<T>(`${this.config.getBaseUrl()}${path}`, body ?? null, { headers: this.headers() }).pipe(
        catchError((err) => {
          throw this.toErrorMessage(err);
        })
      )
    );
  }


  getAccountsStatus(): Promise<DhanAccountStatus[]> {
    return this.get<DhanAccountStatus[]>('/accounts/status');
  }

  saveAccounts(accounts: DhanAccountInput[]): Promise<void> {
    return this.post<void>('/accounts', { accounts });
  }

  login(): Promise<DhanLoginResult[]> {
    return this.post<DhanLoginResult[]>('/login');
  }

  getPositions(dhanClientId: string): Promise<any> {
    return this.get(`/accounts/${encodeURIComponent(dhanClientId)}/positions`);
  }

  getOrders(dhanClientId: string): Promise<any> {
    return this.get(`/accounts/${encodeURIComponent(dhanClientId)}/orders`);
  }

  getInstruments(exchange: string = 'NSE'): Promise<FuturesInstrument[]> {
    return this.get<FuturesInstrument[]>(`/instruments?exchange=${encodeURIComponent(exchange)}`);
  }

  getLtp(securityId: string, exchangeSegment: string): Promise<DhanLtpResult> {
    return this.get<DhanLtpResult>(`/instruments/${encodeURIComponent(securityId)}/ltp?exchangeSegment=${encodeURIComponent(exchangeSegment)}`);
  }

  placeOrder(order: PlaceOrderRequest): Promise<OrderResult[]> {
    return this.post<OrderResult[]>('/order', order);
  }

  getCopyTradingDashboard(): Promise<CopyTradingDashboard> {
    return this.get<CopyTradingDashboard>('/copy-trading');
  }

  saveCopyTradingSettings(settings: CopyTradingSettings): Promise<void> {
    return this.post<void>('/copy-trading/settings', settings);
  }

  retryCopyOrder(masterClientId: string, masterOrderId: string, childClientId: string): Promise<void> {
    return this.post<void>(
      `/copy-trading/orders/${encodeURIComponent(masterClientId)}/${encodeURIComponent(masterOrderId)}/children/${encodeURIComponent(childClientId)}/retry`
    );
  }

  setStaticIp(ip: string, ipFlag: 'PRIMARY' | 'SECONDARY'): Promise<DhanSetIpResult[]> {
    return this.post<DhanSetIpResult[]>('/accounts/set-ip', { ip, ipFlag });
  }

  getMyIp(): Promise<string> {
    return this.getText('/accounts/my-ip');
  }

  getIpStatus(): Promise<DhanIpStatus[]> {
    return this.get<DhanIpStatus[]>('/accounts/ip-status');
  }
}
