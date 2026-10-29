using System.Globalization;
using System.Text.Json;

namespace DhanService.Services
{
    internal static class DhanCandleDataParser
    {
        public static List<DhanCandleData> Parse(JsonElement root, out int rejectedCount)
        {
            var opens = ReadArray(root, "open");
            var highs = ReadArray(root, "high");
            var lows = ReadArray(root, "low");
            var closes = ReadArray(root, "close");
            var volumes = ReadArray(root, "volume");
            var timestamps = ReadArray(root, "timestamp");
            var arrays = new[] { opens, highs, lows, closes, volumes, timestamps };
            var count = arrays.Min(array => array.Count);
            rejectedCount = arrays.Max(array => array.Count) - count;

            var candles = new List<DhanCandleData>(count);
            for (var index = 0; index < count; index++)
            {
                if (!TryReadDecimal(opens[index], out var open) ||
                    !TryReadDecimal(highs[index], out var high) ||
                    !TryReadDecimal(lows[index], out var low) ||
                    !TryReadDecimal(closes[index], out var close) ||
                    !TryReadDecimal(volumes[index], out var volume) ||
                    !TryReadTimestamp(timestamps[index], out var timestamp))
                {
                    rejectedCount++;
                    continue;
                }

                candles.Add(new DhanCandleData(timestamp, open, high, low, close, volume));
            }

            return candles;
        }

        private static List<JsonElement> ReadArray(JsonElement root, string propertyName)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.Array)
            {
                return new List<JsonElement>();
            }

            return property.EnumerateArray().ToList();
        }

        private static bool TryReadDecimal(JsonElement value, out decimal result)
        {
            result = 0;
            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.TryGetDecimal(out result);
            }

            return value.ValueKind == JsonValueKind.String &&
                decimal.TryParse(
                    value.GetString(),
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out result);
        }

        private static bool TryReadTimestamp(JsonElement value, out long result)
        {
            result = 0;
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt64(out result))
                {
                    return IsUnixTimestampInRange(result);
                }

                if (value.TryGetDecimal(out var decimalTimestamp) &&
                    decimal.Truncate(decimalTimestamp) == decimalTimestamp &&
                    decimalTimestamp >= long.MinValue &&
                    decimalTimestamp <= long.MaxValue)
                {
                    result = (long)decimalTimestamp;
                    return IsUnixTimestampInRange(result);
                }
            }
            else if (value.ValueKind == JsonValueKind.String &&
                     long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return IsUnixTimestampInRange(result);
            }

            return false;
        }

        private static bool IsUnixTimestampInRange(long timestamp) =>
            timestamp >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
            timestamp <= DateTimeOffset.MaxValue.ToUnixTimeSeconds();
    }

    internal sealed record DhanCandleData(
        long Timestamp,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume);
}
