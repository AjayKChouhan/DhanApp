namespace DhanService.Models
{
    public sealed record BreakoutSignal(
        string SecurityId,
        string TradingSymbol,
        string UnderlyingSymbol,
        string Direction,
        DateTimeOffset CandleTime,
        decimal PriceChangePercent,
        decimal Close,
        decimal PreviousClose);

    public sealed record BreakoutScannerResponse(
        DateTimeOffset? LastScannedAt,
        int InstrumentCount,
        int ScannedCount,
        int FailedCount,
        string? Warning,
        IReadOnlyList<BreakoutSignal> Signals);

}
