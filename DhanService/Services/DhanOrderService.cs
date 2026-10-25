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

        /// <summary>
        /// Fetches the order book for one saved account from Dhan, reusing (or refreshing)
        /// its cached access token from the token store.
        /// </summary>
        Task<DhanOrdersResultDto> GetOrdersAsync(string dhanClientId, CancellationToken cancellationToken = default);
    }

    public class DhanOrderService : IDhanOrderService
    {
        private static readonly HashSet<string> ValidTransactionTypes = new(StringComparer.OrdinalIgnoreCase) { "BUY", "SELL" };
        private static readonly HashSet<string> ValidOrderTypes = new(StringComparer.OrdinalIgnoreCase) { "MARKET", "LIMIT" };

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
                throw new ArgumentException("orderType must be MARKET or LIMIT.", nameof(request));
            }

            if (string.Equals(orderType, "LIMIT", StringComparison.OrdinalIgnoreCase) && (request.Price is null || request.Price <= 0))
            {
                throw new ArgumentException("price must be greater than zero for LIMIT orders.", nameof(request));
            }

            if (request.Lots <= 0 || request.LotSize <= 0)
            {
                throw new ArgumentException("lots and lotSize must be greater than zero.", nameof(request));
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

                var isLimit = string.Equals(orderType, "LIMIT", StringComparison.OrdinalIgnoreCase);
                var orderPayload = new
                {
                    dhanClientId = account.DhanClientId,
                    correlationId,
                    transactionType,
                    exchangeSegment = "NSE_FNO",
                    productType = "MARGIN",
                    orderType,
                    validity = "DAY",
                    securityId = request.SecurityId,
                    quantity = quantity.ToString(),
                    disclosedQuantity = "",
                    price = isLimit ? request.Price!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0",
                    triggerPrice = "",
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

                var parsed = JsonSerializer.Deserialize<PlaceOrderResponse>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

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
