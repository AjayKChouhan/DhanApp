using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public interface IDhanTokenStore
    {
        Task<Token?> GetAsync(string dhanClientId, CancellationToken cancellationToken = default);
        Task<Token?> GetAsync(string dhanClientId, string broker, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Token>> GetAllAsync(CancellationToken cancellationToken = default);
        Task SaveAsync(Token token, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Caches generated Dhan access tokens in memory and persists them (encrypted)
    /// to a local JSON file so they survive app restarts within their 24h validity.
    /// </summary>
    public class DhanTokenStore : IDhanTokenStore
    {
        private readonly string _filePath;
        private readonly IDataProtector _protector;
        private readonly ILogger<DhanTokenStore> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private Dictionary<string, Token>? _cache;

        public DhanTokenStore(
            IOptions<DhanTradingOptions> options,
            IDataProtectionProvider dataProtectionProvider,
            IHostEnvironment environment,
            ILogger<DhanTokenStore> logger)
        {
            _logger = logger;
            _protector = dataProtectionProvider.CreateProtector("DhanAI.DhanTrading.Tokens");

            var folder = Path.Combine(environment.ContentRootPath, options.Value.DataFolder);
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "tokens.json");
        }

        public async Task<Token?> GetAsync(string dhanClientId, CancellationToken cancellationToken = default)
            => await GetAsync(dhanClientId, "DHAN", cancellationToken);

        public async Task<Token?> GetAsync(
            string dhanClientId, string broker, CancellationToken cancellationToken = default)
        {
            var all = await GetAllAsync(cancellationToken);
            return all.FirstOrDefault(t =>
                string.Equals(t.DhanClientId, dhanClientId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Broker, broker, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<IReadOnlyList<Token>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                return _cache!.Values.ToList();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveAsync(Token token, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                _cache![GetTokenKey(token.DhanClientId, token.Broker)] = token;
                await PersistAsync(cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (_cache is not null)
            {
                return;
            }

            _cache = new Dictionary<string, Token>();

            if (!File.Exists(_filePath))
            {
                return;
            }

            try
            {
                var protectedJson = await File.ReadAllTextAsync(_filePath, cancellationToken);
                if (string.IsNullOrWhiteSpace(protectedJson))
                {
                    return;
                }

                var json = _protector.Unprotect(protectedJson);
                var tokens = JsonSerializer.Deserialize<List<Token>>(json) ?? new List<Token>();
                foreach (var token in tokens)
                {
                    _cache[GetTokenKey(token.DhanClientId, token.Broker)] = token;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load persisted Dhan tokens from {Path}; starting with empty cache", _filePath);
            }
        }

        private async Task PersistAsync(CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(_cache!.Values.ToList());
            var protectedJson = _protector.Protect(json);
            await File.WriteAllTextAsync(_filePath, protectedJson, cancellationToken);
        }

        private static string GetTokenKey(string clientId, string broker) => $"{broker}:{clientId}";
    }
}
