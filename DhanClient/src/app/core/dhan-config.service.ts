import { Injectable } from '@angular/core';

// Shared configuration for the DhanClient app. Values are persisted to
// localStorage so they survive page navigation/reloads.
const BASE_URL_KEY = 'dhanai.apiBaseUrl';
const API_KEY_KEY = 'dhanai.apiKey';
const DEFAULT_BASE_URL = 'http://localhost:5050/api/dhantrading';

@Injectable({
  providedIn: 'root'
})
export class DhanConfigService {
  readonly defaultBaseUrl = DEFAULT_BASE_URL;

  getBaseUrl(): string {
    return localStorage.getItem(BASE_URL_KEY) || DEFAULT_BASE_URL;
  }

  setBaseUrl(value: string): void {
    localStorage.setItem(BASE_URL_KEY, value || DEFAULT_BASE_URL);
  }

  getApiKey(): string {
    return localStorage.getItem(API_KEY_KEY) || '';
  }

  setApiKey(value: string): void {
    localStorage.setItem(API_KEY_KEY, value || '');
  }
}
