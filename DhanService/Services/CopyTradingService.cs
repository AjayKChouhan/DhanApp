using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using DhanService.Models;

namespace DhanService.Services
{
    public interface ICopyTradingService
    {
        Task SaveSettingsAsync(CopyTradingSettings settings, CancellationToken cancellationToken = default);
        Task HandleOrderUpdateAsync(string clientId, JsonElement message, CancellationToken cancellationToken = default);
        Task ProcessReadyCopiesAsync(CancellationToken cancellationToken = default);
        Task<bool> RetryChildCopyAsync(string masterClientId, string masterOrderId, string childClientId, CancellationToken cancellationToken = default);
    }

    public class CopyTradingService : ICopyTradingService
    {
        private static readonly HashSet<string> CopyableMasterStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "PENDING", "TRADED", "PART_TRADED"
        };
        private static readonly HashSet<string> TerminalMasterStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "REJECTED", "CANCELLED", "EXPIRED"
        };

        private readonly ICopyTradingStore _store;
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanOrderService _orderService;
        private readonly ILogger<CopyTradingService> _logger;
        private readonly SemaphoreSlim _processingLock = new(1, 1);

        public CopyTradingService(
            ICopyTradingStore store,
            IDhanAccountStore accountStore,
            IDhanOrderService orderService,
            ILogger<CopyTradingService> logger)
        {
            _store = store;
            _accountStore = accountStore;
            _orderService = orderService;
            _logger = logger;
        }

        public async Task SaveSettingsAsync(CopyTradingSettings settings, CancellationToken cancellationToken = default)
        {
            await _store.SaveSettingsAsync(settings, cancellationToken);
            var enabledChildIds = settings.Children
                .Where(child => child.Enabled)
                .Select(child => child.DhanClientId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var readyOrders = await _store.GetReadyOrdersAsync(cancellationToken);

            foreach (var order in readyOrders)
            {
                foreach (var child in order.Children.Where(child => string.Equals(child.CopyState, "READY", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!settings.Enabled ||
                        !string.Equals(order.MasterClientId, settings.MasterClientId, StringComparison.OrdinalIgnoreCase) ||
                        !enabledChildIds.Contains(child.DhanClientId))
                    {
                        await _store.SkipChildCopyAsync(
                            order.MasterClientId,
                            order.MasterOrderId,
                            child.DhanClientId,
                            "Copy settings changed before this child order could be submitted.",
                            cancellationToken);
                    }
                }
            }
        }

        public async Task HandleOrderUpdateAsync(string clientId, JsonElement message, CancellationToken cancellationToken = default)
        {
            var data = GetProperty(message, "Data");
            if (data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var correlationId = GetText(data, "CorrelationId");
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                await _store.UpdateChildOrderStatusAsync(
                    clientId,
                    correlationId,
                    GetText(data, "OrderNo"),
                    GetText(data, "Status"),
                    GetText(data, "ReasonDescription"),
                    cancellationToken);
            }

            var settings = await _store.GetSettingsAsync(cancellationToken);
            if (!settings.Enabled ||
                !string.Equals(settings.MasterClientId, clientId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var masterOrderId = GetText(data, "OrderNo");
            var status = GetText(data, "Status").ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(masterOrderId))
            {
                return;
            }

            var reason = GetText(data, "ReasonDescription");
            var existingOrder = await _store.GetOrderAsync(clientId, masterOrderId, cancellationToken);
            _logger.LogInformation(
                "Received master order update for {MasterOrderId}: status {Status}, quantity {Quantity}, existing copy record {HasCopyRecord}",
                masterOrderId,
                status,
                GetText(data, "Quantity"),
                existingOrder is not null);
            var existingUpdated = await _store.UpdateMasterOrderStatusAsync(
                clientId, masterOrderId, status, reason, cancellationToken);

            if (existingOrder is not null)
            {
                var updatedOrder = CreateOrderRecord(data, existingOrder, status);
                if (TerminalMasterStatuses.Contains(status))
                {
                    await CancelChildOrdersAsync(existingOrder, cancellationToken);
                }
                else
                {
                    await ModifyChildOrdersAsync(existingOrder, updatedOrder, cancellationToken);
                }
                await _store.UpdateMasterOrderDetailsAsync(updatedOrder, cancellationToken);
                return;
            }

            if (!CopyableMasterStatuses.Contains(status))
            {
                if (!existingUpdated && TerminalMasterStatuses.Contains(status))
                {
                    await CreateTerminalMasterOrderRecordAsync(data, clientId, masterOrderId, status, reason, settings, cancellationToken);
                }
                return;
            }

            var children = settings.Children
                .Where(c => c.Enabled && !string.Equals(c.DhanClientId, clientId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (children.Count == 0)
            {
                return;
            }

            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            var master = accounts.FirstOrDefault(a =>
                string.Equals(a.DhanClientId, clientId, StringComparison.OrdinalIgnoreCase));
            if (master is null)
            {
                _logger.LogWarning("Copy trading skipped order {OrderId}: configured master account is no longer saved.", masterOrderId);
                return;
            }

            var order = CreateOrderRecord(data, master, accounts, status, children);
            var created = await _store.TryCreateOrderAsync(order, cancellationToken);
            if (created is not null)
            {
                await ProcessOrderAsync(created, cancellationToken);
            }
        }

        private async Task CreateTerminalMasterOrderRecordAsync(
            JsonElement data,
            string masterClientId,
            string masterOrderId,
            string status,
            string? reason,
            CopyTradingSettings settings,
            CancellationToken cancellationToken)
        {
            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            var master = accounts.FirstOrDefault(account =>
                string.Equals(account.DhanClientId, masterClientId, StringComparison.OrdinalIgnoreCase));
            if (master is null)
            {
                _logger.LogWarning("Copy trading could not record master order {OrderId}: master account was not found.", masterOrderId);
                return;
            }

            var children = settings.Children
                .Where(child => child.Enabled && !string.Equals(child.DhanClientId, masterClientId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var order = CreateOrderRecord(data, master, accounts, status, children);
            order.MasterOrderErrorMessage = reason;
            foreach (var child in order.Children)
            {
                child.CopyState = "SKIPPED";
                child.ErrorMessage = $"Master order was {status}; no child order was submitted.";
            }
            await _store.TryCreateOrderAsync(order, cancellationToken);
        }

        public async Task ProcessReadyCopiesAsync(CancellationToken cancellationToken = default)
        {
            var settings = await _store.GetSettingsAsync(cancellationToken);
            if (!settings.Enabled)
            {
                return;
            }

            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                var readyOrders = await _store.GetReadyOrdersAsync(cancellationToken);
                foreach (var order in readyOrders)
                {
                    if (!string.Equals(order.MasterClientId, settings.MasterClientId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    await ProcessOrderAsync(order, cancellationToken);
                }
            }
            finally
            {
                _processingLock.Release();
            }
        }

        public async Task<bool> RetryChildCopyAsync(
            string masterClientId, string masterOrderId, string childClientId, CancellationToken cancellationToken = default)
        {
            var settings = await _store.GetSettingsAsync(cancellationToken);
            if (!settings.Enabled ||
                !string.Equals(settings.MasterClientId, masterClientId, StringComparison.OrdinalIgnoreCase) ||
                !settings.Children.Any(c => c.Enabled && string.Equals(c.DhanClientId, childClientId, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!await _store.RetryChildCopyAsync(masterClientId, masterOrderId, childClientId, cancellationToken))
            {
                return false;
            }

            await ProcessReadyCopiesAsync(cancellationToken);
            return true;
        }

        private CopyTradingOrderRecord CreateOrderRecord(
            JsonElement data,
            DhanAccountCredential master,
            IReadOnlyList<DhanAccountCredential> childAccounts,
            string status,
            IReadOnlyList<CopyTradingChildSetting> children)
        {
            var masterOrderId = GetText(data, "OrderNo");
            var order = new CopyTradingOrderRecord
            {
                MasterClientId = master.DhanClientId,
                MasterAccountName = master.AccountName,
                MasterOrderId = masterOrderId,
                TradingSymbol = FirstNonEmpty(GetText(data, "DisplayName"), GetText(data, "Symbol"), GetText(data, "SecurityId")),
                SecurityId = GetText(data, "SecurityId"),
                ExchangeSegment = MapExchangeSegment(GetText(data, "Exchange"), GetText(data, "Segment")),
                ProductType = MapProductType(GetText(data, "ProductName"), GetText(data, "Product")),
                TransactionType = MapTransactionType(GetText(data, "TxnType")),
                OrderType = MapOrderType(GetText(data, "OrderType")),
                Validity = MapValidity(GetText(data, "Validity")),
                Quantity = GetInteger(data, "Quantity"),
                Price = GetDecimal(data, "Price"),
                TriggerPrice = GetDecimal(data, "TriggerPrice"),
                AfterMarketOrder = GetText(data, "OffMktFlag") == "1",
                DisclosedQuantity = GetInteger(data, "DiscQuantity"),
                MasterOrderStatus = status,
                MasterOrderErrorMessage = GetText(data, "ReasonDescription"),
                Source = GetText(data, "Source"),
                ReceivedAtUtc = DateTime.UtcNow
            };

            foreach (var child in children)
            {
                var account = childAccounts.FirstOrDefault(a =>
                    string.Equals(a.DhanClientId, child.DhanClientId, StringComparison.OrdinalIgnoreCase));
                order.Children.Add(new CopyTradingChildOrderRecord
                {
                    DhanClientId = child.DhanClientId,
                    AccountName = account?.AccountName ?? child.DhanClientId,
                    CorrelationId = CreateCorrelationId(master.DhanClientId, masterOrderId, child.DhanClientId)
                });
            }

            return order;
        }

        private static CopyTradingOrderRecord CreateOrderRecord(
            JsonElement data, CopyTradingOrderRecord existingOrder, string status)
        {
            var updated = CloneOrder(existingOrder);
            updated.TradingSymbol = FirstNonEmpty(
                GetText(data, "DisplayName"),
                GetText(data, "Symbol"),
                GetText(data, "SecurityId"));
            updated.SecurityId = GetText(data, "SecurityId");
            updated.ExchangeSegment = MapExchangeSegment(GetText(data, "Exchange"), GetText(data, "Segment"));
            updated.ProductType = MapProductType(GetText(data, "ProductName"), GetText(data, "Product"));
            updated.TransactionType = MapTransactionType(GetText(data, "TxnType"));
            updated.OrderType = MapOrderType(GetText(data, "OrderType"));
            updated.Validity = MapValidity(GetText(data, "Validity"));
            updated.Quantity = GetInteger(data, "Quantity");
            updated.Price = GetDecimal(data, "Price");
            updated.TriggerPrice = GetDecimal(data, "TriggerPrice");
            updated.AfterMarketOrder = GetText(data, "OffMktFlag") == "1";
            updated.DisclosedQuantity = GetInteger(data, "DiscQuantity");
            updated.MasterOrderStatus = status;
            updated.MasterOrderErrorMessage = GetText(data, "ReasonDescription");
            return updated;
        }

        private async Task ModifyChildOrdersAsync(
            CopyTradingOrderRecord existingOrder,
            CopyTradingOrderRecord updatedOrder,
            CancellationToken cancellationToken)
        {
            foreach (var child in existingOrder.Children)
            {
                if (string.IsNullOrWhiteSpace(child.OrderId) ||
                    IsTerminalOrderStatus(child.OrderStatus) ||
                    !IsModifiableOrderStatus(child.OrderStatus))
                {
                    continue;
                }

                var previousQuantity = child.MirroredQuantity ?? existingOrder.Quantity;
                var previousOrderType = child.MirroredOrderType ?? existingOrder.OrderType;
                var previousPrice = child.MirroredPrice ?? existingOrder.Price;
                var previousTriggerPrice = child.MirroredTriggerPrice ?? existingOrder.TriggerPrice;
                var previousValidity = child.MirroredValidity ?? existingOrder.Validity;
                if (previousQuantity == updatedOrder.Quantity &&
                    string.Equals(previousOrderType, updatedOrder.OrderType, StringComparison.OrdinalIgnoreCase) &&
                    previousPrice == updatedOrder.Price &&
                    previousTriggerPrice == updatedOrder.TriggerPrice &&
                    string.Equals(previousValidity, updatedOrder.Validity, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _logger.LogInformation(
                    "Mirroring update for master order {MasterOrderId} to child {ClientId}: quantity {OldQuantity} -> {NewQuantity}, status {ChildStatus}",
                    existingOrder.MasterOrderId,
                    child.DhanClientId,
                    previousQuantity,
                    updatedOrder.Quantity,
                    child.OrderStatus);

                DhanOrderResultDto result;
                if (!string.Equals(existingOrder.SecurityId, updatedOrder.SecurityId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existingOrder.ExchangeSegment, updatedOrder.ExchangeSegment, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existingOrder.ProductType, updatedOrder.ProductType, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existingOrder.TransactionType, updatedOrder.TransactionType, StringComparison.OrdinalIgnoreCase))
                {
                    result = FailedResult(child, "Dhan does not support changing instrument, exchange, product, or side on an existing order.");
                }
                else
                {
                    result = await _orderService.ModifyOrderAsync(new ModifyDhanOrderRequest
                    {
                        DhanClientId = child.DhanClientId,
                        OrderId = child.OrderId,
                        OrderType = updatedOrder.OrderType,
                        Quantity = updatedOrder.Quantity,
                        Price = updatedOrder.Price,
                        TriggerPrice = updatedOrder.TriggerPrice,
                        Validity = updatedOrder.Validity
                    }, cancellationToken);
                }

                if (!result.Success)
                {
                    _logger.LogWarning("Could not mirror modification of master order {MasterOrderId} to child {ClientId}: {Error}",
                        existingOrder.MasterOrderId, child.DhanClientId, result.ErrorMessage);
                }
                await _store.UpdateChildCopyActionAsync(
                    existingOrder.MasterClientId,
                    existingOrder.MasterOrderId,
                    child.DhanClientId,
                    "MODIFY",
                    result,
                    updatedOrder,
                    cancellationToken);
            }
        }

        private async Task CancelChildOrdersAsync(
            CopyTradingOrderRecord masterOrder, CancellationToken cancellationToken)
        {
            foreach (var child in masterOrder.Children)
            {
                if (string.IsNullOrWhiteSpace(child.OrderId) || IsTerminalOrderStatus(child.OrderStatus))
                {
                    continue;
                }

                var result = await _orderService.CancelOrderAsync(
                    child.DhanClientId, child.OrderId, cancellationToken);
                if (!result.Success)
                {
                    _logger.LogWarning("Could not mirror cancellation of master order {MasterOrderId} to child {ClientId}: {Error}",
                        masterOrder.MasterOrderId, child.DhanClientId, result.ErrorMessage);
                }
                await _store.UpdateChildCopyActionAsync(
                    masterOrder.MasterClientId,
                    masterOrder.MasterOrderId,
                    child.DhanClientId,
                    "CANCEL",
                    result,
                    cancellationToken: cancellationToken);
            }
        }

        private static bool IsTerminalOrderStatus(string? status) =>
            !string.IsNullOrWhiteSpace(status) &&
            (TerminalMasterStatuses.Contains(status) ||
             string.Equals(status, "TRADED", StringComparison.OrdinalIgnoreCase));

        private static bool IsModifiableOrderStatus(string? status) =>
            string.IsNullOrWhiteSpace(status) ||
            string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "TRANSIT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "PART_TRADED", StringComparison.OrdinalIgnoreCase);

        private static CopyTradingOrderRecord CloneOrder(CopyTradingOrderRecord order) =>
            JsonSerializer.Deserialize<CopyTradingOrderRecord>(JsonSerializer.Serialize(order))
            ?? throw new InvalidDataException("Could not clone copy-trading order.");

        private async Task ProcessOrderAsync(CopyTradingOrderRecord order, CancellationToken cancellationToken)
        {
            var currentSettings = await _store.GetSettingsAsync(cancellationToken);
            var enabledChildIds = currentSettings.Children
                .Where(child => child.Enabled)
                .Select(child => child.DhanClientId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var child in order.Children.Where(c => string.Equals(c.CopyState, "READY", StringComparison.OrdinalIgnoreCase)))
            {
                if (!currentSettings.Enabled ||
                    !string.Equals(order.MasterClientId, currentSettings.MasterClientId, StringComparison.OrdinalIgnoreCase) ||
                    !enabledChildIds.Contains(child.DhanClientId))
                {
                    await _store.SkipChildCopyAsync(
                        order.MasterClientId,
                        order.MasterOrderId,
                        child.DhanClientId,
                        "Copy settings changed before this child order could be submitted.",
                        cancellationToken);
                    continue;
                }

                if (!await _store.TryStartChildCopyAsync(order.MasterClientId, order.MasterOrderId, child.DhanClientId, cancellationToken))
                {
                    continue;
                }

                DhanOrderResultDto result;
                var permanentFailure = false;
                try
                {
                    var validationError = ValidateCopyOrder(order);
                    if (validationError is not null)
                    {
                        permanentFailure = true;
                        result = FailedResult(child, validationError);
                    }
                    else
                    {
                        var request = new PlaceDhanOrderRequest
                        {
                            AccountClientIds = new List<string> { child.DhanClientId },
                            CorrelationId = child.CorrelationId,
                            SecurityId = order.SecurityId,
                            TradingSymbol = order.TradingSymbol,
                            ExchangeSegment = order.ExchangeSegment,
                            ProductType = order.ProductType,
                            Validity = order.Validity,
                            LotSize = 1,
                            Lots = order.Quantity,
                            TransactionType = order.TransactionType,
                            OrderType = order.OrderType,
                            Price = order.OrderType == "MARKET" || order.OrderType == "STOP_LOSS_MARKET" ? null : order.Price,
                            TriggerPrice = order.TriggerPrice > 0 ? order.TriggerPrice : null
                        };

                        var results = await _orderService.PlaceOrderForAccountsAsync(request, cancellationToken);
                        result = results.FirstOrDefault() ?? FailedResult(child, "Order service returned no result for the child account.");
                    }
                }
                catch (ArgumentException ex)
                {
                    permanentFailure = true;
                    _logger.LogWarning(ex, "Copy order {MasterOrderId} is not supported for child {ClientId}", order.MasterOrderId, child.DhanClientId);
                    result = FailedResult(child, ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Copy order {MasterOrderId} failed for child {ClientId}", order.MasterOrderId, child.DhanClientId);
                    result = FailedResult(child, ex.Message);
                }

                await _store.UpdateChildSubmissionAsync(
                    order.MasterClientId, order.MasterOrderId, child.DhanClientId, result, permanentFailure, cancellationToken);
            }
        }

        private static string? ValidateCopyOrder(CopyTradingOrderRecord order)
        {
            if (string.IsNullOrWhiteSpace(order.SecurityId) || string.IsNullOrWhiteSpace(order.ExchangeSegment))
            {
                return "The master order's instrument or exchange segment is not supported for copying.";
            }
            if (string.IsNullOrWhiteSpace(order.ProductType) || string.IsNullOrWhiteSpace(order.OrderType) ||
                string.IsNullOrWhiteSpace(order.Validity) || string.IsNullOrWhiteSpace(order.TransactionType))
            {
                return "The master order contains an unsupported product, side, validity, or order type.";
            }
            if (order.ProductType is "BO" or "CO")
            {
                return "Bracket and cover orders are not supported by copy trading.";
            }
            if (order.AfterMarketOrder)
            {
                return "After-market orders are not supported by copy trading.";
            }
            if (order.DisclosedQuantity > 0)
            {
                return "Orders with a disclosed quantity are not supported by copy trading.";
            }
            if (order.Quantity <= 0)
            {
                return "The master order quantity is invalid.";
            }
            if ((order.OrderType is "LIMIT" or "STOP_LOSS") && order.Price <= 0)
            {
                return "The master limit/stop-loss order price is invalid.";
            }
            if ((order.OrderType is "STOP_LOSS" or "STOP_LOSS_MARKET") && order.TriggerPrice <= 0)
            {
                return "The master stop-loss order trigger price is invalid.";
            }
            return null;
        }

        private static DhanOrderResultDto FailedResult(CopyTradingChildOrderRecord child, string message) => new()
        {
            AccountName = child.AccountName,
            DhanClientId = child.DhanClientId,
            Success = false,
            ErrorMessage = message
        };

        private static string CreateCorrelationId(string masterClientId, string orderId, string childClientId)
        {
            var source = Encoding.UTF8.GetBytes($"{masterClientId}:{orderId}:{childClientId}");
            var hash = Convert.ToHexString(SHA256.HashData(source));
            return $"CT{hash[..28]}";
        }

        private static string MapExchangeSegment(string exchange, string segment)
        {
            var normalizedExchange = exchange.Trim().ToUpperInvariant();
            var normalizedSegment = segment.Trim().ToUpperInvariant();
            return normalizedExchange switch
            {
                "NSE" => normalizedSegment switch
                {
                    "D" => "NSE_FNO",
                    "E" => "NSE_EQ",
                    "C" => "NSE_CURRENCY",
                    _ => string.Empty
                },
                "BSE" => normalizedSegment switch
                {
                    "D" => "BSE_FNO",
                    "E" => "BSE_EQ",
                    "C" => "BSE_CURRENCY",
                    _ => string.Empty
                },
                "MCX" => "MCX_COMM",
                _ => string.Empty
            };
        }

        private static string MapProductType(string productName, string productCode) =>
            FirstNonEmpty(productName.ToUpperInvariant(), productCode.ToUpperInvariant()) switch
            {
                "C" or "CNC" => "CNC",
                "I" or "INTRADAY" => "INTRADAY",
                "M" or "MARGIN" => "MARGIN",
                "F" or "MTF" => "MTF",
                "V" or "CO" => "CO",
                "B" or "BO" => "BO",
                _ => string.Empty
            };

        private static string MapTransactionType(string value) =>
            value.Trim().ToUpperInvariant() switch
            {
                "B" or "BUY" => "BUY",
                "S" or "SELL" => "SELL",
                _ => string.Empty
            };

        private static string MapOrderType(string value) =>
            value.Trim().ToUpperInvariant().Replace("-", string.Empty) switch
            {
                "MKT" or "MARKET" => "MARKET",
                "LMT" or "LIMIT" => "LIMIT",
                "SL" or "STOPLOSS" => "STOP_LOSS",
                "SLM" or "STOPLOSSMARKET" => "STOP_LOSS_MARKET",
                _ => string.Empty
            };

        private static string MapValidity(string value) =>
            value.Trim().ToUpperInvariant() switch
            {
                "DAY" => "DAY",
                "IOC" => "IOC",
                _ => string.Empty
            };

        private static JsonElement GetProperty(JsonElement element, string name)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return property.Value;
                    }
                }
            }
            return default;
        }

        private static string GetText(JsonElement element, string name)
        {
            var value = GetProperty(element, name);
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.ToString(),
                _ => string.Empty
            };
        }

        private static int GetInteger(JsonElement element, string name) =>
            int.TryParse(GetText(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;

        private static decimal GetDecimal(JsonElement element, string name) =>
            decimal.TryParse(GetText(element, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;

        private static string FirstNonEmpty(params string[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
