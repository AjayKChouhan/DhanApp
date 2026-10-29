using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public interface IDhanOrderService
    {
        /// <summary>Places the same BUY/SELL futures order across selected saved accounts, in parallel.</summary>
        Task<IReadOnlyList<DhanOrderResultDto>> PlaceOrderForAccountsAsync(
            PlaceDhanOrderRequest request, CancellationToken cancellationToken = default);

        Task<DhanOrderResultDto> ModifyOrderAsync(
            ModifyDhanOrderRequest request, CancellationToken cancellationToken = default);

        Task<DhanOrderResultDto> CancelOrderAsync(
            string dhanClientId, string orderId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Fetches the order book for one saved account from Dhan, reusing (or refreshing)
        /// its cached access token from the token store.
        /// </summary>
        Task<DhanOrdersResultDto> GetOrdersAsync(string dhanClientId, CancellationToken cancellationToken = default);
    }

    public class DhanOrderService : IDhanOrderService
    {
        private static readonly HashSet<string> ValidTransactionTypes = new(StringComparer.OrdinalIgnoreCase) { "BUY", "SELL" };
        private static readonly HashSet<string> ValidOrderTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "MARKET", "LIMIT", "STOP_LOSS", "STOP_LOSS_MARKET"
        };
        private static readonly HashSet<string> ValidProductTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "CNC", "INTRADAY", "MARGIN", "MTF", "CO", "BO"
        };

        private readonly HttpClient _httpClient;
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanAuthService _authService;
        private readonly IAngelOneInstrumentService _angelInstrumentService;
        private readonly IUpstoxInstrumentService _upstoxInstrumentService;
        private readonly DhanTradingOptions _options;
        private readonly ILogger<DhanOrderService> _logger;

        public DhanOrderService(
            HttpClient httpClient,
            IDhanAccountStore accountStore,
            IDhanAuthService authService,
            IAngelOneInstrumentService angelInstrumentService,
            IUpstoxInstrumentService upstoxInstrumentService,
            IOptions<DhanTradingOptions> options,
            ILogger<DhanOrderService> logger)
        {
            _httpClient = httpClient;
            _accountStore = accountStore;
            _authService = authService;
            _angelInstrumentService = angelInstrumentService;
            _upstoxInstrumentService = upstoxInstrumentService;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<IReadOnlyList<DhanOrderResultDto>> PlaceOrderForAccountsAsync(
            PlaceDhanOrderRequest request, CancellationToken cancellationToken = default)
        {
            if (!ValidTransactionTypes.Contains(request.TransactionType))
            {
                throw new ArgumentException("transactionType must be BUY or SELL.", nameof(request));
            }

            var orderType = string.IsNullOrWhiteSpace(request.OrderType) ? "MARKET" : request.OrderType;
            if (!ValidOrderTypes.Contains(orderType))
            {
                throw new ArgumentException("orderType must be MARKET, LIMIT, STOP_LOSS, or STOP_LOSS_MARKET.", nameof(request));
            }

            if ((string.Equals(orderType, "LIMIT", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(orderType, "STOP_LOSS", StringComparison.OrdinalIgnoreCase)) &&
                (request.Price is null || request.Price <= 0))
            {
                throw new ArgumentException("price must be greater than zero for LIMIT and STOP_LOSS orders.", nameof(request));
            }

            if ((string.Equals(orderType, "STOP_LOSS", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(orderType, "STOP_LOSS_MARKET", StringComparison.OrdinalIgnoreCase)) &&
                (request.TriggerPrice is null || request.TriggerPrice <= 0))
            {
                throw new ArgumentException("triggerPrice must be greater than zero for stop-loss orders.", nameof(request));
            }

            if (request.Lots <= 0 || request.LotSize <= 0)
            {
                throw new ArgumentException("lots and lotSize must be greater than zero.", nameof(request));
            }
            if (!ValidProductTypes.Contains(request.ProductType))
            {
                throw new ArgumentException("productType is not supported.", nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.ExchangeSegment))
            {
                throw new ArgumentException("exchangeSegment is required.", nameof(request));
            }
            if (!string.Equals(request.Validity, "DAY", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.Validity, "IOC", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("validity must be DAY or IOC.", nameof(request));
            }

            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            if (request.AccountClientIds is null || request.AccountClientIds.Count == 0)
            {
                throw new ArgumentException("Select at least one account.", nameof(request));
            }

            var requestedClientIds = new HashSet<string>(request.AccountClientIds, StringComparer.OrdinalIgnoreCase);
            var selectedAccounts = accounts.Where(account => requestedClientIds.Remove(account.DhanClientId)).ToList();
            if (requestedClientIds.Count > 0)
            {
                throw new ArgumentException("One or more selected accounts are not saved.", nameof(request));
            }

            if (selectedAccounts.Count == 0)
            {
                throw new ArgumentException("No selected saved accounts were found.", nameof(request));
            }

            var quantity = request.Lots * request.LotSize;
            var transactionType = request.TransactionType.ToUpperInvariant();
            var correlationSuffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            var tasks = selectedAccounts.Select(account =>
                PlaceOrderForAccountAsync(account, request, quantity, transactionType, orderType.ToUpperInvariant(), correlationSuffix, cancellationToken));

            return await Task.WhenAll(tasks);
        }

        public async Task<DhanOrderResultDto> ModifyOrderAsync(
            ModifyDhanOrderRequest request, CancellationToken cancellationToken = default)
        {
            var account = await FindAccountAsync(request.DhanClientId, cancellationToken);
            if (account is null)
            {
                return FailedOrderResult(request.DhanClientId, "No saved account found.");
            }

            try
            {
                if (IsAngelOne(account))
                {
                    return await ModifyAngelOneOrderAsync(account, request, cancellationToken);
                }
                if (IsUpstox(account))
                {
                    return await ModifyUpstoxOrderAsync(account, request, cancellationToken);
                }

                var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
                if (token is null)
                {
                    return FailedOrderResult(account.DhanClientId, "Could not obtain a valid access token (login failed).", account.AccountName);
                }

                var payload = new
                {
                    dhanClientId = account.DhanClientId,
                    orderId = request.OrderId,
                    orderType = request.OrderType,
                    quantity = request.Quantity.ToString(),
                    price = request.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    disclosedQuantity = "",
                    triggerPrice = request.TriggerPrice > 0
                        ? request.TriggerPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "",
                    validity = request.Validity
                };
                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Put,
                    $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/orders/{Uri.EscapeDataString(request.OrderId)}");
                httpRequest.Headers.Add("access-token", token.AccessToken);
                httpRequest.Content = JsonContent.Create(payload);

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Dhan order modification failed for {Account} ({ClientId}): {Status} {Body}",
                        account.AccountName, account.DhanClientId, response.StatusCode, body);
                    return FailedOrderResult(account.DhanClientId, $"{(int)response.StatusCode}: {body}", account.AccountName);
                }

                var parsed = JsonSerializer.Deserialize<PlaceOrderResponse>(
                    body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return new DhanOrderResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Success = true,
                    OrderId = parsed?.OrderId ?? request.OrderId,
                    OrderStatus = parsed?.OrderStatus
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unexpected error modifying {Broker} order {OrderId} for {ClientId}",
                    account.Broker, request.OrderId, request.DhanClientId);
                return FailedOrderResult(request.DhanClientId, ex.Message, account.AccountName, account.Broker);
            }
        }

        public async Task<DhanOrderResultDto> CancelOrderAsync(
            string dhanClientId, string orderId, CancellationToken cancellationToken = default)
        {
            var account = await FindAccountAsync(dhanClientId, cancellationToken);
            if (account is null)
            {
                return FailedOrderResult(dhanClientId, "No saved account found.");
            }

            try
            {
                if (IsAngelOne(account))
                {
                    return await CancelAngelOneOrderAsync(account, orderId, cancellationToken);
                }
                if (IsUpstox(account))
                {
                    return await CancelUpstoxOrderAsync(account, orderId, cancellationToken);
                }

                var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
                if (token is null)
                {
                    return FailedOrderResult(account.DhanClientId, "Could not obtain a valid access token (login failed).", account.AccountName);
                }

                using var request = new HttpRequestMessage(
                    HttpMethod.Delete,
                    $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/orders/{Uri.EscapeDataString(orderId)}");
                request.Headers.Add("access-token", token.AccessToken);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Dhan order cancellation failed for {Account} ({ClientId}): {Status} {Body}",
                        account.AccountName, account.DhanClientId, response.StatusCode, body);
                    return FailedOrderResult(account.DhanClientId, $"{(int)response.StatusCode}: {body}", account.AccountName);
                }

                var parsed = string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonSerializer.Deserialize<PlaceOrderResponse>(
                        body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return new DhanOrderResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Success = true,
                    OrderId = parsed?.OrderId ?? orderId,
                    OrderStatus = parsed?.OrderStatus ?? "CANCELLED"
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unexpected error cancelling {Broker} order {OrderId} for {ClientId}",
                    account.Broker, orderId, dhanClientId);
                return FailedOrderResult(dhanClientId, ex.Message, account.AccountName, account.Broker);
            }
        }

        public async Task<DhanOrdersResultDto> GetOrdersAsync(string dhanClientId, CancellationToken cancellationToken = default)
        {
            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            var account = accounts.FirstOrDefault(a => string.Equals(a.DhanClientId, dhanClientId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return new DhanOrdersResultDto
                {
                    DhanClientId = dhanClientId,
                    Success = false,
                    ErrorMessage = "No saved account found with this DhanClientId."
                };
            }

            try
            {
                if (IsAngelOne(account))
                {
                    return await GetAngelOneOrdersAsync(account, cancellationToken);
                }
                if (IsUpstox(account))
                {
                    return await GetUpstoxOrdersAsync(account, cancellationToken);
                }

                var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
                if (token is null)
                {
                    return new DhanOrdersResultDto
                    {
                        DhanClientId = dhanClientId,
                        Success = false,
                        ErrorMessage = "Could not obtain a valid access token (login failed)."
                    };
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/orders");
                request.Headers.Add("access-token", token.AccessToken);

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Dhan orders fetch failed for {ClientId}: {Status} {Body}",
                        dhanClientId, response.StatusCode, body);
                    return new DhanOrdersResultDto
                    {
                        DhanClientId = dhanClientId,
                        Success = false,
                        ErrorMessage = $"{(int)response.StatusCode}: {body}"
                    };
                }

                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
                return new DhanOrdersResultDto
                {
                    DhanClientId = dhanClientId,
                    Success = true,
                    Orders = document.RootElement.Clone()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error fetching Dhan orders for {ClientId}", dhanClientId);
                return new DhanOrdersResultDto
                {
                    DhanClientId = dhanClientId,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<DhanAccountCredential?> FindAccountAsync(
            string dhanClientId, CancellationToken cancellationToken)
        {
            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            return accounts.FirstOrDefault(account =>
                string.Equals(account.DhanClientId, dhanClientId, StringComparison.OrdinalIgnoreCase));
        }

        private static DhanOrderResultDto FailedOrderResult(
            string clientId, string error, string accountName = "", string broker = "DHAN") => new()
        {
            AccountName = accountName,
            DhanClientId = clientId,
            Broker = broker,
            Success = false,
            ErrorMessage = error
        };

        private async Task<DhanOrderResultDto> PlaceOrderForAccountAsync(
            DhanAccountCredential account,
            PlaceDhanOrderRequest request,
            int quantity,
            string transactionType,
            string orderType,
            string correlationSuffix,
            CancellationToken cancellationToken)
        {
            try
            {
                if (IsAngelOne(account))
                {
                    return await PlaceAngelOneOrderAsync(
                        account, request, quantity, transactionType, orderType, cancellationToken);
                }
                if (IsUpstox(account))
                {
                    return await PlaceUpstoxOrderAsync(
                        account, request, quantity, transactionType, orderType, cancellationToken);
                }

                var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
                if (token is null)
                {
                    return new DhanOrderResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Success = false,
                        ErrorMessage = "Could not obtain a valid access token (login failed)."
                    };
                }

                var correlationId = SanitizeCorrelationId($"{request.TradingSymbol}-{transactionType}-{correlationSuffix}-{account.DhanClientId}");
                if (!string.IsNullOrWhiteSpace(request.CorrelationId))
                {
                    correlationId = SanitizeCorrelationId(request.CorrelationId);
                    var existingOrder = await GetOrderByCorrelationIdAsync(account, token.AccessToken, correlationId, cancellationToken);
                    if (existingOrder is not null)
                    {
                        return new DhanOrderResultDto
                        {
                            AccountName = account.AccountName,
                            DhanClientId = account.DhanClientId,
                            Success = true,
                            OrderId = existingOrder.OrderId,
                            OrderStatus = existingOrder.OrderStatus
                        };
                    }
                }

                var isLimit = string.Equals(orderType, "LIMIT", StringComparison.OrdinalIgnoreCase);
                var isStopLossMarket = string.Equals(orderType, "STOP_LOSS_MARKET", StringComparison.OrdinalIgnoreCase);
                var orderPayload = new
                {
                    dhanClientId = account.DhanClientId,
                    correlationId,
                    transactionType,
                    exchangeSegment = request.ExchangeSegment,
                    productType = request.ProductType,
                    orderType,
                    validity = request.Validity,
                    securityId = request.SecurityId,
                    quantity = quantity.ToString(),
                    disclosedQuantity = "",
                    price = isLimit || string.Equals(orderType, "STOP_LOSS", StringComparison.OrdinalIgnoreCase)
                        ? request.Price!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "0",
                    triggerPrice = request.TriggerPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    afterMarketOrder = false
                };

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/orders");
                httpRequest.Headers.Add("access-token", token.AccessToken);
                httpRequest.Content = JsonContent.Create(orderPayload);

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Dhan order failed for {Account} ({ClientId}): {Status} {Body}",
                        account.AccountName, account.DhanClientId, response.StatusCode, body);
                    return new DhanOrderResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Success = false,
                        ErrorMessage = $"{(int)response.StatusCode}: {body}"
                    };
                }

                if (DhanApiResponse.HasErrorStatus(body))
                {
                    _logger.LogWarning(
                        "Dhan order returned an error payload for {Account} ({ClientId}): {Body}",
                        account.AccountName,
                        account.DhanClientId,
                        body);
                    return new DhanOrderResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Success = false,
                        ErrorMessage = $"Dhan returned an order error: {body}"
                    };
                }

                var parsed = JsonSerializer.Deserialize<PlaceOrderResponse>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (string.IsNullOrWhiteSpace(parsed?.OrderId))
                {
                    return new DhanOrderResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Success = false,
                        ErrorMessage = $"Dhan returned an invalid order response: {body}"
                    };
                }

                return new DhanOrderResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Success = true,
                    OrderId = parsed?.OrderId,
                    OrderStatus = parsed?.OrderStatus
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error placing {Broker} order for {Account} ({ClientId})",
                    account.Broker, account.AccountName, account.DhanClientId);
                return new DhanOrderResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Broker = account.Broker,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<DhanOrderResultDto> PlaceAngelOneOrderAsync(
            DhanAccountCredential account,
            PlaceDhanOrderRequest request,
            int quantity,
            string transactionType,
            string orderType,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Validity, "DAY", StringComparison.OrdinalIgnoreCase))
            {
                return AngelFailure(account, "Angel One futures orders currently support DAY validity only.");
            }

            var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
            if (token is null)
            {
                return AngelFailure(account, "Could not obtain a valid Angel One access token.");
            }

            var instrument = await _angelInstrumentService.ResolveFutureAsync(
                request.SecurityId, request.ExchangeSegment, cancellationToken);
            var angelQuantity = ConvertQuantityToAngelLots(quantity, instrument);
            var apiKey = _accountStore.UnprotectAngelApiKey(account);
            var orderTag = CreateAngelOrderTag(request.CorrelationId);
            if (orderTag is not null)
            {
                var existingOrders = await GetAngelOneOrdersAsync(account, cancellationToken);
                if (!existingOrders.Success)
                {
                    return AngelFailure(
                        account,
                        "Could not check the Angel One order book before submission; retry after order status is available.");
                }
                var existingOrder = existingOrders.Orders is { ValueKind: JsonValueKind.Array } orders
                    ? orders.EnumerateArray().FirstOrDefault(order =>
                        string.Equals(ReadJsonText(order, "correlationId"), orderTag, StringComparison.OrdinalIgnoreCase))
                    : default;
                if (existingOrder.ValueKind == JsonValueKind.Object)
                {
                    return new DhanOrderResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Broker = "ANGELONE",
                        Success = true,
                        OrderId = ReadJsonText(existingOrder, "orderId"),
                        OrderStatus = ReadJsonText(existingOrder, "orderStatus"),
                        BrokerTradingSymbol = instrument.TradingSymbol,
                        BrokerSecurityId = instrument.SecurityId,
                        BrokerQuantity = ReadJsonInteger(existingOrder, "quantity")
                    };
                }
            }

            var payload = new Dictionary<string, object?>
            {
                ["variety"] = "NORMAL",
                ["tradingsymbol"] = instrument.TradingSymbol,
                ["symboltoken"] = instrument.SecurityId,
                ["transactiontype"] = transactionType,
                ["exchange"] = instrument.Exchange,
                ["ordertype"] = MapAngelOrderType(orderType),
                ["producttype"] = MapAngelProductType(request.ProductType),
                ["duration"] = "DAY",
                ["price"] = IsPricedOrder(orderType)
                    ? (request.Price ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "0",
                ["squareoff"] = "0",
                ["stoploss"] = "0",
                ["quantity"] = angelQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["triggerprice"] = request.TriggerPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"
            };
            if (orderTag is not null)
            {
                payload["ordertag"] = orderTag;
            }

            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_options.AngelOneApiBaseUrl.TrimEnd('/')}/rest/secure/angelbroking/order/v1/placeOrder")
            {
                Content = JsonContent.Create(payload)
            };
            DhanAuthService.AddAngelAuthorizationHeaders(
                httpRequest, apiKey, token.AccessToken,
                await _authService.GetPublicIpAddressAsync(cancellationToken));

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Angel One order placement failed for {ClientId}: HTTP {Status}",
                    account.DhanClientId, response.StatusCode);
                return AngelFailure(account, $"{(int)response.StatusCode}: {ExtractAngelMessage(body)}");
            }

            using var document = JsonDocument.Parse(body);
            if (!IsAngelResponseSuccessful(document.RootElement))
            {
                return AngelFailure(account, ExtractAngelMessage(body));
            }

            var orderId = ReadJsonText(document.RootElement, "data", "orderid");
            if (string.IsNullOrWhiteSpace(orderId))
            {
                return AngelFailure(account, "Angel One accepted the request without returning an order ID.");
            }

            return new DhanOrderResultDto
            {
                AccountName = account.AccountName,
                DhanClientId = account.DhanClientId,
                Broker = "ANGELONE",
                Success = true,
                OrderId = orderId,
                OrderStatus = "TRANSIT",
                BrokerTradingSymbol = instrument.TradingSymbol,
                BrokerSecurityId = instrument.SecurityId,
                BrokerQuantity = angelQuantity
            };
        }

        private async Task<DhanOrderResultDto> ModifyAngelOneOrderAsync(
            DhanAccountCredential account,
            ModifyDhanOrderRequest request,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Validity, "DAY", StringComparison.OrdinalIgnoreCase))
            {
                return AngelFailure(account, "Angel One futures orders currently support DAY validity only.");
            }

            var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
            if (token is null)
            {
                return AngelFailure(account, "Could not obtain a valid Angel One access token.");
            }

            var instrument = !string.IsNullOrWhiteSpace(request.SecurityId) &&
                             !string.IsNullOrWhiteSpace(request.TradingSymbol)
                ? new AngelOneInstrument
                {
                    SecurityId = request.SecurityId,
                    TradingSymbol = request.TradingSymbol,
                    Exchange = MapAngelExchange(request.ExchangeSegment),
                    ProductType = request.ProductType,
                    TransactionType = request.TransactionType
                }
                : await FindAngelOrderInstrumentAsync(account, request.OrderId, cancellationToken);
            if (instrument is null)
            {
                return AngelFailure(account, "Could not identify the Angel One contract for the existing order.");
            }
            if (instrument.DhanLotSize <= 0)
            {
                instrument = await _angelInstrumentService.ResolveFutureAsync(
                    request.MasterSecurityId, request.ExchangeSegment, cancellationToken);
            }
            var angelQuantity = ConvertQuantityToAngelLots(request.Quantity, instrument);

            var apiKey = _accountStore.UnprotectAngelApiKey(account);
            var payload = new
            {
                variety = "NORMAL",
                orderid = request.OrderId,
                tradingsymbol = instrument.TradingSymbol,
                symboltoken = instrument.SecurityId,
                transactiontype = string.IsNullOrWhiteSpace(request.TransactionType)
                    ? instrument.TransactionType
                    : request.TransactionType,
                exchange = instrument.Exchange,
                ordertype = MapAngelOrderType(request.OrderType),
                producttype = MapAngelProductType(
                    string.IsNullOrWhiteSpace(request.ProductType) ? instrument.ProductType : request.ProductType),
                duration = "DAY",
                price = IsPricedOrder(request.OrderType) ? request.Price.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0",
                quantity = angelQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                triggerprice = request.TriggerPrice > 0
                    ? request.TriggerPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "0"
            };
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_options.AngelOneApiBaseUrl.TrimEnd('/')}/rest/secure/angelbroking/order/v1/modifyOrder")
            {
                Content = JsonContent.Create(payload)
            };
            DhanAuthService.AddAngelAuthorizationHeaders(
                httpRequest, apiKey, token.AccessToken,
                await _authService.GetPublicIpAddressAsync(cancellationToken));
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = CreateAngelOrderResult(account, request.OrderId, response, body);
            result.BrokerTradingSymbol = instrument.TradingSymbol;
            result.BrokerSecurityId = instrument.SecurityId;
            result.BrokerQuantity = angelQuantity;
            return result;
        }

        private async Task<DhanOrderResultDto> CancelAngelOneOrderAsync(
            DhanAccountCredential account, string orderId, CancellationToken cancellationToken)
        {
            var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
            if (token is null)
            {
                return AngelFailure(account, "Could not obtain a valid Angel One access token.");
            }

            var apiKey = _accountStore.UnprotectAngelApiKey(account);
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_options.AngelOneApiBaseUrl.TrimEnd('/')}/rest/secure/angelbroking/order/v1/cancelOrder")
            {
                Content = JsonContent.Create(new
                {
                    variety = "NORMAL",
                    orderid = orderId
                })
            };
            DhanAuthService.AddAngelAuthorizationHeaders(
                httpRequest, apiKey, token.AccessToken,
                await _authService.GetPublicIpAddressAsync(cancellationToken));
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return CreateAngelOrderResult(account, orderId, response, body, "CANCELLED");
        }

        private async Task<DhanOrderResultDto> PlaceUpstoxOrderAsync(
            DhanAccountCredential account,
            PlaceDhanOrderRequest request,
            int quantity,
            string transactionType,
            string orderType,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Validity, "DAY", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.Validity, "IOC", StringComparison.OrdinalIgnoreCase))
            {
                return UpstoxFailure(account, "Upstox supports DAY and IOC validity only.");
            }
            if (request.AfterMarketOrder || request.DisclosedQuantity > 0)
            {
                return UpstoxFailure(account, "Upstox futures copying does not support after-market or disclosed-quantity orders.");
            }

            var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
            if (token is null)
            {
                return UpstoxFailure(account, "Could not obtain a valid Upstox access token. Connect the account using OAuth.");
            }

            var instrument = await _upstoxInstrumentService.ResolveFutureAsync(
                request.SecurityId, request.ExchangeSegment, cancellationToken);
            var upstoxQuantity = ConvertQuantityToUpstoxUnits(quantity, instrument);
            var tag = CreateAngelOrderTag(request.CorrelationId);
            if (tag is not null)
            {
                var existingOrders = await GetUpstoxOrdersAsync(account, cancellationToken);
                if (!existingOrders.Success)
                {
                    return UpstoxFailure(
                        account,
                        "Could not check the Upstox order book before submission; retry after order status is available.");
                }

                var existingOrder = existingOrders.Orders is { ValueKind: JsonValueKind.Array } orders
                    ? orders.EnumerateArray().FirstOrDefault(order =>
                        string.Equals(ReadJsonText(order, "correlationId"), tag, StringComparison.OrdinalIgnoreCase))
                    : default;
                if (existingOrder.ValueKind == JsonValueKind.Object)
                {
                    return new DhanOrderResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Broker = "UPSTOX",
                        Success = true,
                        OrderId = ReadJsonText(existingOrder, "orderId"),
                        OrderStatus = ReadJsonText(existingOrder, "orderStatus"),
                        BrokerTradingSymbol = instrument.TradingSymbol,
                        BrokerSecurityId = instrument.InstrumentKey,
                        BrokerQuantity = ReadJsonInteger(existingOrder, "quantity")
                    };
                }
            }

            var payload = new Dictionary<string, object?>
            {
                ["quantity"] = upstoxQuantity,
                ["product"] = MapUpstoxProductType(request.ProductType),
                ["validity"] = request.Validity.ToUpperInvariant(),
                ["price"] = IsPricedOrder(orderType) ? request.Price ?? 0 : 0,
                ["instrument_token"] = instrument.InstrumentKey,
                ["order_type"] = MapUpstoxOrderType(orderType),
                ["transaction_type"] = transactionType,
                ["disclosed_quantity"] = 0,
                ["trigger_price"] = request.TriggerPrice ?? 0,
                ["is_amo"] = false
            };
            if (tag is not null)
            {
                payload["tag"] = tag;
            }

            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post, $"{_options.UpstoxApiBaseUrl.TrimEnd('/')}/v2/order/place")
            {
                Content = JsonContent.Create(payload)
            };
            AddUpstoxHeaders(httpRequest, token.AccessToken);
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = CreateUpstoxOrderResult(account, null, response, body);
            if (result.Success)
            {
                result.BrokerTradingSymbol = instrument.TradingSymbol;
                result.BrokerSecurityId = instrument.InstrumentKey;
                result.BrokerQuantity = upstoxQuantity;
            }
            return result;
        }

        private async Task<DhanOrderResultDto> ModifyUpstoxOrderAsync(
            DhanAccountCredential account,
            ModifyDhanOrderRequest request,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Validity, "DAY", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.Validity, "IOC", StringComparison.OrdinalIgnoreCase))
            {
                return UpstoxFailure(account, "Upstox supports DAY and IOC validity only.");
            }

            var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
            if (token is null)
            {
                return UpstoxFailure(account, "Could not obtain a valid Upstox access token. Connect the account using OAuth.");
            }

            var instrument = await _upstoxInstrumentService.ResolveFutureAsync(
                request.MasterSecurityId, request.ExchangeSegment, cancellationToken);
            var upstoxQuantity = ConvertQuantityToUpstoxUnits(request.Quantity, instrument);
            var payload = new
            {
                order_id = request.OrderId,
                quantity = upstoxQuantity,
                validity = request.Validity.ToUpperInvariant(),
                price = IsPricedOrder(request.OrderType) ? request.Price : 0,
                order_type = MapUpstoxOrderType(request.OrderType),
                disclosed_quantity = 0,
                trigger_price = request.TriggerPrice > 0 ? request.TriggerPrice : 0
            };
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Put, $"{_options.UpstoxApiBaseUrl.TrimEnd('/')}/v2/order/modify")
            {
                Content = JsonContent.Create(payload)
            };
            AddUpstoxHeaders(httpRequest, token.AccessToken);
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = CreateUpstoxOrderResult(account, request.OrderId, response, body);
            result.BrokerTradingSymbol = instrument.TradingSymbol;
            result.BrokerSecurityId = instrument.InstrumentKey;
            result.BrokerQuantity = upstoxQuantity;
            return result;
        }

        private async Task<DhanOrderResultDto> CancelUpstoxOrderAsync(
            DhanAccountCredential account, string orderId, CancellationToken cancellationToken)
        {
            var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
            if (token is null)
            {
                return UpstoxFailure(account, "Could not obtain a valid Upstox access token. Connect the account using OAuth.");
            }

            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Delete,
                $"{_options.UpstoxApiBaseUrl.TrimEnd('/')}/v2/order/cancel?order_id={Uri.EscapeDataString(orderId)}");
            AddUpstoxHeaders(httpRequest, token.AccessToken);
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return CreateUpstoxOrderResult(account, orderId, response, body, "CANCELLED");
        }

        private async Task<DhanOrdersResultDto> GetUpstoxOrdersAsync(
            DhanAccountCredential account, CancellationToken cancellationToken)
        {
            var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
            if (token is null)
            {
                return new DhanOrdersResultDto
                {
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = "Could not obtain a valid Upstox access token. Connect the account using OAuth."
                };
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{_options.UpstoxApiBaseUrl.TrimEnd('/')}/v2/order/retrieve-all");
            AddUpstoxHeaders(request, token.AccessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DhanOrdersResultDto
                {
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = $"{(int)response.StatusCode}: {ExtractUpstoxMessage(body)}"
                };
            }

            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.TryGetProperty("data", out var responseData)
                ? responseData
                : default;
            if (!string.Equals(ReadJsonText(document.RootElement, "status"), "success", StringComparison.OrdinalIgnoreCase) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return new DhanOrdersResultDto
                {
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = ExtractUpstoxMessage(body)
                };
            }

            var normalizedOrders = data.EnumerateArray().Select(order => MapUpstoxOrderToDhanShape(order)).ToList();
            using var normalizedDocument = JsonDocument.Parse(JsonSerializer.Serialize(normalizedOrders));
            return new DhanOrdersResultDto
            {
                DhanClientId = account.DhanClientId,
                Success = true,
                Orders = normalizedDocument.RootElement.Clone()
            };
        }

        private static void AddUpstoxHeaders(HttpRequestMessage request, string accessToken)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Api-Version", "2.0");
        }

        private static DhanOrderResultDto CreateUpstoxOrderResult(
            DhanAccountCredential account,
            string? orderId,
            HttpResponseMessage response,
            string body,
            string successStatus = "TRANSIT")
        {
            if (!response.IsSuccessStatusCode)
            {
                return UpstoxFailure(account, $"{(int)response.StatusCode}: {ExtractUpstoxMessage(body)}");
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (!string.Equals(ReadJsonText(document.RootElement, "status"), "success", StringComparison.OrdinalIgnoreCase))
                {
                    return UpstoxFailure(account, ExtractUpstoxMessage(body));
                }

                var returnedOrderId = ReadJsonText(document.RootElement, "data", "order_id") ?? orderId;
                if (string.IsNullOrWhiteSpace(returnedOrderId))
                {
                    return UpstoxFailure(account, "Upstox accepted the request without returning an order ID.");
                }

                return new DhanOrderResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Broker = "UPSTOX",
                    Success = true,
                    OrderId = returnedOrderId,
                    OrderStatus = successStatus
                };
            }
            catch (JsonException)
            {
                return UpstoxFailure(account, "Upstox returned an invalid order response.");
            }
        }

        private static object MapUpstoxOrderToDhanShape(JsonElement order)
        {
            var status = (ReadJsonText(order, "status") ?? string.Empty).ToUpperInvariant();
            return new
            {
                orderId = ReadJsonText(order, "order_id") ?? string.Empty,
                orderStatus = status switch
                {
                    "COMPLETE" or "COMPLETED" => "TRADED",
                    "OPEN" or "PENDING" or "TRIGGER PENDING" => "PENDING",
                    "PARTIALLY FILLED" or "PARTIAL" => "PART_TRADED",
                    "CANCELLED" or "CANCELED" => "CANCELLED",
                    "REJECTED" => "REJECTED",
                    _ => status
                },
                transactionType = ReadJsonText(order, "transaction_type") ?? string.Empty,
                exchangeSegment = ReadJsonText(order, "instrument_token")?.Split('|').FirstOrDefault() switch
                {
                    "NSE_FO" => "NSE_FNO",
                    "BSE_FO" => "BSE_FNO",
                    "MCX_FO" => "MCX_COMM",
                    var segment => segment ?? string.Empty
                },
                productType = ReadJsonText(order, "product") switch
                {
                    "D" => "MARGIN",
                    "I" => "INTRADAY",
                    var product => product ?? string.Empty
                },
                tradingSymbol = ReadJsonText(order, "trading_symbol") ?? string.Empty,
                securityId = ReadJsonText(order, "instrument_token") ?? string.Empty,
                quantity = ReadJsonInteger(order, "quantity"),
                price = ReadJsonDecimal(order, "price"),
                orderType = ReadJsonText(order, "order_type") switch
                {
                    "SL" => "STOP_LOSS",
                    "SL-M" => "STOP_LOSS_MARKET",
                    var type => type ?? string.Empty
                },
                validity = ReadJsonText(order, "validity") ?? string.Empty,
                triggerPrice = ReadJsonDecimal(order, "trigger_price"),
                correlationId = ReadJsonText(order, "tag") ?? string.Empty,
                reasonDescription = ReadJsonText(order, "status_message") ?? string.Empty
            };
        }

        private static string MapUpstoxOrderType(string orderType) =>
            orderType.ToUpperInvariant() switch
            {
                "MARKET" => "MARKET",
                "LIMIT" => "LIMIT",
                "STOP_LOSS" => "SL",
                "STOP_LOSS_MARKET" => "SL-M",
                _ => throw new ArgumentException($"Upstox does not support order type '{orderType}'.")
            };

        private static string MapUpstoxProductType(string productType) =>
            productType.ToUpperInvariant() switch
            {
                "MARGIN" => "D",
                "INTRADAY" => "I",
                _ => throw new ArgumentException($"Upstox futures does not support product type '{productType}'.")
            };

        private static int ConvertQuantityToUpstoxUnits(int dhanQuantity, UpstoxInstrument instrument)
        {
            if (dhanQuantity <= 0 || instrument.DhanLotSize <= 0 || instrument.LotSize <= 0)
            {
                throw new InvalidOperationException("The master and child futures lot sizes must be positive.");
            }
            if (dhanQuantity % instrument.DhanLotSize != 0)
            {
                throw new InvalidOperationException(
                    $"Master order quantity {dhanQuantity} is not a whole number of Dhan lots ({instrument.DhanLotSize}).");
            }

            return checked((dhanQuantity / instrument.DhanLotSize) * instrument.LotSize);
        }

        private static string ExtractUpstoxMessage(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    var firstError = errors.EnumerateArray().FirstOrDefault();
                    var message = ReadJsonText(firstError, "message");
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }
                return ReadJsonText(root, "message") ?? "Upstox returned an unsuccessful response.";
            }
            catch (JsonException)
            {
                return "Upstox returned an invalid response.";
            }
        }

        private static bool IsUpstox(DhanAccountCredential account) =>
            string.Equals(account.Broker, "UPSTOX", StringComparison.OrdinalIgnoreCase);

        private static DhanOrderResultDto UpstoxFailure(DhanAccountCredential account, string error) => new()
        {
            AccountName = account.AccountName,
            DhanClientId = account.DhanClientId,
            Broker = "UPSTOX",
            Success = false,
            ErrorMessage = error
        };

        private async Task<DhanOrdersResultDto> GetAngelOneOrdersAsync(
            DhanAccountCredential account, CancellationToken cancellationToken)
        {
            var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
            if (token is null)
            {
                return new DhanOrdersResultDto
                {
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = "Could not obtain a valid Angel One access token."
                };
            }

            var apiKey = _accountStore.UnprotectAngelApiKey(account);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_options.AngelOneApiBaseUrl.TrimEnd('/')}/rest/secure/angelbroking/order/v1/getOrderBook");
            DhanAuthService.AddAngelAuthorizationHeaders(
                request, apiKey, token.AccessToken,
                await _authService.GetPublicIpAddressAsync(cancellationToken));
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DhanOrdersResultDto
                {
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = $"{(int)response.StatusCode}: {ExtractAngelMessage(body)}"
                };
            }

            using var document = JsonDocument.Parse(body);
            if (!IsAngelResponseSuccessful(document.RootElement))
            {
                return new DhanOrdersResultDto
                {
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = ExtractAngelMessage(body)
                };
            }
            var sourceOrders = document.RootElement.TryGetProperty("data", out var data) &&
                               data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Select(order => MapAngelOrderToDhanShape(order, account.DhanClientId)).ToList()
                : new List<object>();
            using var normalizedDocument = JsonDocument.Parse(JsonSerializer.Serialize(sourceOrders));
            return new DhanOrdersResultDto
            {
                DhanClientId = account.DhanClientId,
                Success = true,
                Orders = normalizedDocument.RootElement.Clone()
            };
        }

        private async Task<AngelOneInstrument?> FindAngelOrderInstrumentAsync(
            DhanAccountCredential account, string orderId, CancellationToken cancellationToken)
        {
            var ordersResult = await GetAngelOneOrdersAsync(account, cancellationToken);
            if (!ordersResult.Success || ordersResult.Orders is not { ValueKind: JsonValueKind.Array } orders)
            {
                return null;
            }

            foreach (var order in orders.EnumerateArray())
            {
                if (!string.Equals(ReadJsonText(order, "orderId"), orderId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return new AngelOneInstrument
                {
                    TradingSymbol = ReadJsonText(order, "tradingSymbol") ?? string.Empty,
                    SecurityId = ReadJsonText(order, "securityId") ?? string.Empty,
                    Exchange = MapAngelExchangeFromDhan(ReadJsonText(order, "exchangeSegment") ?? string.Empty),
                    ProductType = ReadJsonText(order, "productType") ?? string.Empty,
                    TransactionType = ReadJsonText(order, "transactionType") ?? string.Empty
                };
            }
            return null;
        }

        private DhanOrderResultDto CreateAngelOrderResult(
            DhanAccountCredential account,
            string orderId,
            HttpResponseMessage response,
            string body,
            string successStatus = "TRANSIT")
        {
            if (!response.IsSuccessStatusCode)
            {
                return AngelFailure(account, $"{(int)response.StatusCode}: {ExtractAngelMessage(body)}");
            }

            using var document = JsonDocument.Parse(body);
            if (!IsAngelResponseSuccessful(document.RootElement))
            {
                return AngelFailure(account, ExtractAngelMessage(body));
            }

            return new DhanOrderResultDto
            {
                AccountName = account.AccountName,
                DhanClientId = account.DhanClientId,
                Broker = "ANGELONE",
                Success = true,
                OrderId = ReadJsonText(document.RootElement, "data", "orderid") ?? orderId,
                OrderStatus = successStatus
            };
        }

        private static string MapAngelOrderType(string orderType) =>
            orderType.ToUpperInvariant() switch
            {
                "MARKET" => "MARKET",
                "LIMIT" => "LIMIT",
                "STOP_LOSS" => "STOPLOSS_LIMIT",
                "STOP_LOSS_MARKET" => "STOPLOSS_MARKET",
                _ => throw new ArgumentException($"Angel One does not support order type '{orderType}'.")
            };

        private static string MapAngelProductType(string productType) =>
            productType.ToUpperInvariant() switch
            {
                "MARGIN" => "CARRYFORWARD",
                "INTRADAY" => "INTRADAY",
                _ => throw new ArgumentException($"Angel One futures does not support product type '{productType}'.")
            };

        private static string MapAngelExchange(string exchangeSegment) =>
            exchangeSegment.ToUpperInvariant() switch
            {
                "NSE_FNO" => "NFO",
                "BSE_FNO" => "BFO",
                "MCX_COMM" => "MCX",
                _ => throw new ArgumentException($"Angel One does not support exchange segment '{exchangeSegment}'.")
            };

        private static string MapAngelExchangeFromDhan(string exchangeSegment) =>
            exchangeSegment.ToUpperInvariant() switch
            {
                "NSE_FNO" => "NFO",
                "BSE_FNO" => "BFO",
                "MCX_COMM" => "MCX",
                _ => string.Empty
            };

        private static bool IsPricedOrder(string orderType) =>
            orderType.Equals("LIMIT", StringComparison.OrdinalIgnoreCase) ||
            orderType.Equals("STOP_LOSS", StringComparison.OrdinalIgnoreCase);

        private static bool IsAngelResponseSuccessful(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("status", out var status) &&
            (status.ValueKind == JsonValueKind.True ||
             (status.ValueKind == JsonValueKind.String &&
              string.Equals(status.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

        private static string ExtractAngelMessage(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return ReadJsonText(document.RootElement, "message") ??
                       ReadJsonText(document.RootElement, "errorcode") ??
                       "Angel One returned an unsuccessful response.";
            }
            catch (JsonException)
            {
                return "Angel One returned an invalid response.";
            }
        }

        private static string? ReadJsonText(JsonElement root, string propertyName) =>
            root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string? ReadJsonText(JsonElement root, string objectProperty, string propertyName) =>
            root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(objectProperty, out var nested)
                ? ReadJsonText(nested, propertyName)
                : null;

        private static object MapAngelOrderToDhanShape(JsonElement order, string clientId)
        {
            var exchange = ReadJsonText(order, "exchange") ?? string.Empty;
            var status = (ReadJsonText(order, "orderstatus") ?? string.Empty).ToUpperInvariant();
            return new
            {
                dhanClientId = clientId,
                orderId = ReadJsonText(order, "orderid") ?? string.Empty,
                orderStatus = status switch
                {
                    "COMPLETE" => "TRADED",
                    "OPEN" or "TRIGGER PENDING" or "OPEN PENDING" or "VALIDATION PENDING" => "PENDING",
                    "PARTIALLY EXECUTED" or "PARTIAL" => "PART_TRADED",
                    "CANCELLED" => "CANCELLED",
                    "REJECTED" => "REJECTED",
                    _ => status
                },
                transactionType = ReadJsonText(order, "transactiontype") ?? string.Empty,
                exchangeSegment = exchange switch
                {
                    "NFO" => "NSE_FNO",
                    "BFO" => "BSE_FNO",
                    "MCX" => "MCX_COMM",
                    _ => exchange
                },
                productType = ReadJsonText(order, "producttype") switch
                {
                    "CARRYFORWARD" => "MARGIN",
                    "INTRADAY" => "INTRADAY",
                    var product => product ?? string.Empty
                },
                tradingSymbol = ReadJsonText(order, "tradingsymbol") ?? string.Empty,
                securityId = ReadJsonText(order, "symboltoken") ?? string.Empty,
                quantity = ReadJsonInteger(order, "quantity"),
                price = ReadJsonDecimal(order, "price"),
                createTime = ReadJsonText(order, "updatetime") ?? string.Empty,
                updateTime = ReadJsonText(order, "updatetime") ?? string.Empty,
                averageTradedPrice = ReadJsonDecimal(order, "averageprice"),
                filledQty = ReadJsonInteger(order, "filledshares"),
                correlationId = ReadJsonText(order, "ordertag") ?? string.Empty,
                reasonDescription = ReadJsonText(order, "text") ?? ReadJsonText(order, "rejreason") ?? string.Empty
            };
        }

        private static string? CreateAngelOrderTag(string? correlationId)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                return null;
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(correlationId));
            return Convert.ToHexString(hash)[..20];
        }

        private static int ConvertQuantityToAngelLots(int dhanQuantity, AngelOneInstrument instrument)
        {
            if (dhanQuantity <= 0 || instrument.DhanLotSize <= 0 || instrument.LotSize <= 0)
            {
                throw new InvalidOperationException("The master and child futures lot sizes must be positive.");
            }

            if (dhanQuantity % instrument.DhanLotSize != 0)
            {
                throw new InvalidOperationException(
                    $"Master order quantity {dhanQuantity} is not a whole number of Dhan lots ({instrument.DhanLotSize}).");
            }

            var lots = dhanQuantity / instrument.DhanLotSize;
            return checked(lots * instrument.LotSize);
        }

        private static int ReadJsonInteger(JsonElement root, string propertyName)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value))
            {
                return 0;
            }
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number
                : int.TryParse(value.ToString(), out number) ? number : 0;
        }

        private static decimal ReadJsonDecimal(JsonElement root, string propertyName)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value))
            {
                return 0;
            }
            return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
                ? number
                : decimal.TryParse(
                    value.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out number) ? number : 0;
        }

        private static bool IsAngelOne(DhanAccountCredential account) =>
            string.Equals(account.Broker, "ANGELONE", StringComparison.OrdinalIgnoreCase);

        private static DhanOrderResultDto AngelFailure(DhanAccountCredential account, string error) => new()
        {
            AccountName = account.AccountName,
            DhanClientId = account.DhanClientId,
            Broker = "ANGELONE",
            Success = false,
            ErrorMessage = error
        };

        private async Task<PlaceOrderResponse?> GetOrderByCorrelationIdAsync(
            DhanAccountCredential account, string accessToken, string correlationId, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/orders/external/{Uri.EscapeDataString(correlationId)}");
            request.Headers.Add("access-token", accessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Could not verify copy-order correlation ID ({(int)response.StatusCode}): {body}",
                    null,
                    response.StatusCode);
            }

            var existingOrder = JsonSerializer.Deserialize<PlaceOrderResponse>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (existingOrder is null || string.IsNullOrWhiteSpace(existingOrder.OrderId))
            {
                throw new InvalidDataException("Dhan returned an invalid response while checking the copy-order correlation ID.");
            }
            return existingOrder;
        }

        private static string SanitizeCorrelationId(string raw)
        {
            var cleaned = new string(raw.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-').ToArray());
            return cleaned.Length <= 30 ? cleaned : cleaned[^30..];
        }

        private class PlaceOrderResponse
        {
            [JsonPropertyName("orderId")]
            public string? OrderId { get; set; }

            [JsonPropertyName("orderStatus")]
            public string? OrderStatus { get; set; }
        }
    }
}
