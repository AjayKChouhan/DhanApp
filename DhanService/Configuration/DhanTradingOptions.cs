using DhanService.Models;

namespace DhanService.Configuration
{
    /// <summary>
    /// Configuration for the Dhan multi-account futures trading module.
    /// Bound from the "DhanTrading" section of appsettings.json.
    /// </summary>
    public class DhanTradingOptions
    {
        /// <summary>
        /// Optional: account credentials configured directly (via `dotnet user-secrets`
        /// locally, or Key Vault in production) instead of entered through the UI.
        /// Every time Login is called, these are merged into the saved account store
        /// (config values win on conflict), so clicking "Login" alone is enough -
        /// no manual account entry required. Configure under "DhanTrading:Accounts".
        /// </summary>
        public List<SaveDhanAccountRequest> Accounts { get; set; } = new();

        /// <summary>Base URL for Dhan authentication endpoints.</summary>
        public string AuthBaseUrl { get; set; } = "https://auth.dhan.co";

        /// <summary>Base URL for Dhan trading/data endpoints.</summary>
        public string ApiBaseUrl { get; set; } = "https://api.dhan.co";

        /// <summary>Base URL for Angel One SmartAPI endpoints.</summary>
        public string AngelOneApiBaseUrl { get; set; } = "https://apiconnect.angelone.in";

        /// <summary>Angel One scrip-master JSON URL used to resolve equivalent futures contracts.</summary>
        public string AngelOneInstrumentMasterUrl { get; set; } =
            "https://margincalculator.angelbroking.com/OpenAPI_File/files/OpenAPIScripMaster.json";

        /// <summary>Base URL for Upstox trading endpoints.</summary>
        public string UpstoxApiBaseUrl { get; set; } = "https://api-hft.upstox.com";

        /// <summary>Upstox BOD instrument master used to resolve equivalent futures contracts.</summary>
        public string UpstoxInstrumentMasterUrl { get; set; } =
            "https://assets.upstox.com/market-quote/instruments/exchange/complete.json.gz";

        /// <summary>
        /// Registered Upstox OAuth callback URL. It must match the DhanAPI callback endpoint
        /// and the redirect URI configured for the Upstox developer application.
        /// </summary>
        public string UpstoxRedirectUri { get; set; } =
            "http://localhost:5050/api/dhantrading/upstox/callback";

        /// <summary>URL of the Dhan detailed instrument master CSV.</summary>
        public string InstrumentMasterUrl { get; set; } =
            "https://images.dhan.co/api-data/api-scrip-master-detailed.csv";

        /// <summary>
        /// Optional shared-secret API key. When set, requests to the DhanTrading
        /// controller must include a matching "X-Dhan-Api-Key" header.
        /// Leave empty to disable this check (not recommended for anything but local dev).
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>Folder (relative to content root) used to persist encrypted accounts/tokens.</summary>
        public string DataFolder { get; set; } = "App_Data/DhanTrading";

        /// <summary>How long the parsed instrument list is cached in memory before re-downloading.</summary>
        public int InstrumentCacheHours { get; set; } = 6;

        /// <summary>How close to expiry (minutes) a token must be before it is proactively refreshed.</summary>
        public int TokenRefreshThresholdMinutes { get; set; } = 10;
    }
}
