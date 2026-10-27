using System.Globalization;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public interface IDhanMarketScannerService
    {
        Task<MarketScannerResponse> ScanStockFuturesAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Builds read-only technical scans from Dhan's real five-minute futures candles.</summary>
    public sealed class DhanMarketScannerService : IDhanMarketScannerService
    {
        private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
        private const decimal MinimumAverageDailyTurnover = 50_000_000m;
        private static readonly HashSet<string> IndexSymbols = new(StringComparer.OrdinalIgnoreCase)
        {
            "BANKEX", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTY", "SENSEX"
        };

        private readonly HttpClient _httpClient;
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanAuthService _authService;
        private readonly IDhanInstrumentService _instrumentService;
        private readonly DhanTradingOptions _options;
        private readonly ILogger<DhanMarketScannerService> _logger;

        public DhanMarketScannerService(
            HttpClient httpClient,
            IDhanAccountStore accountStore,
            IDhanAuthService authService,
            IDhanInstrumentService instrumentService,
            IOptions<DhanTradingOptions> options,
            ILogger<DhanMarketScannerService> logger)
        {
            _httpClient = httpClient;
            _accountStore = accountStore;
            _authService = authService;
            _instrumentService = instrumentService;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<MarketScannerResponse> ScanStockFuturesAsync(CancellationToken cancellationToken = default)
        {
            var instruments = await _instrumentService.GetFuturesAsync("NSE", cancellationToken);
            var indiaNow = DateTimeOffset.UtcNow.ToOffset(IndiaOffset);
            var stocks = instruments
                .Where(instrument =>
                    instrument.ExpiryDate.HasValue &&
                    instrument.ExpiryDate.Value.Date >= indiaNow.Date &&
                    !string.IsNullOrWhiteSpace(instrument.UnderlyingSymbol) &&
                    !IndexSymbols.Contains(instrument.UnderlyingSymbol.Trim()) &&
                    (string.Equals(instrument.InstrumentType, "FUTSTK", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(instrument.InstrumentType, "FUT", StringComparison.OrdinalIgnoreCase)))
                .GroupBy(instrument => instrument.UnderlyingSymbol.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(instrument => instrument.ExpiryDate).First())
                .ToList();

            if (stocks.Count == 0)
            {
                var noStockFuturesWarning = instruments.Count == 0
                    ? "The NSE futures instrument list is empty. Refresh the instrument master and try again."
                    : $"The NSE futures list contained {instruments.Count} contracts, but none were recognized as current-expiry stock futures. Check their expiry date, instrument type, and underlying symbol.";
                return new MarketScannerResponse(indiaNow, 0, 0, 0, noStockFuturesWarning, Array.Empty<MarketScannerInstrument>());
            }

            var accounts = (await _accountStore.GetAllAsync(cancellationToken))
                .Where(account => string.Equals(account.Broker, "DHAN", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (accounts.Count == 0)
            {
                throw new InvalidOperationException("No saved Dhan accounts are available for market data.");
            }

            var credentials = new List<(DhanAccountCredential Account, Token Token)>();
            foreach (var account in accounts)
            {
                var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
                if (token is not null)
                {
                    credentials.Add((account, token));
                }
            }

            if (credentials.Count == 0)
            {
                throw new InvalidOperationException("No saved Dhan account has a valid access token. Log in and retry.");
            }

            var niftyFuture = instruments
                .Where(instrument =>
                    instrument.ExpiryDate.HasValue &&
                    instrument.ExpiryDate.Value.Date >= indiaNow.Date &&
                    (string.Equals(instrument.InstrumentType, "FUTIDX", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(instrument.InstrumentType, "FUT", StringComparison.OrdinalIgnoreCase)) &&
                    (string.Equals(instrument.UnderlyingSymbol, "NIFTY", StringComparison.OrdinalIgnoreCase) ||
                     instrument.TradingSymbol.StartsWith("NIFTY-", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(instrument => instrument.ExpiryDate)
                .FirstOrDefault();

            using var concurrencyLimit = new SemaphoreSlim(4);
            var failureReasons = new ConcurrentBag<string>();
            BenchmarkSnapshot? benchmark = null;
            if (niftyFuture is not null)
            {
                var benchmarkBars = await FetchCandlesAsync(niftyFuture, credentials, failureReasons, cancellationToken);
                if (benchmarkBars is not null)
                {
                    benchmark = CreateBenchmarkSnapshot(benchmarkBars);
                }
            }

            var tasks = stocks.Select(async instrument =>
            {
                await concurrencyLimit.WaitAsync(cancellationToken);
                try
                {
                    return await ScanInstrumentAsync(instrument, credentials, indiaNow, benchmark, failureReasons, cancellationToken);
                }
                finally
                {
                    concurrencyLimit.Release();
                }
            });

            var results = await Task.WhenAll(tasks);
            var successful = results.Where(result => result is not null).Cast<MarketScannerInstrument>().ToList();
            var failedCount = stocks.Count - successful.Count;
            if (successful.Count == 0)
            {
                var reasons = string.Join("; ", failureReasons.Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
                var detail = string.IsNullOrWhiteSpace(reasons)
                    ? "Dhan returned no usable intraday candles. Confirm market-data access and try again."
                    : $"Dhan returned no usable intraday candles. {reasons}";
                throw new InvalidOperationException(detail);
            }

            var warnings = new List<string>();
            if (failedCount > 0)
            {
                warnings.Add($"Market data was unavailable for {failedCount} of {stocks.Count} contracts. Rankings use the {successful.Count} successfully scanned contracts.");
            }
            if (benchmark is null)
            {
                warnings.Add("NIFTY 50 futures data is unavailable; market-trend and relative-strength filters cannot qualify instruments.");
            }
            if (successful.Any(instrument => instrument.HistorySessions < 51))
            {
                warnings.Add("Some 50 EMA values use fewer than 51 available sessions, so their EMA warm-up may be less stable.");
            }

            return new MarketScannerResponse(indiaNow, stocks.Count, successful.Count, failedCount,
                warnings.Count == 0 ? null : string.Join(" ", warnings), successful);
        }

        private async Task<MarketScannerInstrument?> ScanInstrumentAsync(
            FuturesInstrument instrument,
            IReadOnlyList<(DhanAccountCredential Account, Token Token)> credentials,
            DateTimeOffset indiaNow,
            BenchmarkSnapshot? benchmark,
            ConcurrentBag<string> failureReasons,
            CancellationToken cancellationToken)
        {
            var bars = await FetchCandlesAsync(instrument, credentials, failureReasons, cancellationToken);
            if (bars is null)
            {
                return null;
            }

            var result = CreateScannerInstrument(instrument, bars, indiaNow, benchmark);
            if (result is null)
            {
                failureReasons.Add($"{instrument.TradingSymbol}: insufficient history or no usable session volume.");
            }
            return result;
        }

        private async Task<List<Candle>?> FetchCandlesAsync(
            FuturesInstrument instrument,
            IReadOnlyList<(DhanAccountCredential Account, Token Token)> credentials,
            ConcurrentBag<string> failureReasons,
            CancellationToken cancellationToken)
        {
            Exception? lastError = null;
            foreach (var (account, token) in credentials)
            {
                try
                {
                    var fromDate = DateTimeOffset.UtcNow.ToOffset(IndiaOffset).AddDays(-89)
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var toDate = DateTimeOffset.UtcNow.ToOffset(IndiaOffset)
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var payload = new
                    {
                        securityId = instrument.SecurityId,
                        exchangeSegment = instrument.ExchangeSegment,
                        instrument = instrument.InstrumentType,
                        expiryCode = 0,
                        interval = "5",
                        oi = false,
                        fromDate,
                        toDate
                    };

                    using var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/charts/intraday");
                    request.Headers.Add("access-token", token.AccessToken);
                    request.Headers.Add("client-id", account.DhanClientId);
                    request.Content = JsonContent.Create(payload);

                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = new HttpRequestException(FormatDhanError(response.StatusCode, body));
                        continue;
                    }

                    using var document = JsonDocument.Parse(body);
                    var bars = ParseCandles(document.RootElement);
                    if (bars.Count > 0)
                    {
                        return bars;
                    }

                    lastError = new InvalidOperationException("Dhan returned no intraday candles.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (
                    ex is HttpRequestException or JsonException or InvalidOperationException or
                    ArgumentException or FormatException or OverflowException)
                {
                    lastError = ex;
                }
            }

            _logger.LogWarning(lastError, "Could not fetch Dhan futures candles for {Symbol}", instrument.TradingSymbol);
            if (lastError is not null)
            {
                failureReasons.Add($"{instrument.TradingSymbol}: {lastError.Message}");
            }
            return null;
        }

        private static string FormatDhanError(System.Net.HttpStatusCode statusCode, string body)
        {
            var providerMessage = ExtractDhanErrorMessage(body);
            return string.IsNullOrWhiteSpace(providerMessage)
                ? $"Dhan returned {(int)statusCode} {statusCode}."
                : $"Dhan returned {(int)statusCode} {statusCode}: {providerMessage}";
        }

        private static string? ExtractDhanErrorMessage(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                foreach (var propertyName in new[] { "remarks", "message", "errorMessage", "error" })
                {
                    if (root.TryGetProperty(propertyName, out var property) &&
                        property.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(property.GetString()))
                    {
                        return property.GetString();
                    }
                }

                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in data.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        {
                            return property.Value.GetString();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            return null;
        }

        private static List<Candle> ParseCandles(JsonElement root)
        {
            var opens = ReadDecimalArray(root, "open");
            var highs = ReadDecimalArray(root, "high");
            var lows = ReadDecimalArray(root, "low");
            var closes = ReadDecimalArray(root, "close");
            var volumes = ReadDecimalArray(root, "volume");
            var timestamps = ReadLongArray(root, "timestamp");
            var count = new[] { opens.Count, highs.Count, lows.Count, closes.Count, volumes.Count, timestamps.Count }.Min();

            return Enumerable.Range(0, count)
                .Select(index => new Candle(
                    DateTimeOffset.FromUnixTimeSeconds(timestamps[index]).ToOffset(IndiaOffset),
                    opens[index],
                    highs[index],
                    lows[index],
                    closes[index],
                    volumes[index]))
                .OrderBy(candle => candle.Timestamp)
                .ToList();
        }

        private static BenchmarkSnapshot? CreateBenchmarkSnapshot(IReadOnlyList<Candle> bars)
        {
            var sessions = CreateDailySessions(bars);
            if (sessions.Count < 15)
            {
                return null;
            }

            var closes = sessions.Select(session => session.Close).ToList();
            var returnPercent = CalculateReturnPercent(closes);
            return returnPercent is null
                ? null
                : new BenchmarkSnapshot(
                    returnPercent.Value,
                    closes[^1] > CalculateEma(closes, 20) && CalculateEma(closes, 20) > CalculateEma(closes, 50),
                    closes[^1] < CalculateEma(closes, 20) && CalculateEma(closes, 20) < CalculateEma(closes, 50));
        }

        private static MarketScannerInstrument? CreateScannerInstrument(
            FuturesInstrument instrument,
            IReadOnlyList<Candle> bars,
            DateTimeOffset indiaNow,
            BenchmarkSnapshot? benchmark)
        {
            if (bars.Count == 0)
            {
                return null;
            }

            var sessions = CreateDailySessions(bars);

            if (sessions.Count < 15)
            {
                return null;
            }

            var latest = bars[^1];
            var latestSessionBars = bars.Where(bar => bar.Timestamp.Date == latest.Timestamp.Date).ToList();
            var previousSession = sessions[^2];
            var closes = sessions.Select(session => session.Close).ToList();
            var ema20 = CalculateEma(closes, 20);
            var ema50 = CalculateEma(closes, 50);
            var rsi = CalculateRsi(closes, 14);
            var macdHistogram = CalculateMacdHistogram(closes);
            var atr = CalculateAtr(sessions, 14);
            var vwapVolume = latestSessionBars.Sum(bar => bar.Volume);
            if (vwapVolume <= 0)
            {
                return null;
            }

            var vwap = latestSessionBars.Sum(bar => ((bar.High + bar.Low + bar.Close) / 3m) * bar.Volume) / vwapVolume;
            var priorSessions = sessions.Take(sessions.Count - 1).TakeLast(20).ToList();
            var averageVolume = priorSessions.Count == 0 ? 0m : priorSessions.Average(session => session.Volume);
            var sessionMinutes = (latest.Timestamp.TimeOfDay - new TimeSpan(9, 15, 0)).TotalMinutes;
            var elapsedFraction = latest.Timestamp.Date == indiaNow.Date
                ? Math.Clamp((decimal)(sessionMinutes / 375d), 1m / 75m, 1m)
                : 1m;
            var volumeConfirmed = averageVolume > 0 &&
                                  vwapVolume >= averageVolume * elapsedFraction * 1.2m;
            var averageDailyTurnover = averageVolume * latest.Close;
            var liquid = averageDailyTurnover >= MinimumAverageDailyTurnover;
            var longEntry = latest.Close > previousSession.High ? previousSession.High : latest.High;
            var shortEntry = latest.Close < previousSession.Low ? previousSession.Low : latest.Low;
            var longStop = longEntry - atr;
            var shortStop = shortEntry + atr;
            var longTarget = longEntry + atr * 2m;
            var shortTarget = shortEntry - atr * 2m;
            var longRiskReward = longEntry > longStop && longEntry > 0
                ? (longTarget - longEntry) / (longEntry - longStop)
                : 0m;
            var shortRiskReward = shortStop > shortEntry && shortTarget > 0
                ? (shortEntry - shortTarget) / (shortStop - shortEntry)
                : 0m;
            var longPullback = latest.Close > ema20 &&
                               latest.Low <= ema20 + atr * 0.25m &&
                               latest.Close > latest.Open;
            var shortPullback = latest.Close < ema20 &&
                                latest.High >= ema20 - atr * 0.25m &&
                                latest.Close < latest.Open;
            var stockReturn = CalculateReturnPercent(closes);
            decimal? relativeStrength = stockReturn is not null && benchmark is not null
                ? stockReturn.Value - benchmark.ReturnPercent
                : null;

            return new MarketScannerInstrument(
                instrument.SecurityId,
                instrument.TradingSymbol,
                instrument.UnderlyingSymbol,
                latest.Timestamp,
                latest.Close,
                vwap,
                ema20,
                ema50,
                rsi,
                macdHistogram,
                atr,
                vwapVolume,
                averageVolume,
                previousSession.High,
                previousSession.Low,
                sessions.Count,
                relativeStrength,
                averageDailyTurnover,
                longRiskReward,
                shortRiskReward,
                liquid,
                benchmark?.LongMarketTrend ?? false,
                benchmark?.ShortMarketTrend ?? false,
                relativeStrength > 0,
                relativeStrength < 0,
                latest.Close > ema20 && ema20 > ema50,
                latest.Close < ema20 && ema20 < ema50,
                latest.Close > vwap,
                latest.Close < vwap,
                latest.Close > ema20,
                latest.Close < ema20,
                latest.Close > ema50,
                latest.Close < ema50,
                rsi >= 50m && rsi <= 70m,
                rsi >= 30m && rsi <= 50m,
                volumeConfirmed,
                latest.Close > previousSession.High,
                latest.Close < previousSession.Low,
                longPullback,
                shortPullback,
                longEntry,
                shortEntry,
                longStop,
                shortStop,
                longTarget,
                shortTarget,
                macdHistogram > 0,
                macdHistogram < 0);
        }

        private static List<DailyCandle> CreateDailySessions(IReadOnlyList<Candle> bars) =>
            bars.GroupBy(bar => bar.Timestamp.Date)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    var ordered = group.OrderBy(bar => bar.Timestamp).ToList();
                    return new DailyCandle(
                        ordered[0].Timestamp.Date,
                        ordered[0].Open,
                        ordered.Max(bar => bar.High),
                        ordered.Min(bar => bar.Low),
                        ordered[^1].Close,
                        ordered.Sum(bar => bar.Volume));
                })
                .ToList();

        private static decimal? CalculateReturnPercent(IReadOnlyList<decimal> closes)
        {
            var baselineIndex = Math.Max(0, closes.Count - 21);
            if (closes.Count < 2 || closes[baselineIndex] <= 0)
            {
                return null;
            }

            var baseline = closes[baselineIndex];
            return (closes[^1] / baseline - 1m) * 100m;
        }

        private static decimal CalculateEma(IReadOnlyList<decimal> values, int period)
        {
            var multiplier = 2m / (period + 1);
            var ema = values[0];
            for (var index = 1; index < values.Count; index++)
            {
                ema = (values[index] - ema) * multiplier + ema;
            }

            return ema;
        }

        private static decimal CalculateRsi(IReadOnlyList<decimal> closes, int period)
        {
            var averageGain = 0m;
            var averageLoss = 0m;
            for (var index = 1; index <= period; index++)
            {
                var change = closes[index] - closes[index - 1];
                averageGain += Math.Max(change, 0m);
                averageLoss += Math.Max(-change, 0m);
            }

            averageGain /= period;
            averageLoss /= period;
            for (var index = period + 1; index < closes.Count; index++)
            {
                var change = closes[index] - closes[index - 1];
                averageGain = (averageGain * (period - 1) + Math.Max(change, 0m)) / period;
                averageLoss = (averageLoss * (period - 1) + Math.Max(-change, 0m)) / period;
            }

            return averageLoss == 0 ? 100m : 100m - 100m / (1m + averageGain / averageLoss);
        }

        private static decimal CalculateMacdHistogram(IReadOnlyList<decimal> closes)
        {
            var fast = CalculateEmaSeries(closes, 12);
            var slow = CalculateEmaSeries(closes, 26);
            var macd = Enumerable.Range(0, closes.Count).Select(index => fast[index] - slow[index]).ToList();
            var signal = CalculateEmaSeries(macd, 9);
            return macd[^1] - signal[^1];
        }

        private static List<decimal> CalculateEmaSeries(IReadOnlyList<decimal> values, int period)
        {
            var result = new List<decimal>(values.Count);
            var multiplier = 2m / (period + 1);
            var ema = values[0];
            result.Add(ema);
            for (var index = 1; index < values.Count; index++)
            {
                ema = (values[index] - ema) * multiplier + ema;
                result.Add(ema);
            }

            return result;
        }

        private static decimal CalculateAtr(IReadOnlyList<DailyCandle> sessions, int period)
        {
            var trueRanges = new List<decimal>();
            for (var index = 1; index < sessions.Count; index++)
            {
                var current = sessions[index];
                var previousClose = sessions[index - 1].Close;
                trueRanges.Add(Math.Max(
                    current.High - current.Low,
                    Math.Max(Math.Abs(current.High - previousClose), Math.Abs(current.Low - previousClose))));
            }

            return trueRanges.TakeLast(period).Average();
        }

        private static List<decimal> ReadDecimalArray(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return new List<decimal>();
            }

            return array.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.Number)
                .Select(value => value.GetDecimal())
                .ToList();
        }

        private static List<long> ReadLongArray(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return new List<long>();
            }

            return array.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _))
                .Select(value => value.GetInt64())
                .ToList();
        }

        private sealed record Candle(
            DateTimeOffset Timestamp,
            decimal Open,
            decimal High,
            decimal Low,
            decimal Close,
            decimal Volume);

        private sealed record DailyCandle(
            DateTimeOffset Date,
            decimal Open,
            decimal High,
            decimal Low,
            decimal Close,
            decimal Volume);

        private sealed record BenchmarkSnapshot(
            decimal ReturnPercent,
            bool LongMarketTrend,
            bool ShortMarketTrend);
    }
}
