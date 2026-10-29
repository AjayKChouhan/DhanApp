// Shape of Dhan API responses is loosely typed since the backend forwards
// Dhan's raw JSON payloads as-is (fields vary by endpoint).
export interface DhanAccountStatus {
  accountName: string;
  broker?: 'DHAN' | 'ANGELONE' | string;
  dhanClientId: string;
  isConnected: boolean;
  tokenExpiryUtc?: string | null;
}

export interface DhanAccountInput {
  accountName: string;
  broker: 'DHAN' | 'ANGELONE' | 'UPSTOX';
  dhanClientId: string;
  clientCode?: string;
  pin: string;
  totpSecret: string;
  password?: string;
  apiKey?: string;
  apiSecret?: string;
}

export interface DhanLoginResult {
  accountName: string;
  broker?: 'DHAN' | 'ANGELONE' | string;
  dhanClientId: string;
  success: boolean;
  expiryUtc?: string | null;
  errorMessage?: string | null;
}

export interface DhanAccountResult<T> {
  success: boolean;
  errorMessage?: string | null;
  [key: string]: unknown;
}

export interface FuturesInstrument {
  securityId: string;
  tradingSymbol: string;
  underlyingSymbol?: string;
  displayName?: string;
  instrumentType?: string;
  lotSize: number;
  expiryDate?: string | null;
  exchangeSegment?: string;
}

export interface PlaceOrderRequest {
  accountClientIds: string[];
  correlationId?: string;
  securityId: string;
  tradingSymbol: string;
  exchangeSegment?: string;
  productType?: string;
  validity?: string;
  lotSize: number;
  lots: number;
  transactionType: 'BUY' | 'SELL';
  orderType: 'MARKET' | 'LIMIT' | 'STOP_LOSS' | 'STOP_LOSS_MARKET';
  price?: number | null;
  triggerPrice?: number | null;
}

export interface OrderResult {
  accountName: string;
  dhanClientId: string;
  broker?: string;
  success: boolean;
  orderId?: string | null;
  orderStatus?: string | null;
  errorMessage?: string | null;
}

export interface CopyTradingChildSetting {
  dhanClientId: string;
  enabled: boolean;
}

export interface CopyTradingSettings {
  enabled: boolean;
  masterClientId: string | null;
  children: CopyTradingChildSetting[];
}

export interface CopyTradingChildOrder {
  dhanClientId: string;
  accountName: string;
  correlationId: string;
  copyState: 'READY' | 'SUBMITTING' | 'SUBMITTED' | 'FAILED' | string;
  orderId?: string | null;
  orderStatus?: string | null;
  errorMessage?: string | null;
}

export interface CopyTradingOrder {
  masterClientId: string;
  masterAccountName: string;
  masterOrderId: string;
  tradingSymbol: string;
  securityId: string;
  exchangeSegment: string;
  productType: string;
  transactionType: string;
  orderType: string;
  validity: string;
  quantity: number;
  price: number;
  triggerPrice: number;
  masterOrderStatus: string;
  masterOrderErrorMessage?: string | null;
  source: string;
  receivedAtUtc: string;
  children: CopyTradingChildOrder[];
}

export interface CopyTradingDashboard {
  settings: CopyTradingSettings;
  accounts: DhanAccountStatus[];
  orders: CopyTradingOrder[];
  listenerConnected: boolean;
  listenerMessage?: string | null;
}

// Raw shape of a single entry from Dhan's /v2/positions response
// (see https://dhanhq.co/docs/v2/portfolio/#positions).
export interface DhanPosition {
  dhanClientId: string;
  tradingSymbol: string;
  securityId: string;
  positionType: 'LONG' | 'SHORT' | 'CLOSED';
  exchangeSegment: string;
  productType: string;
  buyAvg: number;
  buyQty: number;
  costPrice: number;
  sellAvg: number;
  sellQty: number;
  netQty: number;
  realizedProfit: number;
  unrealizedProfit: number;
}

// Raw shape of a single entry from Dhan's /v2/orders response
// (see https://dhanhq.co/docs/v2/orders/#order-book).
export interface DhanOrder {
  dhanClientId: string;
  orderId: string;
  orderStatus: string;
  transactionType: 'BUY' | 'SELL';
  exchangeSegment: string;
  productType: string;
  tradingSymbol: string;
  securityId: string;
  quantity: number;
  price: number;
  createTime: string;
  updateTime: string;
  averageTradedPrice: number;
  filledQty: number;
}

// Result of fetching the last traded price for one instrument
// (see https://dhanhq.co/docs/v2/market-quote/#ticker-data).
export interface DhanLtpResult {
  securityId: string;
  exchangeSegment: string;
  success: boolean;
  lastPrice?: number | null;
  errorMessage?: string | null;
}

export interface MarketScannerInstrument {
  securityId: string;
  tradingSymbol: string;
  underlyingSymbol: string;
  lastUpdated: string;
  lastPrice: number;
  vwap: number;
  ema20: number;
  ema50: number;
  rsi14: number;
  macdHistogram: number;
  atr14: number;
  sessionVolume: number;
  averageVolume: number;
  previousHigh: number;
  previousLow: number;
  historySessions: number;
  relativeStrengthPercent: number | null;
  averageDailyTurnover: number;
  longRiskReward: number;
  shortRiskReward: number;
  isLiquid: boolean;
  longMarketTrend: boolean;
  shortMarketTrend: boolean;
  longRelativeStrength: boolean;
  shortRelativeStrength: boolean;
  longEmaAlignment: boolean;
  shortEmaAlignment: boolean;
  longAboveVwap: boolean;
  shortBelowVwap: boolean;
  longAboveEma20: boolean;
  shortBelowEma20: boolean;
  longAboveEma50: boolean;
  shortBelowEma50: boolean;
  longRsiConfirmation: boolean;
  shortRsiConfirmation: boolean;
  volumeConfirmation: boolean;
  longBreakout: boolean;
  shortBreakdown: boolean;
  longPullbackConfirmation: boolean;
  shortPullbackConfirmation: boolean;
  longEntryTrigger: number;
  shortEntryTrigger: number;
  longStopLoss: number;
  shortStopLoss: number;
  longTarget: number;
  shortTarget: number;
  longMacdConfirmation: boolean;
  shortMacdConfirmation: boolean;
}

export interface MarketScannerResponse {
  generatedAt: string;
  instrumentCount: number;
  scannedCount: number;
  failedCount: number;
  warning?: string | null;
  instruments: MarketScannerInstrument[];
}

export interface BreakoutSignal {
  securityId: string;
  tradingSymbol: string;
  underlyingSymbol: string;
  direction: 'LONG' | 'SHORT';
  candleTime: string;
  priceChangePercent: number;
  close: number;
  previousClose: number;
}

export interface BreakoutScannerResponse {
  lastScannedAt: string | null;
  instrumentCount: number;
  scannedCount: number;
  failedCount: number;
  warning?: string | null;
  signals: BreakoutSignal[];
}

export interface DhanSetIpResult {
  accountName: string;
  dhanClientId: string;
  success: boolean;
  errorMessage?: string | null;
}

export interface DhanIpStatus {
  accountName: string;
  dhanClientId: string;
  success: boolean;
  primaryIp?: string | null;
  secondaryIp?: string | null;
  modifyDatePrimary?: string | null;
  modifyDateSecondary?: string | null;
  errorMessage?: string | null;
}
