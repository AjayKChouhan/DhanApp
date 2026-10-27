using System.Text.Json;

namespace DhanService.Models
{
    /// <summary>Request from the UI to buy/sell a futures contract for selected saved accounts.</summary>
    public class PlaceDhanOrderRequest
    {
        public List<string> AccountClientIds { get; set; } = new();
        public string? CorrelationId { get; set; }
        public string SecurityId { get; set; } = string.Empty;
        public string TradingSymbol { get; set; } = string.Empty;
        public string ExchangeSegment { get; set; } = "NSE_FNO";
        public string ProductType { get; set; } = "MARGIN";
        public string Validity { get; set; } = "DAY";
        public int LotSize { get; set; }
        public int Lots { get; set; }

        /// <summary>"BUY" or "SELL".</summary>
        public string TransactionType { get; set; } = string.Empty;

        /// <summary>"MARKET", "LIMIT", "STOP_LOSS" or "STOP_LOSS_MARKET". Defaults to "MARKET".</summary>
        public string OrderType { get; set; } = "MARKET";

        /// <summary>Limit price. Required (and must be greater than zero) when OrderType is "LIMIT".</summary>
        public decimal? Price { get; set; }
        public decimal? TriggerPrice { get; set; }
    }

    /// <summary>Per-account result of a fanned-out order placement.</summary>
    public class DhanOrderResultDto
    {
        public string AccountName { get; set; } = string.Empty;
        public string DhanClientId { get; set; } = string.Empty;
        public string Broker { get; set; } = "DHAN";
        public bool Success { get; set; }
        public string? OrderId { get; set; }
        public string? OrderStatus { get; set; }
        public string? ErrorMessage { get; set; }
        public string? BrokerTradingSymbol { get; set; }
        public string? BrokerSecurityId { get; set; }
        public int? BrokerQuantity { get; set; }
    }

    public class ModifyDhanOrderRequest
    {
        public string DhanClientId { get; set; } = string.Empty;
        public string TradingSymbol { get; set; } = string.Empty;
        public string SecurityId { get; set; } = string.Empty;
        public string MasterSecurityId { get; set; } = string.Empty;
        public string ExchangeSegment { get; set; } = string.Empty;
        public string ProductType { get; set; } = string.Empty;
        public string TransactionType { get; set; } = string.Empty;
        public string OrderId { get; set; } = string.Empty;
        public string OrderType { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal TriggerPrice { get; set; }
        public string Validity { get; set; } = string.Empty;
    }

    /// <summary>Per-account result of a login/token-generation attempt.</summary>
    public class DhanLoginResultDto
    {
        public string AccountName { get; set; } = string.Empty;
        public string Broker { get; set; } = "DHAN";
        public string DhanClientId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public DateTime? ExpiryUtc { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>Result of fetching open positions for one account directly from Dhan.</summary>
    public class DhanPositionsResultDto
    {
        public string DhanClientId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>Raw Dhan /v2/positions response, passed through as-is.</summary>
        public JsonElement? Positions { get; set; }
    }

    /// <summary>Result of fetching the order book for one account directly from Dhan.</summary>
    public class DhanOrdersResultDto
    {
        public string DhanClientId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>Raw Dhan /v2/orders response, passed through as-is.</summary>
        public JsonElement? Orders { get; set; }
    }

    /// <summary>Result of fetching the last traded price for one instrument.</summary>
    public class DhanLtpResultDto
    {
        public string SecurityId { get; set; } = string.Empty;
        public string ExchangeSegment { get; set; } = string.Empty;
        public bool Success { get; set; }
        public decimal? LastPrice { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>Request to set/update the static IP (required for order placement) for every saved account.</summary>
    public class SetDhanStaticIpRequest
    {
        /// <summary>
        /// Static IP address (IPv4 or IPv6) to whitelist. Leave empty/omit to auto-detect this
        /// server's current public/outbound IP instead of typing it manually.
        /// </summary>
        public string? Ip { get; set; }

        /// <summary>"PRIMARY" or "SECONDARY". Defaults to "PRIMARY".</summary>
        public string IpFlag { get; set; } = "PRIMARY";
    }

    /// <summary>Per-account result of a static IP set/update attempt.</summary>
    public class DhanSetIpResultDto
    {
        public string AccountName { get; set; } = string.Empty;
        public string DhanClientId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>Per-account currently-configured static IP(s) and when each can next be changed.</summary>
    public class DhanIpStatusResultDto
    {
        public string AccountName { get; set; } = string.Empty;
        public string DhanClientId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? PrimaryIp { get; set; }
        public string? SecondaryIp { get; set; }
        public string? ModifyDatePrimary { get; set; }
        public string? ModifyDateSecondary { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
