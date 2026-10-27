using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public interface IDhanInstrumentService
    {
        /// <summary>Returns the list of currently tradable futures (index + stock) contracts for the given exchange (NSE, BSE or MCX).</summary>
        Task<IReadOnlyList<FuturesInstrument>> GetFuturesAsync(string exchange, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Downloads and parses Dhan's compact instrument master CSV, caching the
    /// filtered futures list (per exchange) in memory to avoid re-downloading on every request.
    /// </summary>
    public class DhanInstrumentService : IDhanInstrumentService
    {
        private const string DefaultExchange = "NSE";

        // Supported exchanges and the instrument-master segment/exchangeSegment label that
        // identifies their derivatives/futures rows. NSE and BSE futures live in the "D"
        // (Derivatives) segment; MCX commodity futures live in the "M" (Commodity) segment.
        private static readonly Dictionary<string, (string Segment, string ExchangeSegmentLabel)> ExchangeSettings =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["NSE"] = ("D", "NSE_FNO"),
                ["BSE"] = ("D", "BSE_FNO"),
                ["MCX"] = ("M", "MCX_COMM"),
            };

        // Dhan's instrument master historically used "FUTIDX"/"FUTSTK" in SEM_EXCH_INSTRUMENT_TYPE;
        // it now uses the single generic code "FUT" for both index and stock futures. Accept all
        // three (plus the legacy commodity code) so this keeps working regardless of which
        // schema is currently being served.
        private static readonly string[] FutureInstrumentTypes = { "FUT", "FUTIDX", "FUTSTK", "FUTCOM" };

        // Candidate header names - Dhan's compact CSV uses the SEM_* raw column names.
        private static readonly string[] ExchIdHeaders = { "SEM_EXM_EXCH_ID", "EXCH_ID" };
        private static readonly string[] SegmentHeaders = { "SEM_SEGMENT", "SEGMENT" };
        private static readonly string[] SecurityIdHeaders = { "SEM_SMST_SECURITY_ID", "SECURITY_ID" };
        private static readonly string[] InstrumentTypeHeaders = { "SEM_EXCH_INSTRUMENT_TYPE", "INSTRUMENT_TYPE" };
        private static readonly string[] InstrumentNameHeaders = { "SEM_INSTRUMENT_NAME", "INSTRUMENT" };
        private static readonly string[] TradingSymbolHeaders = { "SEM_TRADING_SYMBOL", "TRADING_SYMBOL" };
        private static readonly string[] DisplayNameHeaders = { "SEM_CUSTOM_SYMBOL", "DISPLAY_NAME" };
        private static readonly string[] SymbolNameHeaders = { "SM_SYMBOL_NAME", "SYMBOL_NAME" };
        private static readonly string[] LotSizeHeaders = { "SEM_LOT_UNITS", "LOT_SIZE" };
        private static readonly string[] ExpiryDateHeaders = { "SEM_EXPIRY_DATE", "SM_EXPIRY_DATE" };

        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly DhanTradingOptions _options;
        private readonly ILogger<DhanInstrumentService> _logger;

        public DhanInstrumentService(
            HttpClient httpClient,
            IMemoryCache cache,
            IOptions<DhanTradingOptions> options,
            ILogger<DhanInstrumentService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<IReadOnlyList<FuturesInstrument>> GetFuturesAsync(string exchange, CancellationToken cancellationToken = default)
        {
            var normalizedExchange = string.IsNullOrWhiteSpace(exchange) ? DefaultExchange : exchange.Trim().ToUpperInvariant();
            if (!ExchangeSettings.TryGetValue(normalizedExchange, out var settings))
            {
                _logger.LogWarning("Unsupported exchange {Exchange} requested; falling back to {Default}", exchange, DefaultExchange);
                normalizedExchange = DefaultExchange;
                settings = ExchangeSettings[DefaultExchange];
            }

            var cacheKey = $"DhanTrading.Futures.{normalizedExchange}";
            if (_cache.TryGetValue(cacheKey, out IReadOnlyList<FuturesInstrument>? cached) && cached is not null)
            {
                return cached;
            }

            var csv = await _httpClient.GetStringAsync(_options.InstrumentMasterUrl, cancellationToken);
            var instruments = Parse(csv, normalizedExchange, settings.Segment, settings.ExchangeSegmentLabel);

            _cache.Set(cacheKey, instruments, TimeSpan.FromHours(Math.Max(1, _options.InstrumentCacheHours)));
            _logger.LogInformation("Cached {Count} {Exchange} futures instruments", instruments.Count, normalizedExchange);
            return instruments;
        }

        private List<FuturesInstrument> Parse(string csv, string exchange, string segment, string exchangeSegmentLabel)
        {
            var result = new List<FuturesInstrument>();
            using var reader = new StringReader(csv);

            var headerLine = reader.ReadLine();
            if (headerLine is null)
            {
                return result;
            }

            var headers = SplitCsvLine(headerLine);
            var headerIndex = headers
                .Select((h, i) => (Header: h.Trim(), Index: i))
                .ToDictionary(x => x.Header, x => x.Index, StringComparer.OrdinalIgnoreCase);

            int ResolveIndex(string[] candidates) =>
                candidates.Select(c => headerIndex.TryGetValue(c, out var idx) ? idx : -1).FirstOrDefault(idx => idx >= 0, -1);

            var exchIdIdx = ResolveIndex(ExchIdHeaders);
            var segmentIdx = ResolveIndex(SegmentHeaders);
            var securityIdIdx = ResolveIndex(SecurityIdHeaders);
            var instrumentTypeIdx = ResolveIndex(InstrumentTypeHeaders);
            var instrumentNameIdx = ResolveIndex(InstrumentNameHeaders);
            var tradingSymbolIdx = ResolveIndex(TradingSymbolHeaders);
            var displayNameIdx = ResolveIndex(DisplayNameHeaders);
            var symbolNameIdx = ResolveIndex(SymbolNameHeaders);
            var lotSizeIdx = ResolveIndex(LotSizeHeaders);
            var expiryDateIdx = ResolveIndex(ExpiryDateHeaders);

            if (exchIdIdx < 0 || segmentIdx < 0 || securityIdIdx < 0 || instrumentTypeIdx < 0)
            {
                _logger.LogWarning("Dhan instrument CSV header did not match expected columns; returning empty futures list.");
                return result;
            }

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var fields = SplitCsvLine(line);
                    if (fields.Length <= exchIdIdx || fields.Length <= segmentIdx || fields.Length <= instrumentTypeIdx)
                    {
                        continue;
                    }

                    var exchId = fields[exchIdIdx].Trim();
                    var rowSegment = fields[segmentIdx].Trim();
                    var instrumentType = fields[instrumentTypeIdx].Trim();
                    var instrumentName = instrumentNameIdx >= 0 && fields.Length > instrumentNameIdx
                        ? fields[instrumentNameIdx].Trim()
                        : instrumentType;

                    if (!string.Equals(exchId, exchange, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(rowSegment, segment, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!FutureInstrumentTypes.Contains(instrumentType, StringComparer.OrdinalIgnoreCase)) continue;

                    var lotSizeRaw = lotSizeIdx >= 0 && fields.Length > lotSizeIdx ? fields[lotSizeIdx] : "0";
                    // SEM_LOT_UNITS is sometimes a plain integer ("1100") and sometimes a decimal
                    // string ("1100.0"), so parse as a float/decimal first and truncate to int.
                    decimal.TryParse(lotSizeRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var lotSizeDecimal);
                    var lotSize = (int)lotSizeDecimal;
                    if (lotSize <= 0)
                    {
                        continue;
                    }

                    var expiryRaw = expiryDateIdx >= 0 && fields.Length > expiryDateIdx ? fields[expiryDateIdx] : null;
                    DateTime.TryParse(expiryRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiryDate);

                    var symbolName = symbolNameIdx >= 0 && fields.Length > symbolNameIdx ? fields[symbolNameIdx].Trim() : string.Empty;
                    var tradingSymbol = tradingSymbolIdx >= 0 && fields.Length > tradingSymbolIdx
                        ? fields[tradingSymbolIdx].Trim()
                        : string.Empty;
                    var displayName = displayNameIdx >= 0 && fields.Length > displayNameIdx
                        ? fields[displayNameIdx].Trim()
                        : symbolName;

                    result.Add(new FuturesInstrument
                    {
                        SecurityId = fields[securityIdIdx].Trim(),
                        TradingSymbol = tradingSymbol,
                        DisplayName = displayName,
                        UnderlyingSymbol = ResolveUnderlyingSymbol(symbolName, tradingSymbol),
                        InstrumentType = instrumentName,
                        LotSize = lotSize,
                        ExpiryDate = expiryDate == default ? null : expiryDate,
                        ExchangeSegment = exchangeSegmentLabel
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping unparseable instrument CSV row");
                }
            }

            return result
                .Where(i => !string.IsNullOrEmpty(i.SecurityId))
                .OrderBy(i => i.UnderlyingSymbol)
                .ThenBy(i => i.ExpiryDate)
                .ToList();
        }

        private static string ResolveUnderlyingSymbol(string symbolName, string tradingSymbol)
        {
            if (!string.IsNullOrWhiteSpace(symbolName))
            {
                return symbolName;
            }

            var expirySeparator = tradingSymbol.IndexOf('-', StringComparison.Ordinal);
            return expirySeparator > 0 ? tradingSymbol[..expirySeparator].Trim() : string.Empty;
        }

        /// <summary>Minimal CSV line splitter that understands double-quoted fields.</summary>
        private static string[] SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            var inQuotes = false;

            foreach (var c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}
