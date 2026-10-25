using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OtpNet;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public interface IDhanAuthService
    {
        /// <summary>Computes TOTP, calls Dhan's generateAccessToken, and caches the resulting token.</summary>
        Task<DhanLoginResultDto> LoginAccountAsync(DhanAccountCredential account, CancellationToken cancellationToken = default);

        /// <summary>Logs in every saved account.</summary>
        Task<IReadOnlyList<DhanLoginResultDto>> LoginAllAsync(CancellationToken cancellationToken = default);

        /// <summary>Returns the cached token for the account, refreshing it first if it's missing/near expiry.</summary>
        Task<Token?> EnsureValidTokenAsync(DhanAccountCredential account, CancellationToken cancellationToken = default);

        /// <summary>Sets/updates the static IP (required for order placement) for every saved account.</summary>
        Task<IReadOnlyList<DhanSetIpResultDto>> SetStaticIpForAllAccountsAsync(string ip, string ipFlag, CancellationToken cancellationToken = default);

        /// <summary>Fetches the currently-configured static IP(s) and next-allowed-change date for every saved account.</summary>
        Task<IReadOnlyList<DhanIpStatusResultDto>> GetStaticIpStatusForAllAccountsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Detects this server's own current public/outbound IP address - i.e. the IP Dhan will see
        /// when this app places orders - so the user doesn't have to look it up and type it manually.
        /// </summary>
        Task<string> GetPublicIpAddressAsync(CancellationToken cancellationToken = default);
    }

    public class DhanAuthService : IDhanAuthService
    {
        private readonly HttpClient _httpClient;
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanTokenStore _tokenStore;
        private readonly DhanTradingOptions _options;
        private readonly ILogger<DhanAuthService> _logger;

        public DhanAuthService(
            HttpClient httpClient,
            IDhanAccountStore accountStore,
            IDhanTokenStore tokenStore,
            IOptions<DhanTradingOptions> options,
            ILogger<DhanAuthService> logger)
        {
            _httpClient = httpClient;
            _accountStore = accountStore;
            _tokenStore = tokenStore;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<DhanLoginResultDto> LoginAccountAsync(DhanAccountCredential account, CancellationToken cancellationToken = default)
        {
            try
            {
                var pin = _accountStore.UnprotectPin(account);
                var totpSecret = _accountStore.UnprotectTotpSecret(account);
                var totpCode = ComputeTotp(totpSecret);

                var url = $"{_options.AuthBaseUrl.TrimEnd('/')}/app/generateAccessToken" +
                          $"?dhanClientId={Uri.EscapeDataString(account.DhanClientId)}" +
                          $"&pin={Uri.EscapeDataString(pin)}" +
                          $"&totp={Uri.EscapeDataString(totpCode)}";

                using var response = await _httpClient.PostAsync(url, content: null, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Dhan login failed for {Account} ({ClientId}): {Status} {Body}",
                        account.AccountName, account.DhanClientId, response.StatusCode, body);
                    return new DhanLoginResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Success = false,
                        ErrorMessage = $"{(int)response.StatusCode}: {body}"
                    };
                }

                var parsed = System.Text.Json.JsonSerializer.Deserialize<GenerateTokenResponse>(body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (parsed is null || string.IsNullOrEmpty(parsed.AccessToken))
                {
                    return new DhanLoginResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Success = false,
                        ErrorMessage = "Dhan returned an empty access token."
                    };
                }

                var expiryUtc = ParseExpiryAsUtc(parsed.ExpiryTime);
                var token = new Token
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    AccessToken = parsed.AccessToken,
                    ExpiryTimeUtc = expiryUtc
                };

                await _tokenStore.SaveAsync(token, cancellationToken);

                return new DhanLoginResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Success = true,
                    ExpiryUtc = expiryUtc
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error logging in Dhan account {Account} ({ClientId})", account.AccountName, account.DhanClientId);
                return new DhanLoginResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<IReadOnlyList<DhanLoginResultDto>> LoginAllAsync(CancellationToken cancellationToken = default)
        {
            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            var tasks = accounts.Select(a => LoginAccountAsync(a, cancellationToken));
            var results = await Task.WhenAll(tasks);
            return results;
        }

        public async Task<Token?> EnsureValidTokenAsync(DhanAccountCredential account, CancellationToken cancellationToken = default)
        {
            var existing = await _tokenStore.GetAsync(account.DhanClientId, cancellationToken);
            if (existing is not null && !existing.IsNearExpiry(_options.TokenRefreshThresholdMinutes))
            {
                return existing;
            }

            var loginResult = await LoginAccountAsync(account, cancellationToken);
            if (!loginResult.Success)
            {
                return null;
            }

            return await _tokenStore.GetAsync(account.DhanClientId, cancellationToken);
        }

        public async Task<string> GetPublicIpAddressAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var ip = await _httpClient.GetStringAsync("https://api.ipify.org?format=text", cancellationToken);
                return ip.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Primary IP-detection service (ipify) failed, falling back to checkip.amazonaws.com");
                var ip = await _httpClient.GetStringAsync("https://checkip.amazonaws.com", cancellationToken);
                return ip.Trim();
            }
        }

        public async Task<IReadOnlyList<DhanSetIpResultDto>> SetStaticIpForAllAccountsAsync(string ip, string ipFlag, CancellationToken cancellationToken = default)
        {
            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            var tasks = accounts.Select(account => SetStaticIpForAccountAsync(account, ip, ipFlag, cancellationToken));
            return await Task.WhenAll(tasks);
        }

        public async Task<IReadOnlyList<DhanIpStatusResultDto>> GetStaticIpStatusForAllAccountsAsync(CancellationToken cancellationToken = default)
        {
            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            var tasks = accounts.Select(account => GetStaticIpStatusForAccountAsync(account, cancellationToken));
            return await Task.WhenAll(tasks);
        }

        private async Task<DhanSetIpResultDto> SetStaticIpForAccountAsync(DhanAccountCredential account, string ip, string ipFlag, CancellationToken cancellationToken)
        {
            try
            {
                var token = await EnsureValidTokenAsync(account, cancellationToken);
                if (token is null)
                {
                    return new DhanSetIpResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Success = false,
                        ErrorMessage = "Could not obtain a valid access token (login failed)."
                    };
                }

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/ip/setIP");
                httpRequest.Headers.Add("access-token", token.AccessToken);
                httpRequest.Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    dhanClientId = account.DhanClientId,
                    ip,
                    ipFlag
                });

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Dhan set-IP failed for {Account} ({ClientId}): {Status} {Body}",
                        account.AccountName, account.DhanClientId, response.StatusCode, body);
                    return new DhanSetIpResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Success = false,
                        ErrorMessage = $"{(int)response.StatusCode}: {body}"
                    };
                }

                return new DhanSetIpResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error setting Dhan static IP for {Account} ({ClientId})", account.AccountName, account.DhanClientId);
                return new DhanSetIpResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        
