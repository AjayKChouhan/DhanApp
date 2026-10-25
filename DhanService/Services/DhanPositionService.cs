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
    }
}
