using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DhanService.Services;

namespace DhanService.Services
{
    public sealed class CopyTradingRuntimeStatus
    {
        private readonly ConcurrentDictionary<string, string> _connections = new(StringComparer.OrdinalIgnoreCase);

        public void Set(string clientId, string status) => _connections[clientId] = status;

        public string? Get(string? clientId) =>
            !string.IsNullOrWhiteSpace(clientId) && _connections.TryGetValue(clientId, out var status)
                ? status
                : null;
    }

    public sealed class DhanOrderUpdateWorker : BackgroundService
    {
        private static readonly Uri OrderUpdateUri = new("wss://api-order-update.dhan.co");

        private readonly ICopyTradingStore _store;
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanAuthService _authService;
        private readonly IDhanOrderService _orderService;
        private readonly ICopyTradingService _copyTradingService;
        private readonly CopyTradingRuntimeStatus _runtimeStatus;
        private readonly ILogger<DhanOrderUpdateWorker> _logger;

        public DhanOrderUpdateWorker(
            ICopyTradingStore store,
            IDhanAccountStore accountStore,
            IDhanAuthService authService,
            IDhanOrderService orderService,
            ICopyTradingService copyTradingService,
            CopyTradingRuntimeStatus runtimeStatus,
            ILogger<DhanOrderUpdateWorker> logger)
        {
            _store = store;
            _accountStore = accountStore;
            _authService = authService;
            _orderService = orderService;
            _copyTradingService = copyTradingService;
            _runtimeStatus = runtimeStatus;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _store.ResetInterruptedCopiesAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var settings = await _store.GetSettingsAsync(stoppingToken);
                    var masterClientId = settings.MasterClientId;
                    if (!settings.Enabled || string.IsNullOrWhiteSpace(masterClientId))
                    {
                        if (!string.IsNullOrWhiteSpace(masterClientId))
                        {
                            _runtimeStatus.Set(masterClientId, "Copy trading is paused.");
                        }
                        await _store.WaitForSettingsChangeAsync(stoppingToken);
                        continue;
                    }

                    if (!settings.Children.Any(child => child.Enabled))
                    {
                        _runtimeStatus.Set(masterClientId, "Enable at least one child account to start order monitoring.");
                        await _store.WaitForSettingsChangeAsync(stoppingToken);
                        continue;
                    }

                    var requiredMasterClientId = masterClientId
                        ?? throw new InvalidOperationException("Enabled copy trading requires a master account.");
                    await _copyTradingService.ProcessReadyCopiesAsync(stoppingToken);
                    var accounts = await _accountStore.GetAllAsync(stoppingToken);
                    var enabledDhanChildIds = settings.Children
                        .Where(child => child.Enabled)
                        .Select(child => accounts.FirstOrDefault(account =>
                            string.Equals(account.DhanClientId, child.DhanClientId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(account.Broker, "DHAN", StringComparison.OrdinalIgnoreCase))?.DhanClientId)
                        .Where(clientId => !string.IsNullOrWhiteSpace(clientId))
                        .OfType<string>()
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var connectionTasks = enabledDhanChildIds
                        .Prepend(requiredMasterClientId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(clientId => RunAccountSocketAsync(clientId, accounts, session.Token))
                        .Append(PollAngelChildStatusesAsync(settings.Children, accounts, session.Token))
                        .ToArray();
                    var connectionEnded = Task.WhenAny(connectionTasks);
                    var configurationChanged = _store.WaitForSettingsChangeAsync(session.Token).AsTask();
                    var completed = await Task.WhenAny(connectionEnded, configurationChanged);

                    session.Cancel();
                    if (completed == configurationChanged)
                    {
                        await configurationChanged;
                    }

                    else if (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("A Dhan order-update connection ended. Reconnecting after a short delay.");
                    }

                    await Task.WhenAll(connectionTasks);
                    if (!stoppingToken.IsCancellationRequested && completed != configurationChanged)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Copy-trading order monitor failed; it will retry.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        private async Task PollAngelChildStatusesAsync(
            IReadOnlyList<DhanService.Models.CopyTradingChildSetting> children,
            IReadOnlyList<DhanService.Models.DhanAccountCredential> accounts,
            CancellationToken cancellationToken)
        {
            var angelChildIds = children
                .Where(child => child.Enabled)
                .Select(child => accounts.FirstOrDefault(account =>
                    string.Equals(account.DhanClientId, child.DhanClientId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(account.Broker, "ANGELONE", StringComparison.OrdinalIgnoreCase)))
                .Where(account => account is not null)
                .Select(account => account!.DhanClientId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var clientId in angelChildIds)
                {
                    var result = await _orderService.GetOrdersAsync(clientId, cancellationToken);
                    if (!result.Success || result.Orders is not { ValueKind: JsonValueKind.Array } orders)
                    {
                        _logger.LogWarning("Angel One order status polling failed for account {ClientId}: {Error}",
                            clientId, result.ErrorMessage);
                        continue;
                    }

                    foreach (var order in orders.EnumerateArray())
                    {
                        var orderId = ReadJsonText(order, "orderId");
                        if (string.IsNullOrWhiteSpace(orderId))
                        {
                            continue;
                        }

                        await _store.UpdateChildOrderStatusByOrderIdAsync(
                            clientId,
                            orderId,
                            ReadJsonText(order, "orderStatus") ?? string.Empty,
                            ReadJsonText(order, "reasonDescription"),
                            cancellationToken);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }

        private static string? ReadJsonText(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        private async Task RunAccountSocketAsync(
            string clientId,
            IReadOnlyList<DhanService.Models.DhanAccountCredential> accounts,
            CancellationToken cancellationToken)
        {
            var account = accounts.FirstOrDefault(a =>
                string.Equals(a.DhanClientId, clientId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                _runtimeStatus.Set(clientId, "Account is no longer saved.");
                _logger.LogWarning("Dhan order-update monitoring skipped: account {ClientId} is not saved.", clientId);
                return;
            }
            if (!string.Equals(account.Broker, "DHAN", StringComparison.OrdinalIgnoreCase))
            {
                _runtimeStatus.Set(clientId, "The configured copy-trading master must be a Dhan account.");
                _logger.LogWarning("Dhan order-update monitoring skipped: configured master {ClientId} is not a Dhan account.", clientId);
                return;
            }

            try
            {
                var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
                if (token is null)
                {
                    _runtimeStatus.Set(clientId, "Token unavailable. Check account credentials and login.");
                    _logger.LogWarning("Dhan order-update monitoring could not get a valid token for {ClientId}.", clientId);
                    return;
                }

                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await socket.ConnectAsync(OrderUpdateUri, cancellationToken);

                var login = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    LoginReq = new
                    {
                        MsgCode = 42,
                        ClientId = account.DhanClientId,
                        Token = token.AccessToken
                    },
                    UserType = "SELF"
                });
                await socket.SendAsync(login, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
                _runtimeStatus.Set(clientId, "Listening for Dhan order updates.");
                _logger.LogInformation("Connected to Dhan order updates for {ClientId}.", clientId);

                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var payload = await ReceiveMessageAsync(socket, cancellationToken);
                    if (payload is null)
                    {
                        break;
                    }

                    using var document = JsonDocument.Parse(payload);
                    try
                    {
                        await _copyTradingService.HandleOrderUpdateAsync(clientId, document.RootElement, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Could not process Dhan order update for {ClientId}.", clientId);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (WebSocketException ex)
            {
                _runtimeStatus.Set(clientId, $"Order-update connection failed: {ex.Message}");
                _logger.LogWarning(ex, "Dhan order-update connection failed for {ClientId}.", clientId);
            }
            catch (Exception ex)
            {
                _runtimeStatus.Set(clientId, $"Order-update listener failed: {ex.Message}");
                _logger.LogError(ex, "Dhan order-update listener failed for {ClientId}.", clientId);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _runtimeStatus.Set(clientId, "Order monitoring paused.");
                }
                else if (_runtimeStatus.Get(clientId) == "Listening for Dhan order updates.")
                {
                    _runtimeStatus.Set(clientId, "Disconnected. Reconnecting.");
                }
            }
        }

        private static async Task<byte[]?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            const int maxMessageSize = 1024 * 1024;
            var buffer = new byte[8192];
            using var message = new MemoryStream();

            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                if (message.Length + result.Count > maxMessageSize)
                {
                    throw new InvalidDataException("Dhan order-update message exceeded the 1 MB limit.");
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return message.ToArray();
                }
            }
        }
    }
}
