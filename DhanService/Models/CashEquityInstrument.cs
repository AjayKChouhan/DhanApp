namespace DhanService.Models
{
    public sealed class CashEquityInstrument
    {
        public string SecurityId { get; set; } = string.Empty;
        public string TradingSymbol { get; set; } = string.Empty;
        public string UnderlyingSymbol { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public string Segment { get; set; } = string.Empty;
        public string InstrumentType { get; set; } = string.Empty;
        public string ExchangeSegment { get; set; } = string.Empty;
    }
}
