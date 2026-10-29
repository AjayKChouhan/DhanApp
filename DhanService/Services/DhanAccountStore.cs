using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public interface IDhanAccountStore
    {
        Task<IReadOnlyList<DhanAccountCredential>> GetAllAsync(CancellationToken cancellationToken = default);
        Task SaveAllAsync(IEnumerable<SaveDhanAccountRequest> accounts, CancellationToken cancellationToken = default);

        /// <summary>
        /// Upserts Dhan and Angel One accounts from configuration and the local JSON file
        /// into the encrypted saved store, keyed by the broker account ID. Returns the number seeded.
        /// </summary>
        Task<int> SeedFromConfigurationAsync(CancellationToken cancellationToken = default);
        string UnprotectPin(DhanAccountCredential account);
        string UnprotectTotpSecret(DhanAccountCredential account);
        string UnprotectAngelPassword(DhanAccountCredential account);
        string UnprotectAngelApiKey(DhanAccountCredential account);
        string UnprotectAngelTotpSecret(DhanAccountCredential account);
        string UnprotectUpstoxApiKey(DhanAccountCredential account);
        string UnprotectUpstoxApiSecret(DhanAccountCredential account);
    }

    /// <summary>
    /// Persists broker account credentials to a local JSON file, encrypted at rest
    /// via ASP.NET Core Data Protection. Credentials are only
    /// decrypted in-memory, on demand, when generating an access token.
    /// </summary>
    public class DhanAccountStore : IDhanAccountStore
    {
        private readonly string _filePath;
        private readonly string _plaintextAccountsFilePath;
        private readonly IOptions<DhanTradingOptions> _options;
        private readonly IDataProtector _protector;
        private readonly ILogger<DhanAccountStore> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public DhanAccountStore(
            IOptions<DhanTradingOptions> options,
            IDataProtectionProvider dataProtectionProvider,
            IHostEnvironment environment,
            ILogger<DhanAccountStore> logger)
        {
            _options = options;
            _logger = logger;
            _protector = dataProtectionProvider.CreateProtector("DhanAI.DhanTrading.AccountCredentials");

            var folder = Path.Combine(environment.ContentRootPath, options.Value.DataFolder);
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "accounts.json");

            // Plaintext, hand-editable alternative to user-secrets/Key Vault. Login
            // imports these credentials into the protected saved-account store. The
            // local file is gitignored; do not commit or share it.
            _plaintextAccountsFilePath = Path.Combine(folder, "dhan-accounts.local.json");
        }

        public async Task<IReadOnlyList<DhanAccountCredential>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return await ReadFromDiskAsync(cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveAllAsync(IEnumerable<SaveDhanAccountRequest> accounts, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var toSave = accounts
                    .Where(a => !string.IsNullOrWhiteSpace(GetAccountId(a)))
                    .Select(a => new DhanAccountCredential
                    {
                        AccountName = string.IsNullOrWhiteSpace(a.AccountName) ? GetAccountId(a) : a.AccountName.Trim(),
                        Broker = NormalizeBroker(a.Broker),
                        DhanClientId = GetAccountId(a).Trim(),
                        ProtectedPin = ProtectIfNotEmpty(a.Pin),
                        ProtectedTotpSecret = ProtectIfNotEmpty(a.TotpSecret),
                        ProtectedAngelPassword = ProtectIfNotEmpty(a.Password),
                        ProtectedAngelApiKey = ProtectIfNotEmpty(NormalizeBroker(a.Broker) == "ANGELONE" ? a.ApiKey : null),
                        ProtectedAngelTotpSecret = ProtectIfNotEmpty(a.TotpSecret),
                        ProtectedUpstoxApiKey = ProtectIfNotEmpty(NormalizeBroker(a.Broker) == "UPSTOX" ? a.ApiKey : null),
                        ProtectedUpstoxApiSecret = ProtectIfNotEmpty(NormalizeBroker(a.Broker) == "UPSTOX" ? a.ApiSecret : null),
                    })
                    .ToList();

                var json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_filePath, json, cancellationToken);
                _logger.LogInformation("Saved {Count} Dhan account(s) to {Path}", toSave.Count, _filePath);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<int> SeedFromConfigurationAsync(CancellationToken cancellationToken = default)
        {
            var fromConfig = _options.Value.Accounts ?? new List<SaveDhanAccountRequest>();
            var fromFile = await ReadPlaintextAccountsFileAsync(cancellationToken);
            var combined = fromConfig.Concat(fromFile).ToList();

            if (combined.Count == 0)
            {
                return 0;
            }

            await _lock.WaitAsync(cancellationToken);
            try
            {
                var existing = await ReadFromDiskAsync(cancellationToken);
                var byClientId = existing.ToDictionary(a => a.DhanClientId, a => a, StringComparer.OrdinalIgnoreCase);

                var seededCount = 0;
                foreach (var account in combined)
                {
                    var broker = NormalizeBroker(account.Broker);
                    var accountId = GetAccountId(account);
                    var validDhan = broker == "DHAN" &&
                        !string.IsNullOrWhiteSpace(account.Pin) &&
                        !string.IsNullOrWhiteSpace(account.TotpSecret);
                    var validAngelOne = broker == "ANGELONE" &&
                        !string.IsNullOrWhiteSpace(account.Password) &&
                        !string.IsNullOrWhiteSpace(account.ApiKey) &&
                        !string.IsNullOrWhiteSpace(account.TotpSecret);
                    var validUpstox = broker == "UPSTOX" &&
                        !string.IsNullOrWhiteSpace(account.ApiKey) &&
                        !string.IsNullOrWhiteSpace(account.ApiSecret);
                    if (string.IsNullOrWhiteSpace(accountId) || (!validDhan && !validAngelOne && !validUpstox))
                    {
                        continue;
                    }

                    var clientId = accountId.Trim();
                    if (byClientId.TryGetValue(clientId, out var existingAccount) &&
                        !string.Equals(existingAccount.Broker, broker, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Account ID '{clientId}' is configured for more than one broker; account IDs must be unique.");
                    }
                    byClientId[clientId] = new DhanAccountCredential
                    {
                        AccountName = string.IsNullOrWhiteSpace(account.AccountName) ? clientId : account.AccountName.Trim(),
                        Broker = broker,
                        DhanClientId = clientId,
                        ProtectedPin = ProtectIfNotEmpty(account.Pin),
                        ProtectedTotpSecret = ProtectIfNotEmpty(broker == "DHAN" ? account.TotpSecret : null),
                        ProtectedAngelPassword = ProtectIfNotEmpty(account.Password),
                        ProtectedAngelApiKey = ProtectIfNotEmpty(broker == "ANGELONE" ? account.ApiKey : null),
                        ProtectedAngelTotpSecret = ProtectIfNotEmpty(broker == "ANGELONE" ? account.TotpSecret : null),
                        ProtectedUpstoxApiKey = ProtectIfNotEmpty(broker == "UPSTOX" ? account.ApiKey : null),
                        ProtectedUpstoxApiSecret = ProtectIfNotEmpty(broker == "UPSTOX" ? account.ApiSecret : null),
                    };
                    seededCount++;
                }

                if (seededCount > 0)
                {
                    var json = JsonSerializer.Serialize(byClientId.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(_filePath, json, cancellationToken);
                    _logger.LogInformation("Seeded {Count} Dhan account(s) from configuration into {Path}", seededCount, _filePath);
                }

                return seededCount;
            }
            finally
            {
                _lock.Release();
            }
        }

        public string UnprotectPin(DhanAccountCredential account) => _protector.Unprotect(account.ProtectedPin);

        public string UnprotectTotpSecret(DhanAccountCredential account) => _protector.Unprotect(account.ProtectedTotpSecret);

        public string UnprotectAngelPassword(DhanAccountCredential account) => _protector.Unprotect(account.ProtectedAngelPassword);

        public string UnprotectAngelApiKey(DhanAccountCredential account) => _protector.Unprotect(account.ProtectedAngelApiKey);

        public string UnprotectAngelTotpSecret(DhanAccountCredential account) => _protector.Unprotect(account.ProtectedAngelTotpSecret);

        public string UnprotectUpstoxApiKey(DhanAccountCredential account) => _protector.Unprotect(account.ProtectedUpstoxApiKey);

        public string UnprotectUpstoxApiSecret(DhanAccountCredential account) => _protector.Unprotect(account.ProtectedUpstoxApiSecret);

        private string ProtectIfNotEmpty(string? secret) =>
            string.IsNullOrEmpty(secret) ? string.Empty : _protector.Protect(secret);

        private static string GetAccountId(SaveDhanAccountRequest account) =>
            NormalizeBroker(account.Broker) switch
            {
                "ANGELONE" or "UPSTOX" =>
                    string.IsNullOrWhiteSpace(account.ClientCode) ? account.DhanClientId : account.ClientCode,
                _ => account.DhanClientId
            };

        private static string NormalizeBroker(string? broker)
        {
            if (string.IsNullOrWhiteSpace(broker) ||
                string.Equals(broker.Trim(), "DHAN", StringComparison.OrdinalIgnoreCase))
            {
                return "DHAN";
            }
            if (string.Equals(broker.Trim(), "ANGELONE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(broker.Trim(), "ANGEL", StringComparison.OrdinalIgnoreCase))
            {
                return "ANGELONE";
            }
            if (string.Equals(broker.Trim(), "UPSTOX", StringComparison.OrdinalIgnoreCase))
            {
                return "UPSTOX";
            }
            throw new ArgumentException($"Unsupported broker '{broker}'. Supported brokers are DHAN, ANGELONE, and UPSTOX.");
        }

        private async Task<List<SaveDhanAccountRequest>> ReadPlaintextAccountsFileAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_plaintextAccountsFilePath))
            {
                return new List<SaveDhanAccountRequest>();
            }

            var json = await File.ReadAllTextAsync(_plaintextAccountsFilePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<SaveDhanAccountRequest>();
            }

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<List<SaveDhanAccountRequest>>(json, options) ?? new List<SaveDhanAccountRequest>();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse {Path} - check it is valid JSON", _plaintextAccountsFilePath);
                return new List<SaveDhanAccountRequest>();
            }
        }

        private async Task<IReadOnlyList<DhanAccountCredential>> ReadFromDiskAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<DhanAccountCredential>();
            }

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<DhanAccountCredential>();
            }

            var accounts = JsonSerializer.Deserialize<List<DhanAccountCredential>>(json) ?? new List<DhanAccountCredential>();

            // Defensively drop any stray/corrupted records (e.g. blank DhanClientId) so they
            // never show up in status/login results and get pruned on the next seed/save write.
            return accounts.Where(a => !string.IsNullOrWhiteSpace(a.DhanClientId)).ToList();
        }
    }
}
