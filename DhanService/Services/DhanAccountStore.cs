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
        /// Upserts accounts from "DhanTrading:Accounts" config (user-secrets/Key Vault) and
        /// from the plaintext App_Data/DhanTrading/dhan-accounts.local.json file into the
        /// saved store, keyed by DhanClientId. Returns the number seeded.
        /// </summary>
        Task<int> SeedFromConfigurationAsync(CancellationToken cancellationToken = default);
        string UnprotectPin(DhanAccountCredential account);
        string UnprotectTotpSecret(DhanAccountCredential account);
    }

    /// <summary>
    /// Persists Dhan account credentials (PIN, TOTP secret) to a local JSON file,
    /// encrypted at rest via ASP.NET Core Data Protection. Credentials are only
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

            // Plaintext, hand-editable alternative to user-secrets/Key Vault: fill in
            // [{ "accountName", "dhanClientId", "pin", "totpSecret" }, ...] and click
            // Login - no UI entry or CLI commands required. Lives under App_Data (gitignored).
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
                    .Where(a => !string.IsNullOrWhiteSpace(a.DhanClientId))
                    .Select(a => new DhanAccountCredential
                    {
                        AccountName = string.IsNullOrWhiteSpace(a.AccountName) ? a.DhanClientId : a.AccountName.Trim(),
                        DhanClientId = a.DhanClientId.Trim(),
                        ProtectedPin = _protector.Protect(a.Pin ?? string.Empty),
                        ProtectedTotpSecret = _protector.Protect(a.TotpSecret ?? string.Empty),
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
                    if (string.IsNullOrWhiteSpace(account.DhanClientId) ||
                        string.IsNullOrWhiteSpace(account.Pin) ||
                        string.IsNullOrWhiteSpace(account.TotpSecret))
                    {
                        continue;
                    }

                    var clientId = account.DhanClientId.Trim();
                    byClientId[clientId] = new DhanAccountCredential
                    {
                        AccountName = string.IsNullOrWhiteSpace(account.AccountName) ? clientId : account.AccountName.Trim(),
                        DhanClientId = clientId,
                        ProtectedPin = _protector.Protect(account.Pin),
                        ProtectedTotpSecret = _protector.Protect(account.TotpSecret),
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
