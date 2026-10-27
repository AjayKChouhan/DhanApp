using System.Net.Http.Json;
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
        private readonly DhanTradingOptions _options;
        private readonly ILogger<DhanOrderService> _logger;

        public DhanOrderService(
            HttpClient httpClient,
            IDhanAccountStore accountStore,
            IDhanAuthService authService,
            IOptions<DhanTradingOptions> options,
            ILogger<DhanOrderService> logger)
        {
            _httpClient = httpClient;
            _accountStore = accountStore;
            _authService = authService;
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
                _logger.LogError(ex, "Unexpected error modifying Dhan order {OrderId} for {ClientId}",
                    request.OrderId, request.DhanClientId);
                return FailedOrderResult(request.DhanClientId, ex.Message, account.AccountName);
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
                _logger.LogError(ex, "Unexpected error cancelling Dhan order {OrderId} for {ClientId}", orderId, dhanClientId);
                return FailedOrderResult(dhanClientId, ex.Message, account.AccountName);
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
            string clientId, string error, string accountName = "") => new()
        {
            AccountName = accountName,
            DhanClientId = clientId,
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
                _logger.LogError(ex, "Unexpected error placing Dhan order for {Account} ({ClientId})", account.AccountName, account.DhanClientId);
                return new DhanOrderResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

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
