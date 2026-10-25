// Shape of Dhan API responses is loosely typed since the backend forwards
// Dhan's raw JSON payloads as-is (fields vary by endpoint).
export interface DhanAccountStatus {
  accountName: string;
  dhanClientId: string;
  isConnected: boolean;
  tokenExpiryUtc?: string | null;
}

export interface DhanAccountInput {
  accountName: string;
  dhanClientId: string;
  pin: string;
  totpSecret: string;
}

export interface DhanLoginResult {
  accountName: string;
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
  lotSize: number;
  expiryDate?: string | null;
  exchangeSegment?: string;
}

export interface PlaceOrderRequest {
  accountClientIds: string[];
  securityId: string;
  tradingSymbol: string;
  lotSize: number;
  lots: number;
  transactionType: 'BUY' | 'SELL';
  orderType: 'MARKET' | 'LIMIT';
  price?: number | null;
}

export interface OrderResult {
  accountName: string;
  dhanClientId: string;
  success: boolean;
  orderId?: string | null;
  orderStatus?: string | null;
  errorMessage?: string | null;
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
