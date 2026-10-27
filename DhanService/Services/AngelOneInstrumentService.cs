using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using DhanService.Configuration;
using DhanService.Models;

namespace DhanService.Services
{
    public sealed class AngelOneInstrument
    {
        public string TradingSymbol { get; set; } = string.Empty;
        public string SecurityId { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public int LotSize { get; set; }
        public int DhanLotSize { get; set; }
        public string ProductType { get; set; } = string.Empty;
        public string TransactionType { get; set; } = string.Empty;
    }

    public interface IAngelOneInstrumentService
    {
        Task<AngelOneInstrument> ResolveFutureAsync(
            string dhanSecurityId, string exchangeSegment, CancellationToken cancellationToken = default);
    }

    public sealed class AngelOneInstrumentService : IAngelOneInstrumentService
    {
        private static readonly string[] FutureTypes = { "FUTIDX", "FUTSTK", "FUTCOM", "FUT" };
        private static readonly string[] ExpiryFormats =
        {
            "ddMMMyyyy", "dd-MMM-yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "dd MMM yyyy"
        };

        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly IDhanInstrumentService _dhanInstrumentService;
        private readonly DhanTradingOptions _options;

        public AngelOneInstrumentService(
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

        public async Task<AngelOneInstrument> ResolveFutureAsync(
            string dhanSecurityId, string exchangeSegment, CancellationToken cancellationToken = default)
        {
            var source = await _dhanInstrumentService.FindFutureBySecurityIdAsync(dhanSecurityId, cancellationToken);
            if (source?.ExpiryDate is null || string.IsNullOrWhiteSpace(source.UnderlyingSymbol))
            {
                throw new InvalidOperationException(
                    $"Could not resolve Dhan futures contract {dhanSecurityId} to its underlying and expiry.");
            }

            var exchange = MapExchange(exchangeSegment);
            var master = await GetInstrumentsAsync(cancellationToken);
            var candidates = master.Where(instrument =>
                string.Equals(instrument.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
                FutureTypes.Contains(instrument.InstrumentType, StringComparer.OrdinalIgnoreCase) &&
                string.Equals(NormalizeSymbol(instrument.Name), NormalizeSymbol(source.UnderlyingSymbol), StringComparison.OrdinalIgnoreCase) &&
                TryParseExpiry(instrument.Expiry, out var expiry) &&
                expiry.Date == source.ExpiryDate.Value.Date)
                .ToList();

            if (candidates.Count != 1)
            {
                throw new InvalidOperationException(candidates.Count == 0
                    ? $"No Angel One {exchange} futures contract matched {source.UnderlyingSymbol} expiring {source.ExpiryDate:yyyy-MM-dd}."
                    : $"Angel One returned multiple matching futures contracts for {source.UnderlyingSymbol} expiring {source.ExpiryDate:yyyy-MM-dd}; refusing an ambiguous mapping.");
            }

            var match = candidates[0];
            if (string.IsNullOrWhiteSpace(match.Symbol) || string.IsNullOrWhiteSpace(match.Token))
            {
                throw new InvalidDataException("Angel One instrument master returned an incomplete contract.");
            }
            if (!TryParseLotSize(match.LotSize, out var angelLotSize) || source.LotSize <= 0)
            {
                throw new InvalidOperationException(
                    $"A valid lot size could not be resolved for {source.TradingSymbol} on both Dhan and Angel One.");
            }

            return new AngelOneInstrument
            {
                TradingSymbol = match.Symbol,
                SecurityId = match.Token,
                Exchange = exchange,
                LotSize = angelLotSize,
                DhanLotSize = source.LotSize
            };
        }

        private async Task<IReadOnlyList<AngelInstrumentMasterEntry>> GetInstrumentsAsync(
            CancellationToken cancellationToken)
        {
            const string cacheKey = "DhanTrading.AngelOne.InstrumentMaster";
            if (_cache.TryGetValue(cacheKey, out IReadOnlyList<AngelInstrumentMasterEntry>? cached) && cached is not null)
            {
                return cached;
            }

            var instruments = await _httpClient.GetFromJsonAsync<List<AngelInstrumentMasterEntry>>(
                _options.AngelOneInstrumentMasterUrl, cancellationToken);
            if (instruments is null || instruments.Count == 0)
            {
                throw new InvalidDataException("Angel One instrument master was empty.");
            }

            _cache.Set(cacheKey, instruments, TimeSpan.FromHours(Math.Max(1, _options.InstrumentCacheHours)));
            return instruments;
        }

        private static string MapExchange(string exchangeSegment) =>
            exchangeSegment.ToUpperInvariant() switch
            {
                "NSE_FNO" => "NFO",
                "BSE_FNO" => "BFO",
                "MCX_COMM" => "MCX",
                _ => throw new ArgumentException(
                    $"Angel One futures copying does not support exchange segment '{exchangeSegment}'.",
                    nameof(exchangeSegment))
            };

        private static string NormalizeSymbol(string? symbol) =>
            new string((symbol ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        private static bool TryParseExpiry(string? value, out DateTime expiry) =>
            DateTime.TryParseExact(
                value,
                ExpiryFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out expiry);

        private static bool TryParseLotSize(string? value, out int lotSize)
        {
            lotSize = 0;
            if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
                parsed <= 0 ||
                parsed != decimal.Truncate(parsed) ||
                parsed > int.MaxValue)
            {
                return false;
            }

            lotSize = (int)parsed;
            return true;
        }

        private sealed class AngelInstrumentMasterEntry
        {
            [JsonPropertyName("token")]
            public string Token { get; set; } = string.Empty;

            [JsonPropertyName("symbol")]
            public string Symbol { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("expiry")]
            public string Expiry { get; set; } = string.Empty;

            [JsonPropertyName("exch_seg")]
            public string Exchange { get; set; } = string.Empty;

            [JsonPropertyName("instrumenttype")]
            public string InstrumentType { get; set; } = string.Empty;

            [JsonPropertyName("lotsize")]
            public string LotSize { get; set; } = string.Empty;
        }
    }
}
