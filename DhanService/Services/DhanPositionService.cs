using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public interface IDhanPositionService
    {
        /// <summary>
        /// Fetches open positions for one saved account from Dhan, reusing (or refreshing)
        /// its cached access token from the token store.
        /// </summary>
        Task<DhanPositionsResultDto> GetPositionsAsync(string dhanClientId, CancellationToken cancellationToken = default);
    }

    public class DhanPositionService : IDhanPositionService
    {
        private readonly HttpClient _httpClient;
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanAuthService _authService;
        private readonly DhanTradingOptions _options;
        private readonly ILogger<DhanPositionService> _logger;

        public DhanPositionService(
            HttpClient httpClient,
            IDhanAccountStore accountStore,
            IDhanAuthService authService,
            IOptions<DhanTradingOptions> options,
            ILogger<DhanPositionService> logger)
        {
            _httpClient = httpClient;
            _accountStore = accountStore;
            _authService = authService;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<DhanPositionsResultDto> GetPositionsAsync(string dhanClientId, CancellationToken cancellationToken = default)
        {
            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            var account = accounts.FirstOrDefault(a => string.Equals(a.DhanClientId, dhanClientId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return new DhanPositionsResultDto
                {
                    DhanClientId = dhanClientId,
                    Success = false,
                    ErrorMessage = "No saved account found with this DhanClientId."
                };
            }

            try
            {
                if (string.Equals(account.Broker, "ANGELONE", StringComparison.OrdinalIgnoreCase))
                {
                    return await GetAngelOnePositionsAsync(account, cancellationToken);
                }

                var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
                if (token is null)
                {
                    return new DhanPositionsResultDto
                    {
                        DhanClientId = dhanClientId,
                        Success = false,
                        ErrorMessage = "Could not obtain a valid access token (login failed)."
                    };
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/positions");
                request.Headers.Add("access-token", token.AccessToken);

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Dhan positions fetch failed for {ClientId}: {Status} {Body}",
                        dhanClientId, response.StatusCode, body);
                    return new DhanPositionsResultDto
                    {
                        DhanClientId = dhanClientId,
                        Success = false,
                        ErrorMessage = $"{(int)response.StatusCode}: {body}"
                    };
                }

                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
                return new DhanPositionsResultDto
                {
                    DhanClientId = dhanClientId,
                    Success = true,
                    Positions = document.RootElement.Clone()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error fetching Dhan positions for {ClientId}", dhanClientId);
                return new DhanPositionsResultDto
                {
                    DhanClientId = dhanClientId,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<DhanPositionsResultDto> GetAngelOnePositionsAsync(
            DhanAccountCredential account, CancellationToken cancellationToken)
        {
            var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
            if (token is null)
            {
                return new DhanPositionsResultDto
                {
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = "Could not obtain a valid Angel One access token."
                };
            }

            var apiKey = _accountStore.UnprotectAngelApiKey(account);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_options.AngelOneApiBaseUrl.TrimEnd('/')}/rest/secure/angelbroking/order/v1/getPosition");
            DhanAuthService.AddAngelAuthorizationHeaders(
                request, apiKey, token.AccessToken,
                await _authService.GetPublicIpAddressAsync(cancellationToken));

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DhanPositionsResultDto
                {
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = $"{(int)response.StatusCode}: {body}"
                };
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind == JsonValueKind.False)
            {
                var message = document.RootElement.TryGetProperty("message", out var error)
                    ? error.GetString()
                    : "Angel One returned an unsuccessful positions response.";
                return new DhanPositionsResultDto
                {
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = message
                };
            }

            var angelPositions = document.RootElement.TryGetProperty("data", out var data) &&
                                 data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Select(position => MapAngelPositionToDhanShape(position, account.DhanClientId)).ToList()
                : new List<object>();
            using var normalized = JsonDocument.Parse(JsonSerializer.Serialize(angelPositions));
            return new DhanPositionsResultDto
            {
                DhanClientId = account.DhanClientId,
                Success = true,
                Positions = normalized.RootElement.Clone()
            };
        }

        private static object MapAngelPositionToDhanShape(JsonElement position, string clientId)
        {
            var netQuantity = ReadInteger(position, "netqty");
            var exchange = ReadText(position, "exchange");
            return new
            {
                dhanClientId = clientId,
                tradingSymbol = ReadText(position, "tradingsymbol") ?? string.Empty,
                securityId = ReadText(position, "symboltoken") ?? string.Empty,
                positionType = netQuantity > 0 ? "LONG" : netQuantity < 0 ? "SHORT" : "CLOSED",
                exchangeSegment = exchange switch
                {
                    "NFO" => "NSE_FNO",
                    "BFO" => "BSE_FNO",
                    "MCX" => "MCX_COMM",
                    "NSE" => "NSE_EQ",
                    "BSE" => "BSE_EQ",
                    _ => exchange ?? string.Empty
                },
                productType = ReadText(position, "producttype") switch
                {
                    "CARRYFORWARD" => "MARGIN",
                    var product => product ?? string.Empty
                },
                buyAvg = ReadDecimal(position, "buyavgprice"),
                buyQty = ReadInteger(position, "buyqty"),
                costPrice = ReadDecimal(position, "avgnetprice"),
                sellAvg = ReadDecimal(position, "sellavgprice"),
                sellQty = ReadInteger(position, "sellqty"),
                netQty = netQuantity,
                realizedProfit = ReadDecimal(position, "realised"),
                unrealizedProfit = ReadDecimal(position, "unrealised")
            };
        }

        private static string? ReadText(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property)
                ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString()
                : null;

        private static int ReadInteger(JsonElement element, string propertyName) =>
            int.TryParse(ReadText(element, propertyName), out var value) ? value : 0;

        private static decimal ReadDecimal(JsonElement element, string propertyName) =>
            decimal.TryParse(
                ReadText(element, propertyName),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) ? value : 0;
    }
}
