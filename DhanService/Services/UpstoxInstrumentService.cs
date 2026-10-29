using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using DhanService.Configuration;

namespace DhanService.Services
{
    public sealed class UpstoxInstrument
    {
        public string InstrumentKey { get; set; } = string.Empty;
        public string TradingSymbol { get; set; } = string.Empty;
        public int LotSize { get; set; }
        public int DhanLotSize { get; set; }
    }

    public interface IUpstoxInstrumentService
    {
        Task<UpstoxInstrument> ResolveFutureAsync(
            string dhanSecurityId, string exchangeSegment, CancellationToken cancellationToken = default);
    }

    public sealed class UpstoxInstrumentService : IUpstoxInstrumentService
    {
        private const string CacheKey = "DhanTrading.Upstox.InstrumentMaster";
        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly IDhanInstrumentService _dhanInstrumentService;
        private readonly DhanTradingOptions _options;

        public UpstoxInstrumentService(
            HttpClient httpClient,
            IMemoryCache cache,
            IDhanInstrumentService dhanInstrumentService,
            IOptions<DhanTradingOptions> options)
        {
            _httpClient = httpClient;
            _cache = cache;
            _dhanInstrumentService = dhanInstrumentService;
            _options = options.Value;
        }

        public async Task<UpstoxInstrument> ResolveFutureAsync(
            string dhanSecurityId, string exchangeSegment, CancellationToken cancellationToken = default)
        {
            var source = await _dhanInstrumentService.FindFutureBySecurityIdAsync(dhanSecurityId, cancellationToken);
            if (source?.ExpiryDate is null ||
                string.IsNullOrWhiteSpace(source.UnderlyingSymbol) ||
                source.LotSize <= 0)
            {
                throw new InvalidOperationException(
                    $"Could not resolve Dhan futures contract {dhanSecurityId} to its underlying, expiry, and lot size.");
            }

            var segment = MapSegment(exchangeSegment);
            var instruments = await GetInstrumentsAsync(cancellationToken);
            var candidates = instruments.Where(instrument =>
                string.Equals(instrument.Segment, segment, StringComparison.OrdinalIgnoreCase) &&
                IsFuture(instrument.InstrumentType) &&
                (string.Equals(NormalizeSymbol(instrument.ShortName), NormalizeSymbol(source.UnderlyingSymbol), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(NormalizeSymbol(instrument.Name), NormalizeSymbol(source.UnderlyingSymbol), StringComparison.OrdinalIgnoreCase) ||
                 NormalizeSymbol(instrument.TradingSymbol).StartsWith(
                     NormalizeSymbol(source.UnderlyingSymbol), StringComparison.OrdinalIgnoreCase)) &&
                TryParseExpiry(instrument.Expiry, out var expiry) &&
                expiry.Date == source.ExpiryDate.Value.Date)
                .ToList();

            if (candidates.Count != 1)
            {
                throw new InvalidOperationException(candidates.Count == 0
                    ? $"No Upstox {segment} futures contract matched {source.UnderlyingSymbol} expiring {source.ExpiryDate:yyyy-MM-dd}."
                    : $"Upstox returned multiple matching futures contracts for {source.UnderlyingSymbol} expiring {source.ExpiryDate:yyyy-MM-dd}; refusing an ambiguous mapping.");
            }

            var match = candidates[0];
            if (string.IsNullOrWhiteSpace(match.InstrumentKey) ||
                string.IsNullOrWhiteSpace(match.TradingSymbol) ||
                match.LotSize <= 0)
            {
                throw new InvalidDataException("Upstox instrument master returned an incomplete futures contract.");
            }

            return new UpstoxInstrument
            {
                InstrumentKey = match.InstrumentKey,
                TradingSymbol = match.TradingSymbol,
                LotSize = match.LotSize,
                DhanLotSize = source.LotSize
            };
        }

        private async Task<IReadOnlyList<UpstoxInstrumentEntry>> GetInstrumentsAsync(
            CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(CacheKey, out IReadOnlyList<UpstoxInstrumentEntry>? cached) &&
                cached is not null)
            {
                return cached;
            }

            using var response = await _httpClient.GetAsync(
                _options.UpstoxInstrumentMasterUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var gzipStream = new GZipStream(responseStream, CompressionMode.Decompress);
            using var document = await JsonDocument.ParseAsync(gzipStream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Upstox instrument master was not a JSON array.");
            }

            var instruments = new List<UpstoxInstrumentEntry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                instruments.Add(new UpstoxInstrumentEntry
                {
                    Segment = ReadText(item, "segment"),
                    Name = ReadText(item, "name"),
                    ShortName = ReadText(item, "short_name"),
                    InstrumentType = ReadText(item, "instrument_type"),
                    InstrumentKey = ReadText(item, "instrument_key"),
                    TradingSymbol = ReadText(item, "trading_symbol"),
                    Expiry = ReadText(item, "expiry"),
                    LotSize = ReadInteger(item, "lot_size")
                });
            }
            if (instruments.Count == 0)
            {
                throw new InvalidDataException("Upstox instrument master was empty.");
            }

            _cache.Set(CacheKey, instruments, TimeSpan.FromHours(Math.Max(1, _options.InstrumentCacheHours)));
            return instruments;
        }

        private static string MapSegment(string exchangeSegment) =>
            exchangeSegment.ToUpperInvariant() switch
            {
                "NSE_FNO" => "NSE_FO",
                "BSE_FNO" => "BSE_FO",
                "MCX_COMM" => "MCX_FO",
                _ => throw new ArgumentException(
                    $"Upstox futures copying does not support exchange segment '{exchangeSegment}'.",
                    nameof(exchangeSegment))
            };

        private static bool IsFuture(string type) =>
            type.Equals("FUT", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("FUT", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeSymbol(string? value) =>
            new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        private static bool TryParseExpiry(string value, out DateTime expiry)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            {
                try
                {
                    expiry = DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                        .ToOffset(TimeSpan.FromMinutes(330))
                        .DateTime;
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            return DateTime.TryParseExact(
                value,
                new[] { "yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out expiry);
        }

        private static string ReadText(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var value))
            {
                return string.Empty;
            }

            return value.ValueKind == JsonValueKind.String || value.ValueKind == JsonValueKind.Number
                ? value.ToString()
                : string.Empty;
        }

        private static int ReadInteger(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var value))
            {
                return 0;
            }

            return value.TryGetInt32(out var number)
                ? number
                : int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                    ? number
                    : 0;
        }

        private sealed class UpstoxInstrumentEntry
        {
            public string Segment { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string ShortName { get; set; } = string.Empty;
            public string InstrumentType { get; set; } = string.Empty;
            public string InstrumentKey { get; set; } = string.Empty;
            public string TradingSymbol { get; set; } = string.Empty;
            public string Expiry { get; set; } = string.Empty;
            public int LotSize { get; set; }
        }
    }
}
