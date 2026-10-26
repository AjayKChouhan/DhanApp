namespace DhanService.Models
{
    public class CopyTradingSettings
    {
        public bool Enabled { get; set; }
        public string? MasterClientId { get; set; }
        public List<CopyTradingChildSetting> Children { get; set; } = new();
    }

    public class CopyTradingChildSetting
    {
        public string DhanClientId { get; set; } = string.Empty;
        public bool Enabled { get; set; }
    }

    public class CopyTradingOrderRecord
    {
        public string MasterClientId { get; set; } = string.Empty;
        public string MasterAccountName { get; set; } = string.Empty;
        public string MasterOrderId { get; set; } = string.Empty;
        public string TradingSymbol { get; set; } = string.Empty;
        public string SecurityId { get; set; } = string.Empty;
        public string ExchangeSegment { get; set; } = string.Empty;
        public string ProductType { get; set; } = string.Empty;
        public string TransactionType { get; set; } = string.Empty;
        public string OrderType { get; set; } = string.Empty;
        public string Validity { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal TriggerPrice { get; set; }
        public bool AfterMarketOrder { get; set; }
        public int DisclosedQuantity { get; set; }
        public string MasterOrderStatus { get; set; } = string.Empty;
        public string? MasterOrderErrorMessage { get; set; }
        public string Source { get; set; } = string.Empty;
        public DateTime ReceivedAtUtc { get; set; }
        public List<CopyTradingChildOrderRecord> Children { get; set; } = new();
    }

    public class CopyTradingChildOrderRecord
    {
        public string DhanClientId { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string CopyState { get; set; } = "READY";
        public string? OrderId { get; set; }
        public string? OrderStatus { get; set; }
        public DateTime? LastStatusUpdateUtc { get; set; }
        public string? ErrorMessage { get; set; }
        public int? MirroredQuantity { get; set; }
        public string? MirroredOrderType { get; set; }
        public decimal? MirroredPrice { get; set; }
        public decimal? MirroredTriggerPrice { get; set; }
        public string? MirroredValidity { get; set; }
    }

    public class CopyTradingDashboardDto
    {
        public CopyTradingSettings Settings { get; set; } = new();
        public List<DhanAccountStatusDto> Accounts { get; set; } = new();
        public List<CopyTradingOrderRecord> Orders { get; set; } = new();
        public bool ListenerConnected { get; set; }
        public string? ListenerMessage { get; set; }
    }
}
