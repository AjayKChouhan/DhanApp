using System.Text.Json;

namespace DhanService.Services
{
    internal static class DhanApiResponse
    {
        public static bool HasErrorStatus(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    return root.EnumerateArray().Any(HasErrorStatus);
                }

                return root.ValueKind == JsonValueKind.Object &&
                       TryGetPropertyIgnoreCase(root, "status", out var status) &&
                       status.ValueKind == JsonValueKind.String &&
                       string.Equals(status.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool HasErrorStatus(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   TryGetPropertyIgnoreCase(element, "status", out var status) &&
                   status.ValueKind == JsonValueKind.String &&
                   string.Equals(status.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}