private async Task<DhanIpStatusResultDto> GetStaticIpStatusForAccountAsync(
    DhanAccountCredential account,
    CancellationToken cancellationToken)
{
    try
    {
        var token = await EnsureValidTokenAsync(account, cancellationToken);

        if (token is null)
        {
            return new DhanIpStatusResultDto
            {
                AccountName = account.AccountName,
                DhanClientId = account.DhanClientId,
                Success = false,
                ErrorMessage = "Could not obtain a valid access token."
            };
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/ip/getIP");

        request.Headers.Add("access-token", token.AccessToken);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(
            request, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(
            cancellationToken);

        // Log status and response, but never log the access token.
        _logger.LogInformation(
            "Dhan getIP response for account {Account}, client {ClientId}: HTTP {StatusCode}",
            account.AccountName,
            account.DhanClientId,
            (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Dhan getIP error for client {ClientId}: {StatusCode}, body: {Body}",
                account.DhanClientId,
                (int)response.StatusCode,
                body);

            return new DhanIpStatusResultDto
            {
                AccountName = account.AccountName,
                DhanClientId = account.DhanClientId,
                Success = false,
                ErrorMessage = $"{(int)response.StatusCode}: {body}"
            };
        }

        using var document = System.Text.Json.JsonDocument.Parse(body);
        var root = document.RootElement;

        // Dhan can return HTTP 200 with an error in the JSON body.
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array ||
            (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
             root.TryGetProperty("status", out var status) &&
             string.Equals(
                 status.GetString(),
                 "ERROR",
                 StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(
                "Dhan getIP returned an error payload for client {ClientId}: {Body}",
                account.DhanClientId,
                body);

            return new DhanIpStatusResultDto
            {
                AccountName = account.AccountName,
                DhanClientId = account.DhanClientId,
                Success = false,
                ErrorMessage = $"Dhan getIP returned an error: {body}"
            };
        }

        var parsed = System.Text.Json.JsonSerializer
            .Deserialize<GetIpResponse>(
                body,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

        if (parsed is null)
        {
            return new DhanIpStatusResultDto
            {
                AccountName = account.AccountName,
                DhanClientId = account.DhanClientId,
                Success = false,
                ErrorMessage = "Dhan returned an empty or invalid IP response."
            };
        }

        return new DhanIpStatusResultDto
        {
            AccountName = account.AccountName,
            DhanClientId = account.DhanClientId,
            Success = true,
            PrimaryIp = parsed.PrimaryIP,
            SecondaryIp = parsed.SecondaryIP,
            ModifyDatePrimary = parsed.ModifyDatePrimary,
            ModifyDateSecondary = parsed.ModifyDateSecondary
        };
    }
    catch (Exception ex)
    {
        _logger.LogError(
            ex,
            "Failed to fetch Dhan static IP status for account {Account}, client {ClientId}",
            account.AccountName,
            account.DhanClientId);

        return new DhanIpStatusResultDto
        {
            AccountName = account.AccountName,
            DhanClientId = account.DhanClientId,
            Success = false,
            ErrorMessage = ex.Message
        };
    }
}


        private async Task<DhanIpStatusResultDto> GetStaticIpStatusForAccountAsync1(DhanAccountCredential account, CancellationToken cancellationToken)
        {
            try
            {
                var token = await EnsureValidTokenAsync(account, cancellationToken);
                if (token is null)
                {
                    return new DhanIpStatusResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Success = false,
                        ErrorMessage = "Could not obtain a valid access token (login failed)."
                    };
                }

                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/ip/getIP");
                httpRequest.Headers.Add("access-token", token.AccessToken);

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Dhan get-IP failed for {Account} ({ClientId}): {Status} {Body}",
                        account.AccountName, account.DhanClientId, response.StatusCode, body);
                    return new DhanIpStatusResultDto
                    {
                        AccountName = account.AccountName,
                        DhanClientId = account.DhanClientId,
                        Success = false,
                        ErrorMessage = $"{(int)response.StatusCode}: {body}"
                    };
                }

                var parsed = System.Text.Json.JsonSerializer.Deserialize<GetIpResponse>(body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                return new DhanIpStatusResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Success = true,
                    PrimaryIp = parsed?.PrimaryIP,
                    SecondaryIp = parsed?.SecondaryIP,
                    ModifyDatePrimary = parsed?.ModifyDatePrimary,
                    ModifyDateSecondary = parsed?.ModifyDateSecondary
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error fetching Dhan static IP status for {Account} ({ClientId})", account.AccountName, account.DhanClientId);
                return new DhanIpStatusResultDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private class GetIpResponse
        {
            [JsonPropertyName("primaryIP")]
            public string? PrimaryIP { get; set; }

            [JsonPropertyName("secondaryIP")]
            public string? SecondaryIP { get; set; }

            [JsonPropertyName("modifyDatePrimary")]
            public string? ModifyDatePrimary { get; set; }

            [JsonPropertyName("modifyDateSecondary")]
            public string? ModifyDateSecondary { get; set; }
        }

        private static string ComputeTotp(string base32Secret)
        {
            var bytes = Base32Encoding.ToBytes(base32Secret);
            var totp = new Totp(bytes);
            return totp.ComputeTotp();
        }

        private static DateTime ParseExpiryAsUtc(string? expiryTime)
        {
            if (string.IsNullOrWhiteSpace(expiryTime) || !DateTime.TryParse(expiryTime, out var parsed))
            {
                // Fall back to a conservative 23h window if Dhan's response is unparseable.
                return DateTime.UtcNow.AddHours(23);
            }

            // Dhan returns expiryTime in IST without an offset; convert explicitly.
            var istOffset = TimeSpan.FromHours(5.5);
            var unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
            return (unspecified - istOffset).ToUniversalTime();
        }

        private class GenerateTokenResponse
        {
            [JsonPropertyName("dhanClientId")]
            public string? DhanClientId { get; set; }

            [JsonPropertyName("accessToken")]
            public string? AccessToken { get; set; }

            [JsonPropertyName("expiryTime")]
            public string? ExpiryTime { get; set; }
        }
    }
}
