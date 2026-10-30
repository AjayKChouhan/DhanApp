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
        /// <summary>Returns futures contracts only for the requested underlying symbols.</summary>
        Task<IReadOnlyList<FuturesInstrument>> GetFuturesForSymbolsAsync(
            string exchange, IReadOnlySet<string> underlyingSymbols, CancellationToken cancellationToken = default);
        Task<FuturesInstrument?> FindFutureBySecurityIdAsync(string securityId, CancellationToken cancellationToken = default);
        /// <summary>Returns NSE cash-equity scrips whose underlying is listed in NSE F&amp;O.</summary>
        Task<IReadOnlyList<CashEquityInstrument>> GetNseFnoUnderlyingEquitiesAsync(
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Downloads and parses Dhan's compact instrument master CSV, caching futures lists and
    /// NSE F&amp;O underlying cash equities.
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
        private static readonly string[] TradingSymbolHeaders = { "SEM_TRADING_SYMBOL", "TRADING_SYMBOL", "SYMBOL_NAME" };
        private static readonly string[] DisplayNameHeaders = { "SEM_CUSTOM_SYMBOL", "DISPLAY_NAME" };
        private static readonly string[] SymbolNameHeaders = { "SM_SYMBOL_NAME", "SYMBOL_NAME" };
        private static readonly string[] LotSizeHeaders = { "SEM_LOT_UNITS", "LOT_SIZE" };
        private static readonly string[] ExpiryDateHeaders = { "SEM_EXPIRY_DATE", "SM_EXPIRY_DATE" };
        private static readonly string[] UnderlyingSecurityIdHeaders = { "SEM_UNDERLYING_SECURITY_ID", "UNDERLYING_SECURITY_ID" };
        private static readonly string[] UnderlyingSymbolHeaders = { "SEM_UNDERLYING_SYMBOL", "UNDERLYING_SYMBOL" };
        private static readonly HashSet<string> DerivativeInstrumentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "FUT", "FUTIDX", "FUTSTK", "FUTCOM", "OPT", "OPTIDX", "OPTSTK"
        };

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
            return await GetFuturesCoreAsync(exchange, null, cancellationToken);
        }

        public async Task<IReadOnlyList<FuturesInstrument>> GetFuturesForSymbolsAsync(
            string exchange, IReadOnlySet<string> underlyingSymbols, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(underlyingSymbols);
            if (underlyingSymbols.Count == 0)
            {
                return Array.Empty<FuturesInstrument>();
            }

            return await GetFuturesCoreAsync(exchange, underlyingSymbols, cancellationToken);
        }

        private async Task<IReadOnlyList<FuturesInstrument>> GetFuturesCoreAsync(
            string exchange, IReadOnlySet<string>? underlyingSymbols, CancellationToken cancellationToken)
        {
            var normalizedExchange = string.IsNullOrWhiteSpace(exchange) ? DefaultExchange : exchange.Trim().ToUpperInvariant();
            if (!ExchangeSettings.TryGetValue(normalizedExchange, out var settings))
            {
                _logger.LogWarning("Unsupported exchange {Exchange} requested; falling back to {Default}", exchange, DefaultExchange);
                normalizedExchange = DefaultExchange;
                settings = ExchangeSettings[DefaultExchange];
            }

            var symbolFilterKey = underlyingSymbols is null
                ? string.Empty
                : $".{string.Join(",", underlyingSymbols.OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase))}";
            var cacheKey = $"DhanTrading.Futures.{normalizedExchange}{symbolFilterKey}";
            if (_cache.TryGetValue(cacheKey, out IReadOnlyList<FuturesInstrument>? cached) && cached is not null)
            {
                return cached;
            }

            var csv = await _httpClient.GetStringAsync(_options.InstrumentMasterUrl, cancellationToken);
            var instruments = Parse(csv, normalizedExchange, settings.Segment, settings.ExchangeSegmentLabel, underlyingSymbols);

            _cache.Set(cacheKey, instruments, TimeSpan.FromHours(Math.Max(1, _options.InstrumentCacheHours)));
            _logger.LogInformation("Cached {Count} {Exchange} futures instruments", instruments.Count, normalizedExchange);
            return instruments;
        }

        public async Task<FuturesInstrument?> FindFutureBySecurityIdAsync(
            string securityId, CancellationToken cancellationToken = default)
        {
            foreach (var exchange in ExchangeSettings.Keys)
            {
                var instruments = await GetFuturesAsync(exchange, cancellationToken);
                var match = instruments.FirstOrDefault(instrument =>
                    string.Equals(instrument.SecurityId, securityId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }
            return null;
        }

        public async Task<IReadOnlyList<CashEquityInstrument>> GetNseFnoUnderlyingEquitiesAsync(
            CancellationToken cancellationToken = default)
        {
            const string cacheKey = "DhanTrading.NSE.FNO.UnderlyingEquities.v2";
            if (_cache.TryGetValue(cacheKey, out IReadOnlyList<CashEquityInstrument>? cached) && cached is not null)
            {
                return cached;
            }

            var csv = await _httpClient.GetStringAsync(_options.InstrumentMasterUrl, cancellationToken);
            var instruments = ParseNseFnoUnderlyingEquities(csv);
            if (instruments.Count > 0)
            {
                _cache.Set(cacheKey, instruments, TimeSpan.FromHours(Math.Max(1, _options.InstrumentCacheHours)));
            }
            _logger.LogInformation(
                "Loaded {Count} NSE F&O underlying cash equities from {InstrumentMasterUrl}",
                instruments.Count, _options.InstrumentMasterUrl);
            return instruments;
        }

        private List<CashEquityInstrument> ParseNseFnoUnderlyingEquities(string csv)
        {
            using var reader = new StringReader(csv);
            var headerLine = reader.ReadLine();
            if (headerLine is null)
            {
                _logger.LogWarning("Dhan instrument CSV was empty; returning no NSE F&O underlying cash equities.");
                return new List<CashEquityInstrument>();
            }

            var headers = SplitCsvLine(headerLine);
            var headerIndex = headers
                .Select((header, index) => (Header: header.Trim().TrimStart('\uFEFF'), Index: index))
                .ToDictionary(item => item.Header, item => item.Index, StringComparer.OrdinalIgnoreCase);

            int ResolveIndex(string[] candidates) =>
                candidates.Select(candidate => headerIndex.TryGetValue(candidate, out var index) ? index : -1)
                    .FirstOrDefault(index => index >= 0, -1);

            var exchangeIndex = ResolveIndex(ExchIdHeaders);
            var segmentIndex = ResolveIndex(SegmentHeaders);
            var securityIdIndex = ResolveIndex(SecurityIdHeaders);
            var instrumentTypeIndex = ResolveIndex(InstrumentTypeHeaders);
            var instrumentNameIndex = ResolveIndex(InstrumentNameHeaders);
            var tradingSymbolIndex = ResolveIndex(TradingSymbolHeaders);
            var symbolNameIndex = ResolveIndex(SymbolNameHeaders);
            var underlyingSecurityIdIndex = ResolveIndex(UnderlyingSecurityIdHeaders);
            var underlyingSymbolIndex = ResolveIndex(UnderlyingSymbolHeaders);

            if (exchangeIndex < 0 || segmentIndex < 0 || securityIdIndex < 0 ||
                instrumentTypeIndex < 0 || tradingSymbolIndex < 0)
            {
                _logger.LogWarning(
                    "Dhan instrument CSV header did not match expected columns; returning no NSE F&O underlying cash equities.");
                return new List<CashEquityInstrument>();
            }

            var rows = new List<MasterInstrumentRow>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = SplitCsvLine(line);
                var requiredLength = Math.Max(
                    Math.Max(exchangeIndex, segmentIndex),
                    Math.Max(Math.Max(securityIdIndex, instrumentTypeIndex), tradingSymbolIndex));
                if (fields.Length <= requiredLength)
                {
                    continue;
                }

                rows.Add(new MasterInstrumentRow(
                    fields[exchangeIndex].Trim(),
                    fields[segmentIndex].Trim(),
                    fields[securityIdIndex].Trim(),
                    fields[instrumentTypeIndex].Trim(),
                    instrumentNameIndex >= 0 && fields.Length > instrumentNameIndex
                        ? fields[instrumentNameIndex].Trim()
                        : string.Empty,
                    fields[tradingSymbolIndex].Trim(),
                    symbolNameIndex >= 0 && fields.Length > symbolNameIndex
                        ? fields[symbolNameIndex].Trim()
                        : string.Empty,
                    underlyingSecurityIdIndex >= 0 && fields.Length > underlyingSecurityIdIndex
                        ? fields[underlyingSecurityIdIndex].Trim()
                        : string.Empty,
                    underlyingSymbolIndex >= 0 && fields.Length > underlyingSymbolIndex
                        ? fields[underlyingSymbolIndex].Trim()
                        : string.Empty));
            }

            var cashEquities = rows
                .Where(row => IsNseCashEquity(row))
                .Where(row => !string.IsNullOrWhiteSpace(row.SecurityId))
                .GroupBy(row => row.SecurityId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var equityIds = cashEquities
                .Select(row => row.SecurityId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var equitySymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var equity in cashEquities)
            {
                AddSymbolAlias(equitySymbols, equity.TradingSymbol);
                AddSymbolAlias(equitySymbols, equity.SymbolName);
            }

            var underlyingSecurityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var underlyingSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var derivativeRows = rows.Where(IsNseDerivative).ToList();
            foreach (var row in derivativeRows)
            {
                if (equityIds.Contains(row.UnderlyingSecurityId))
                {
                    underlyingSecurityIds.Add(row.UnderlyingSecurityId);
                }

                var symbol = !string.IsNullOrWhiteSpace(row.UnderlyingSymbol)
                    ? row.UnderlyingSymbol
                    : row.SymbolName;
                var normalizedSymbol = NormalizeSymbol(symbol);
                if (normalizedSymbol.Length > 0 && equitySymbols.Contains(normalizedSymbol))
                {
                    underlyingSymbols.Add(normalizedSymbol);
                }

                AddMatchingDerivativeSymbol(underlyingSymbols, row.TradingSymbol, equitySymbols);
            }

            var matchedEquities = cashEquities
                .Where(row =>
                    underlyingSecurityIds.Contains(row.SecurityId) ||
                    underlyingSymbols.Contains(NormalizeSymbol(row.TradingSymbol)) ||
                    underlyingSymbols.Contains(NormalizeSymbol(row.SymbolName)))
                .Select(row => new CashEquityInstrument
                {
                    SecurityId = row.SecurityId,
                    TradingSymbol = row.TradingSymbol,
                    UnderlyingSymbol = !string.IsNullOrWhiteSpace(row.SymbolName)
                        ? row.SymbolName
                        : NormalizeSymbol(row.TradingSymbol),
                    Exchange = row.Exchange,
                    Segment = row.Segment,
                    InstrumentType = row.InstrumentType,
                    ExchangeSegment = "NSE_EQ"
                })
                .OrderBy(instrument => instrument.UnderlyingSymbol, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _logger.LogInformation(
                "Parsed Dhan instrument master: {Rows} rows, {CashEquities} NSE cash equities, {DerivativeRows} NSE derivatives, {MatchedEquities} F&O underlying equities",
                rows.Count, cashEquities.Count, derivativeRows.Count, matchedEquities.Count);
            return matchedEquities;
        }

        private static bool IsNseCashEquity(MasterInstrumentRow row) =>
            string.Equals(row.Exchange, "NSE", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.Segment, "E", StringComparison.OrdinalIgnoreCase) &&
            (IsEquityInstrumentType(row.InstrumentName) || IsEquityInstrumentType(row.InstrumentType)) &&
            !DerivativeInstrumentTypes.Contains(row.InstrumentType) &&
            !DerivativeInstrumentTypes.Contains(row.InstrumentName);

        private static bool IsEquityInstrumentType(string instrumentType) =>
            string.Equals(instrumentType, "EQ", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(instrumentType, "EQUITY", StringComparison.OrdinalIgnoreCase);

        private static bool IsNseDerivative(MasterInstrumentRow row) =>
            string.Equals(row.Exchange, "NSE", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.Segment, "D", StringComparison.OrdinalIgnoreCase) &&
            (DerivativeInstrumentTypes.Contains(row.InstrumentType) ||
             DerivativeInstrumentTypes.Contains(row.InstrumentName));

        private static void AddSymbolAlias(HashSet<string> symbols, string value)
        {
            var symbol = NormalizeSymbol(value);
            if (symbol.Length > 0)
            {
                symbols.Add(symbol);
            }
        }

        private static void AddMatchingDerivativeSymbol(
            HashSet<string> underlyingSymbols,
            string tradingSymbol,
            HashSet<string> equitySymbols)
        {
            var normalizedTradingSymbol = NormalizeSymbol(tradingSymbol);
            foreach (var equitySymbol in equitySymbols)
            {
                if (normalizedTradingSymbol.Length > equitySymbol.Length &&
                    normalizedTradingSymbol.StartsWith(equitySymbol, StringComparison.OrdinalIgnoreCase) &&
                    normalizedTradingSymbol[equitySymbol.Length] is '-' or '_' or ' ')
                {
                    underlyingSymbols.Add(equitySymbol);
                }
            }
        }

        private static string NormalizeSymbol(string value)
        {
            var symbol = value.Trim();
            if (symbol.EndsWith("-EQ", StringComparison.OrdinalIgnoreCase))
            {
                symbol = symbol[..^3];
            }
            else if (symbol.EndsWith(".NSE", StringComparison.OrdinalIgnoreCase))
            {
                symbol = symbol[..^4];
            }
            return symbol.Trim();
        }

        private List<FuturesInstrument> Parse(
            string csv,
            string exchange,
            string segment,
            string exchangeSegmentLabel,
            IReadOnlySet<string>? underlyingSymbols = null)
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
                    var underlyingSymbol = ResolveUnderlyingSymbol(symbolName, tradingSymbol);
                    if (underlyingSymbols is not null && !underlyingSymbols.Contains(underlyingSymbol))
                    {
                        continue;
                    }

                    result.Add(new FuturesInstrument
                    {
                        SecurityId = fields[securityIdIdx].Trim(),
                        TradingSymbol = tradingSymbol,
                        DisplayName = displayName,
                        UnderlyingSymbol = underlyingSymbol,
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

        private sealed record MasterInstrumentRow(
            string Exchange,
            string Segment,
            string SecurityId,
            string InstrumentType,
            string InstrumentName,
            string TradingSymbol,
            string SymbolName,
            string UnderlyingSecurityId,
            string UnderlyingSymbol);

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
