using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using DhanService.Configuration;
using DhanService.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DhanService.Services
{
    public interface IDhanBreakoutScannerService
    {
        Task<BreakoutScannerResponse> ScanAsync(CancellationToken cancellationToken);
    }

    public sealed class DhanBreakoutScannerService : IDhanBreakoutScannerService
    {
        private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
        private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(1);
        private const int MaxConcurrentRequests = 5;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanAuthService _authService;
        private readonly IDhanInstrumentService _instrumentService;
        private readonly DhanTradingOptions _options;
        private readonly ILogger<DhanBreakoutScannerService> _logger;

        public DhanBreakoutScannerService(
            IHttpClientFactory httpClientFactory,
            IDhanAccountStore accountStore,
            IDhanAuthService authService,
            IDhanInstrumentService instrumentService,
            IOptions<DhanTradingOptions> options,
            ILogger<DhanBreakoutScannerService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _accountStore = accountStore;
            _authService = authService;
            _instrumentService = instrumentService;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<BreakoutScannerResponse> ScanAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow.ToOffset(IndiaOffset);
            var instruments = (await _instrumentService.GetNseFnoUnderlyingEquitiesAsync(cancellationToken))
                .Where(IsEligibleNseFnoUnderlyingEquity)
                .GroupBy(instrument => instrument.SecurityId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (instruments.Count == 0)
            {
                throw new InvalidOperationException("No NSE F&O underlying cash equities were found in the Dhan instrument list.");
            }

            var accounts = (await _accountStore.GetAllAsync(cancellationToken))
                .Where(account => string.Equals(account.Broker, "DHAN", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (accounts.Count == 0)
            {
                throw new InvalidOperationException("No saved Dhan accounts are available for the breakout scanner.");
            }

            var credentials = new List<(DhanAccountCredential Account, Token Token)>();
            foreach (var account in accounts)
            {
                try
                {
                    var token = await _authService.EnsureValidTokenAsync(account, cancellationToken);
                    if (token is not null)
                    {
                        credentials.Add((account, token));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not refresh market-data credentials for Dhan account {Account}", account.AccountName);
                }
            }

            if (credentials.Count == 0)
            {
                throw new InvalidOperationException("No saved Dhan account has a valid access token for the breakout scanner.");
            }

            var scanResults = new ConcurrentBag<(CashEquityInstrument Instrument, InstrumentScanResult Result)>();
            await Parallel.ForEachAsync(
                instruments,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MaxConcurrentRequests
                },
                async (instrument, token) =>
                {
                    var result = await ScanInstrumentAsync(instrument, credentials, now, token);
                    scanResults.Add((instrument, result));
                });

            var completedResults = scanResults.ToList();
            var failures = completedResults
                .Where(item => item.Result.Failed)
                .Select(item => $"{item.Instrument.TradingSymbol}: {item.Result.Error}")
                .ToList();
            var signals = completedResults
                .Where(item => item.Result.Signal is not null)
                .Select(item => item.Result.Signal!)
                .ToList();
            var scannedCount = completedResults.Count(item => !item.Result.Failed);
            var rateLimited = completedResults.Any(item => item.Result.RateLimited);

            var sortedSignals = signals
                .OrderBy(signal => signal.UnderlyingSymbol, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var failedCount = failures.Count;
            var unscannedCount = instruments.Count - completedResults.Count;
            var details = string.Join("; ", failures.Take(3));
            var warning = failedCount == 0 && !rateLimited
                ? null
                : rateLimited
                    ? $"Dhan rate-limited the scan. Scanned {scannedCount} of {instruments.Count} NSE F&O underlying cash equities; {unscannedCount} were not completed. Retry on the next scan. {details}"
                    : $"Market data was unavailable for {failedCount} of {instruments.Count} NSE F&O underlying cash equities." +
                      (string.IsNullOrWhiteSpace(details) ? string.Empty : $" {details}");

            _logger.LogInformation(
                "Completed 5-minute NSE F&O underlying cash-equity scan: {Scanned}/{Total} instruments; {Failed} failed; {Signals} signals",
                scannedCount, instruments.Count, failedCount, sortedSignals.Count);

            return new BreakoutScannerResponse(
                now,
                instruments.Count,
                scannedCount,
                failedCount,
                warning,
                sortedSignals);
        }

        private static bool IsEligibleNseFnoUnderlyingEquity(CashEquityInstrument instrument) =>
            string.Equals(instrument.Exchange, "NSE", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(instrument.Segment, "E", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(instrument.InstrumentType, "EQ", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(instrument.InstrumentType, "ES", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(instrument.ExchangeSegment, "NSE_EQ", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(instrument.SecurityId) &&
            !string.IsNullOrWhiteSpace(instrument.UnderlyingSymbol);

        private async Task<InstrumentScanResult> ScanInstrumentAsync(
            CashEquityInstrument instrument,
            IReadOnlyList<(DhanAccountCredential Account, Token Token)> credentials,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var fetchResult = await FetchFiveMinuteCandlesAsync(instrument, credentials, now, cancellationToken);
            if (fetchResult.Candles is null)
            {
                return new InstrumentScanResult(
                    null, fetchResult.Error ?? "Dhan returned no usable candles.", true, fetchResult.RateLimited);
            }

            var latestCandles = fetchResult.Candles
                .Where(candle =>
                    candle.Timestamp.AddMinutes(5) <= now)
                .GroupBy(candle => candle.Timestamp)
                .Select(group => group.First())
                .OrderBy(candle => candle.Timestamp)
                .TakeLast(2)
                .ToList();

            if (latestCandles.Count < 2)
            {
                return new InstrumentScanResult(null, null, false, false);
            }

            var previous = latestCandles[0];
            var current = latestCandles[1];
            var previousAveragePrice = (previous.High + previous.Low) / 2m;
            if (previousAveragePrice <= 0)
            {
                return new InstrumentScanResult(null, null, false, false);
            }

            var changePercent = (current.Close - previousAveragePrice) / previousAveragePrice * 100m;
            var direction = changePercent >= 1m ? "LONG" : changePercent <= -1m ? "SHORT" : null;

            var signal = direction is null
                ? null
                : new BreakoutSignal(
                    instrument.SecurityId,
                    instrument.TradingSymbol,
                    instrument.UnderlyingSymbol.Trim(),
                    direction,
                    current.Timestamp,
                    changePercent,
                    current.Close,
                    previous.Close);
            return new InstrumentScanResult(signal, null, false, false);
        }

        private async Task<CandleFetchResult> FetchFiveMinuteCandlesAsync(
            CashEquityInstrument instrument,
            IReadOnlyList<(DhanAccountCredential Account, Token Token)> credentials,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Exception? lastError = null;
            foreach (var (account, token) in credentials)
            {
                try
                {
                    await Task.Delay(RequestInterval, cancellationToken);
                    var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                    var payload = new
                    {
                        securityId = instrument.SecurityId,
                        exchangeSegment = instrument.ExchangeSegment,
                        instrument = "EQUITY",
                        interval = "5",
                        oi = false,
                        fromDate = $"{today} 09:15:00",
                        toDate = $"{today} 16:00:00"
                    };

                    using var request = new HttpRequestMessage(
                        HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/charts/intraday");
                    request.Headers.Add("access-token", token.AccessToken);
                    request.Headers.Add("client-id", account.DhanClientId);
                    request.Content = JsonContent.Create(payload);

                    using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = new HttpRequestException(FormatDhanError(response.StatusCode, body));
                        if ((int)response.StatusCode == 429)
                        {
                            return new CandleFetchResult(null, lastError.Message, true);
                        }
                        continue;
                    }

                    using var document = JsonDocument.Parse(body);
                    var candles = ParseFiveMinuteCandles(document.RootElement, out var parseDetail);
                    if (candles.Count > 0)
                    {
                        return new CandleFetchResult(candles, null, false);
                    }

                    lastError = new InvalidOperationException(
                        parseDetail);
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

            _logger.LogWarning(lastError, "Could not fetch five-minute Dhan candles for {Symbol}", instrument.TradingSymbol);
            return new CandleFetchResult(null, lastError?.Message ?? "No response from Dhan.", false);
        }

        private static List<FiveMinuteCandle> ParseFiveMinuteCandles(JsonElement root, out string parseDetail)
        {
            var parsedCandles = DhanCandleDataParser.Parse(root, out var rejectedRows);
            var candles = parsedCandles
                .Select(candle => new FiveMinuteCandle(
                    DateTimeOffset.FromUnixTimeSeconds(candle.Timestamp).ToOffset(IndiaOffset),
                    candle.Open,
                    candle.High,
                    candle.Low,
                    candle.Close))
                .OrderBy(candle => candle.Timestamp)
                .ToList();

            parseDetail = candles.Count == 0
                ? $"Dhan returned no usable five-minute candles. Rejected or incomplete rows={rejectedRows}; fields={GetJsonFields(root)}."
                : string.Empty;
            return candles;
        }

        private static string FormatDhanError(System.Net.HttpStatusCode statusCode, string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                foreach (var propertyName in new[] { "remarks", "message", "errorMessage", "error", "errorCode" })
                {
                    if (document.RootElement.TryGetProperty(propertyName, out var value) &&
                        value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return $"Dhan returned {(int)statusCode} {statusCode}: {value.GetString()}";
                    }
                }
            }
            catch (JsonException)
            {
            }

            var detail = body.Length <= 160 ? body : body[..160];
            return string.IsNullOrWhiteSpace(detail)
                ? $"Dhan returned {(int)statusCode} {statusCode}."
                : $"Dhan returned {(int)statusCode} {statusCode}: {detail}";
        }

        private static string GetJsonFields(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object
                ? string.Join(", ", root.EnumerateObject().Select(property => property.Name))
                : root.ValueKind.ToString();

        private sealed record FiveMinuteCandle(
            DateTimeOffset Timestamp, decimal Open, decimal High, decimal Low, decimal Close);
        private sealed record CandleFetchResult(List<FiveMinuteCandle>? Candles, string? Error, bool RateLimited);
        private sealed record InstrumentScanResult(
            BreakoutSignal? Signal, string? Error, bool Failed, bool RateLimited);
    }
}
