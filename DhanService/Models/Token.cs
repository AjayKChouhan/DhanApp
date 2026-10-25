namespace DhanService.Models
{
    /// <summary>
    /// Represents a cached Dhan access token for a single account.
    /// Persisted (encrypted) to disk so tokens survive app restarts within
    /// their 24-hour validity window.
    /// </summary>
    public class Token
    {
        public string AccountName { get; set; } = string.Empty;
        public string DhanClientId { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public DateTime ExpiryTimeUtc { get; set; }

        public bool IsExpired => DateTime.UtcNow >= ExpiryTimeUtc;

        public bool IsNearExpiry(int thresholdMinutes) =>
            DateTime.UtcNow >= ExpiryTimeUtc.AddMinutes(-thresholdMinutes);
    }
}
