using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public interface IUpstoxAuthService
    {
        Task<string> CreateAuthorizationUrlAsync(string clientId, CancellationToken cancellationToken = default);
        Task<DhanLoginResultDto> CompleteAuthorizationAsync(
            string code, string state, CancellationToken cancellationToken = default);
    }

    public sealed class UpstoxAuthService : IUpstoxAuthService
    {
        private static readonly Uri AuthorizationEndpoint =
            new("https://api.upstox.com/v2/login/authorization/dialog");
        private static readonly Uri TokenEndpoint =
            new("https://api.upstox.com/v2/login/authorization/token");
        private static readonly Uri ProfileEndpoint =
            new("https://api.upstox.com/v2/user/profile");
        private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(10);

        private readonly HttpClient _httpClient;
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanTokenStore _tokenStore;
        private readonly DhanTradingOptions _options;
        private readonly ConcurrentDictionary<string, PendingAuthorization> _pendingAuthorizations =
            new(StringComparer.Ordinal);

        public UpstoxAuthService(
            IHttpClientFactory httpClientFactory,
            IDhanAccountStore accountStore,
            IDhanTokenStore tokenStore,
            IOptions<DhanTradingOptions> options)
        {
            _httpClient = httpClientFactory.CreateClient();
            _accountStore = accountStore;
            _tokenStore = tokenStore;
            _options = options.Value;
        }

        public async Task<string> CreateAuthorizationUrlAsync(
            string clientId, CancellationToken cancellationToken = default)
        {
            var redirectUri = GetRedirectUri();
            var account = await FindUpstoxAccountAsync(clientId, cancellationToken);
            var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            _pendingAuthorizations[state] = new PendingAuthorization(
                account.DhanClientId, redirectUri, DateTimeOffset.UtcNow.Add(AuthorizationLifetime));
            RemoveExpiredAuthorizations();

            var query = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = _accountStore.UnprotectUpstoxApiKey(account),
                ["redirect_uri"] = redirectUri,
                ["state"] = state
            };
            var builder = new UriBuilder(AuthorizationEndpoint)
            {
                Query = string.Join("&", query.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
            };
            return builder.Uri.ToString();
        }

        public async Task<DhanLoginResultDto> CompleteAuthorizationAsync(
            string code, string state, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(code) ||
                string.IsNullOrWhiteSpace(state) ||
                !_pendingAuthorizations.TryRemove(state, out var pending) ||
                pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("The Upstox authorization state is invalid or expired. Start a new connection.");
            }

            var account = await FindUpstoxAccountAsync(pending.ClientId, cancellationToken);
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = _accountStore.UnprotectUpstoxApiKey(account),
                    ["client_secret"] = _accountStore.UnprotectUpstoxApiSecret(account),
                    ["redirect_uri"] = pending.RedirectUri,
                    ["grant_type"] = "authorization_code"
                })
            };
            using var tokenResponse = await _httpClient.SendAsync(tokenRequest, cancellationToken);
            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Upstox token exchange failed ({(int)tokenResponse.StatusCode}): {ExtractApiError(tokenBody)}");
            }

            using var tokenDocument = JsonDocument.Parse(tokenBody);
            var tokenData = GetDataObject(tokenDocument.RootElement);
            var accessToken = ReadString(tokenData, "access_token");
            var expiresIn = ReadInt64(tokenData, "expires_in");
            if (string.IsNullOrWhiteSpace(accessToken) || expiresIn <= 0)
            {
                throw new InvalidDataException("Upstox returned an incomplete access-token response.");
            }

            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, ProfileEndpoint);
            profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var profileResponse = await _httpClient.SendAsync(profileRequest, cancellationToken);
            var profileBody = await profileResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!profileResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Upstox account verification failed ({(int)profileResponse.StatusCode}): {ExtractApiError(profileBody)}");
            }

            using var profileDocument = JsonDocument.Parse(profileBody);
            var profileData = GetDataObject(profileDocument.RootElement);
            var upstoxClientId = ReadString(profileData, "user_id");
            if (!string.Equals(upstoxClientId, account.DhanClientId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The authorized Upstox user ID does not match the saved child-account ID.");
            }

            var expiryUtc = DateTime.UtcNow.AddSeconds(expiresIn);
            await _tokenStore.SaveAsync(new Token
            {
                AccountName = account.AccountName,
                Broker = "UPSTOX",
                DhanClientId = account.DhanClientId,
                AccessToken = accessToken,
                ExpiryTimeUtc = expiryUtc
            }, cancellationToken);

            return new DhanLoginResultDto
            {
                AccountName = account.AccountName,
                Broker = "UPSTOX",
                DhanClientId = account.DhanClientId,
                Success = true,
                ExpiryUtc = expiryUtc
            };
        }

        private async Task<DhanAccountCredential> FindUpstoxAccountAsync(
            string clientId, CancellationToken cancellationToken)
        {
            var account = (await _accountStore.GetAllAsync(cancellationToken)).FirstOrDefault(candidate =>
                string.Equals(candidate.DhanClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Broker, "UPSTOX", StringComparison.OrdinalIgnoreCase));
            return account ?? throw new InvalidOperationException("The saved Upstox account was not found.");
        }

        private string GetRedirectUri()
        {
            if (!Uri.TryCreate(_options.UpstoxRedirectUri, UriKind.Absolute, out var redirectUri) ||
                (redirectUri.Scheme != Uri.UriSchemeHttps && !redirectUri.IsLoopback) ||
                !string.Equals(
                    redirectUri.AbsolutePath.TrimEnd('/'),
                    "/api/dhantrading/upstox/callback",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(redirectUri.Query) ||
                !string.IsNullOrEmpty(redirectUri.Fragment))
            {
                throw new InvalidOperationException(
                    "Configure UpstoxRedirectUri as the absolute DhanAPI URL ending in /api/dhantrading/upstox/callback, and register that exact URL in the Upstox developer app.");
            }

            return redirectUri.ToString();
        }

        private void RemoveExpiredAuthorizations()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var authorization in _pendingAuthorizations)
            {
                if (authorization.Value.ExpiresAtUtc <= now)
                {
                    _pendingAuthorizations.TryRemove(authorization.Key, out _);
                }
            }
        }

        private static JsonElement GetDataObject(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object
                ? data
                : root;

        private static string? ReadString(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static long ReadInt64(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var value) &&
            value.TryGetInt64(out var number)
                ? number
                : 0;

        private static string ExtractApiError(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return ReadString(document.RootElement, "errors") ??
                       ReadString(document.RootElement, "message") ??
                       "Upstox returned an unsuccessful response.";
            }
            catch (JsonException)
            {
                return "Upstox returned an invalid response.";
            }
        }

        private sealed record PendingAuthorization(
            string ClientId, string RedirectUri, DateTimeOffset ExpiresAtUtc);
    }
}
