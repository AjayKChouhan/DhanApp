using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public interface IDhanMarketQuoteService
    {
        /// <summary>
        /// Fetches the last traded price for one instrument using any saved account
        /// with a valid (or freshly refreshed) access token.
        /// </summary>
        Task<DhanLtpResultDto> GetLtpAsync(string securityId, string exchangeSegment, CancellationToken cancellationToken = default);
    }

    /// <summary>Calls Dhan's Market Quote "ticker data" API to fetch a last traded price (LTP).</summary>
    public class DhanMarketQuoteService : IDhanMarketQuoteService
    {
        private readonly HttpClient _httpClient;
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanAuthService _authService;
        private readonly DhanTradingOptions _options;
        private readonly ILogger<DhanMarketQuoteService> _logger;

        public DhanMarketQuoteService(
            HttpClient httpClient,
            IDhanAccountStore accountStore,
            IDhanAuthService authService,
            IOptions<DhanTradingOptions> options,
            ILogger<DhanMarketQuoteService> logger)
        {
            _httpClient = httpClient;
            _accountStore = accountStore;
            _authService = authService;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<DhanLtpResultDto> GetLtpAsync(string securityId, string exchangeSegment, CancellationToken cancellationToken = default)
        {
            if (!long.TryParse(securityId, out var numericSecurityId))
            {
                return new DhanLtpResultDto
                {
                    SecurityId = securityId,
                    ExchangeSegment = exchangeSegment,
                    Success = false,
                    ErrorMessage = "securityId must be numeric."
                };
            }

            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            if (accounts.Count == 0)
            {
                return new DhanLtpResultDto
                {
                    SecurityId = securityId,
                    ExchangeSegment = exchangeSegment,
                    Success = false,
                    ErrorMessage = "No saved accounts available to fetch quotes with."
                };
            }

            string? lastError = null;

            foreach (var account in accounts)
            {
                var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
                if (token is null)
                {
                    continue;
                }

                try
                {
                    var payload = new Dictionary<string, long[]>
                    {
                        [exchangeSegment] = new[] { numericSecurityId }
                    };

                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/marketfeed/ltp");
                    request.Headers.Add("access-token", token.AccessToken);
                    request.Headers.Add("client-id", account.DhanClientId);
                    request.Content = JsonContent.Create(payload);

                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = ExtractDhanErrorMessage(body) ?? $"Dhan returned {(int)response.StatusCode} {response.StatusCode}";
                        _logger.LogWarning("Dhan LTP fetch failed via {ClientId}: {Status} {Body}",
                            account.DhanClientId, response.StatusCode, body);
                        continue;
                    }

                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty(exchangeSegment, out var segmentData) &&
                        segmentData.TryGetProperty(securityId, out var instrumentData) &&
                        instrumentData.TryGetProperty("last_price", out var lastPriceElement))
                    {
                        return new DhanLtpResultDto
                        {
                            SecurityId = securityId,
                            ExchangeSegment = exchangeSegment,
                            Success = true,
                            LastPrice = lastPriceElement.GetDecimal()
                        };
                    }

                    return new DhanLtpResultDto
                    {
                        SecurityId = securityId,
                        ExchangeSegment = exchangeSegment,
                        Success = false,
                        ErrorMessage = "Unexpected response shape from Dhan market quote API."
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error fetching Dhan LTP via {ClientId}", account.DhanClientId);
                }
            }

            return new DhanLtpResultDto
            {
                SecurityId = securityId,
                ExchangeSegment = exchangeSegment,
                Success = false,
                ErrorMessage = lastError ?? "Could not fetch a quote (no account had a valid token or all requests failed)."
            };
        }

        /// <summary>Pulls a human-readable message out of Dhan's error response shape, e.g. {"data":{"806":"Data APIs not Subscribed"},"status":"failed"}.</summary>
        private static string? ExtractDhanErrorMessage(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var property in data.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            return property.Value.GetString();
                        }
                    }
                }

                if (document.RootElement.TryGetProperty("remarks", out var remarks) && remarks.ValueKind == JsonValueKind.String)
                {
                    return remarks.GetString();
                }
            }
            catch (JsonException)
            {
                // Body wasn't JSON (or wasn't the expected shape) - fall back to the generic message.
            }

            return null;
        }
    }
}
