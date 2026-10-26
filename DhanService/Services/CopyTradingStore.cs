using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public interface ICopyTradingStore
    {
        Task<CopyTradingSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
        Task SaveSettingsAsync(CopyTradingSettings settings, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<CopyTradingOrderRecord>> GetRecentOrdersAsync(CancellationToken cancellationToken = default);
        Task<CopyTradingOrderRecord?> TryCreateOrderAsync(CopyTradingOrderRecord order, CancellationToken cancellationToken = default);
        Task<bool> UpdateMasterOrderStatusAsync(string masterClientId, string masterOrderId, string status, string? errorMessage, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<CopyTradingOrderRecord>> GetReadyOrdersAsync(CancellationToken cancellationToken = default);
        Task<bool> TryStartChildCopyAsync(string masterClientId, string masterOrderId, string childClientId, CancellationToken cancellationToken = default);
        Task SkipChildCopyAsync(string masterClientId, string masterOrderId, string childClientId, string reason, CancellationToken cancellationToken = default);
        Task UpdateChildSubmissionAsync(string masterClientId, string masterOrderId, string childClientId, DhanOrderResultDto result, bool permanentFailure = false, CancellationToken cancellationToken = default);
        Task UpdateChildOrderStatusAsync(string clientId, string correlationId, string orderId, string status, string? errorMessage, CancellationToken cancellationToken = default);
        Task<bool> RetryChildCopyAsync(string masterClientId, string masterOrderId, string childClientId, CancellationToken cancellationToken = default);
        Task ResetInterruptedCopiesAsync(CancellationToken cancellationToken = default);
        ValueTask<bool> WaitForSettingsChangeAsync(CancellationToken cancellationToken = default);
    }

    public sealed class CopyTradingStore : ICopyTradingStore
    {
        private const int MaxOrderHistory = 500;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly string _filePath;
        private readonly ILogger<CopyTradingStore> _logger;
        private readonly IDataProtector _protector;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Channel<bool> _settingsChanged = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        private State? _state;

        public CopyTradingStore(
            IOptions<DhanTradingOptions> options,
            IHostEnvironment environment,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<CopyTradingStore> logger)
        {
            _logger = logger;
            _protector = dataProtectionProvider.CreateProtector("DhanAI.CopyTrading.State");
            var folder = Path.Combine(environment.ContentRootPath, options.Value.DataFolder);
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "copy-trading.json");
        }

        public async Task<CopyTradingSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                return Clone(_state!.Settings);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveSettingsAsync(CopyTradingSettings settings, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                _state!.Settings = Clone(settings);
                await PersistAsync(cancellationToken);
                _settingsChanged.Writer.TryWrite(true);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<IReadOnlyList<CopyTradingOrderRecord>> GetRecentOrdersAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                return _state!.Orders.OrderByDescending(o => o.ReceivedAtUtc).Select(Clone).ToList();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<CopyTradingOrderRecord?> TryCreateOrderAsync(CopyTradingOrderRecord order, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                var existing = FindOrder(order.MasterClientId, order.MasterOrderId);
                if (existing is not null)
                {
                    return null;
                }

                _state!.Orders.Add(Clone(order));
                if (_state.Orders.Count > MaxOrderHistory)
                {
                    _state.Orders = _state.Orders
                        .OrderByDescending(o => o.ReceivedAtUtc)
                        .Take(MaxOrderHistory)
                        .ToList();
                }
                await PersistAsync(cancellationToken);
                return Clone(order);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool> UpdateMasterOrderStatusAsync(
            string masterClientId, string masterOrderId, string status, string? errorMessage,
            CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                var order = FindOrder(masterClientId, masterOrderId);
                if (order is null)
                {
                    return false;
                }

                order.MasterOrderStatus = status;
                order.MasterOrderErrorMessage = errorMessage;
                await PersistAsync(cancellationToken);
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<IReadOnlyList<CopyTradingOrderRecord>> GetReadyOrdersAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                return _state!.Orders
                    .Where(o => o.Children.Any(c => string.Equals(c.CopyState, "READY", StringComparison.OrdinalIgnoreCase)))
                    .Select(Clone)
                    .ToList();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool> TryStartChildCopyAsync(
            string masterClientId, string masterOrderId, string childClientId, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                var child = FindChild(masterClientId, masterOrderId, childClientId);
                if (child is null || !string.Equals(child.CopyState, "READY", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                child.CopyState = "SUBMITTING";
                await PersistAsync(cancellationToken);
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task UpdateChildSubmissionAsync(
            string masterClientId, string masterOrderId, string childClientId, DhanOrderResultDto result, bool permanentFailure = false,
            CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                var child = FindChild(masterClientId, masterOrderId, childClientId);
                if (child is null)
                {
                    throw new InvalidOperationException("Copy-trading child record was not found.");
                }

                child.CopyState = result.Success ? "SUBMITTED" : permanentFailure ? "SKIPPED" : "FAILED";
                if (!string.IsNullOrWhiteSpace(result.OrderId))
                {
                    child.OrderId = result.OrderId;
                }
                if (child.LastStatusUpdateUtc is null)
                {
                    child.OrderStatus = result.OrderStatus;
                }
                child.ErrorMessage = result.ErrorMessage;
                await PersistAsync(cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SkipChildCopyAsync(
            string masterClientId, string masterOrderId, string childClientId, string reason,
            CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                var child = FindChild(masterClientId, masterOrderId, childClientId);
                if (child is null || !string.Equals(child.CopyState, "READY", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                child.CopyState = "SKIPPED";
                child.ErrorMessage = reason;
                await PersistAsync(cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task UpdateChildOrderStatusAsync(
            string clientId, string correlationId, string orderId, string status, string? errorMessage,
            CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                var child = _state!.Orders
                    .SelectMany(o => o.Children)
                    .FirstOrDefault(c =>
                        string.Equals(c.DhanClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.CorrelationId, correlationId, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(orderId))
                {
                    child.OrderId = orderId;
                }
                child.OrderStatus = status;
                child.LastStatusUpdateUtc = DateTime.UtcNow;
                child.ErrorMessage = string.Equals(status, "REJECTED", StringComparison.OrdinalIgnoreCase)
                    ? errorMessage
                    : null;
                await PersistAsync(cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool> RetryChildCopyAsync(
            string masterClientId, string masterOrderId, string childClientId, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                var child = FindChild(masterClientId, masterOrderId, childClientId);
                if (child is null || !string.Equals(child.CopyState, "FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                child.CopyState = "READY";
                child.ErrorMessage = null;
                await PersistAsync(cancellationToken);
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task ResetInterruptedCopiesAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await EnsureLoadedAsync(cancellationToken);
                var changed = false;
                foreach (var child in _state!.Orders.SelectMany(o => o.Children)
                             .Where(c => string.Equals(c.CopyState, "SUBMITTING", StringComparison.OrdinalIgnoreCase)))
                {
                    child.CopyState = "READY";
                    changed = true;
                }

                if (changed)
                {
                    await PersistAsync(cancellationToken);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public ValueTask<bool> WaitForSettingsChangeAsync(CancellationToken cancellationToken = default) =>
            _settingsChanged.Reader.ReadAsync(cancellationToken);

        private CopyTradingOrderRecord? FindOrder(string masterClientId, string masterOrderId) =>
            _state!.Orders.FirstOrDefault(o =>
                string.Equals(o.MasterClientId, masterClientId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.MasterOrderId, masterOrderId, StringComparison.OrdinalIgnoreCase));

        private CopyTradingChildOrderRecord? FindChild(string masterClientId, string masterOrderId, string childClientId) =>
            FindOrder(masterClientId, masterOrderId)?.Children.FirstOrDefault(c =>
                string.Equals(c.DhanClientId, childClientId, StringComparison.OrdinalIgnoreCase));

        private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (_state is not null)
            {
                return;
            }

            if (!File.Exists(_filePath))
            {
                _state = new State();
                return;
            }

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            _state = JsonSerializer.Deserialize<State>(_protector.Unprotect(json))
                ?? throw new InvalidDataException($"Copy-trading data at '{_filePath}' is empty or invalid.");
            _state.Settings ??= new CopyTradingSettings();
            _state.Settings.Children ??= new List<CopyTradingChildSetting>();
            _state.Orders ??= new List<CopyTradingOrderRecord>();
        }

        private async Task PersistAsync(CancellationToken cancellationToken)
        {
            var temporaryPath = $"{_filePath}.tmp";
            var json = JsonSerializer.Serialize(_state, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, _protector.Protect(json), cancellationToken);
            File.Move(temporaryPath, _filePath, overwrite: true);
            _logger.LogDebug("Persisted copy-trading configuration and order state.");
        }

        private static T Clone<T>(T value) =>
            JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))
            ?? throw new InvalidDataException("Could not clone copy-trading state.");

        private sealed class State
        {
            public CopyTradingSettings Settings { get; set; } = new();
            public List<CopyTradingOrderRecord> Orders { get; set; } = new();
        }
    }
}
