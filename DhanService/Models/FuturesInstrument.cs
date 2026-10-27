namespace DhanService.Models
{
    /// <summary>An NSE futures contract available for trading, sourced from Dhan's instrument master.</summary>
    public class FuturesInstrument
    {
        public string SecurityId { get; set; } = string.Empty;
        public string TradingSymbol { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string UnderlyingSymbol { get; set; } = string.Empty;
        public string InstrumentType { get; set; } = string.Empty;
        public int LotSize { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public string ExchangeSegment { get; set; } = "NSE_FNO";
    }
}
