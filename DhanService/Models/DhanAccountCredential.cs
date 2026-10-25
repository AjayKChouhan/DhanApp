namespace DhanService.Models
{
    /// <summary>
    /// A saved Dhan account's credentials, persisted (encrypted) on disk.
    /// PIN and TOTP secret are encrypted at rest via ASP.NET Core Data Protection
    /// and are only decrypted in-memory when needed to generate an access token.
    /// </summary>
    public class DhanAccountCredential
    {
        public string AccountName { get; set; } = string.Empty;
        public string DhanClientId { get; set; } = string.Empty;
        public string ProtectedPin { get; set; } = string.Empty;
        public string ProtectedTotpSecret { get; set; } = string.Empty;
    }

    /// <summary>Request payload for a single account, submitted in plaintext from the setup form.</summary>
    public class SaveDhanAccountRequest
    {
        public string AccountName { get; set; } = string.Empty;
        public string DhanClientId { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
        public string TotpSecret { get; set; } = string.Empty;
    }

    /// <summary>Request payload for saving the full list of accounts at once.</summary>
    public class SaveDhanAccountsRequest
    {
        public List<SaveDhanAccountRequest> Accounts { get; set; } = new();
    }

    /// <summary>Account connection status returned to the UI (never includes PIN/TOTP secret).</summary>
    public class DhanAccountStatusDto
    {
        public string AccountName { get; set; } = string.Empty;
        public string DhanClientId { get; set; } = string.Empty;
        public bool IsConnected { get; set; }
        public DateTime? TokenExpiryUtc { get; set; }
    }
}
