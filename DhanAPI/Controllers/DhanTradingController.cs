using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Text.Json;
using DhanAPI.Filters;
using DhanService.Models;
using DhanService.Services;

namespace DhanAPI.Controllers
{
    /// <summary>
    /// Multi-account Dhan futures trading: save account credentials, generate/cache
    /// access tokens, browse NSE/BSE/MCX futures contracts, view positions/orders, and fan out
    /// BUY/SELL orders for selected saved accounts.
    /// </summary>
    [ApiController]
    [Route("api/dhantrading")]
    [TypeFilter(typeof(DhanApiKeyGuardFilter))]
    public class DhanTradingController : ControllerBase
    {
        private const int MaxAccounts = 25;

        private readonly ILogger<DhanTradingController> _logger;
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanAuthService _authService;
        private readonly IDhanTokenStore _tokenStore;
        private readonly IDhanInstrumentService _instrumentService;
        private readonly IDhanOrderService _orderService;
        private readonly IDhanPositionService _positionService;
        private readonly IDhanMarketQuoteService _marketQuoteService;
        private readonly IDhanMarketScannerService _marketScannerService;
        private readonly ICopyTradingStore _copyTradingStore;
        private readonly IUpstoxAuthService _upstoxAuthService;

        public DhanTradingController(
            ILogger<DhanTradingController> logger,
            IDhanAccountStore accountStore,
            IDhanAuthService authService,
            IDhanTokenStore tokenStore,
            IDhanInstrumentService instrumentService,
            IDhanOrderService orderService,
            IDhanPositionService positionService,
            IDhanMarketQuoteService marketQuoteService,
            IDhanMarketScannerService marketScannerService,
            ICopyTradingStore copyTradingStore,
            IUpstoxAuthService upstoxAuthService)
        {
            _logger = logger;
            _accountStore = accountStore;
            _authService = authService;
            _tokenStore = tokenStore;
            _instrumentService = instrumentService;
            _orderService = orderService;
            _positionService = positionService;
            _marketQuoteService = marketQuoteService;
            _marketScannerService = marketScannerService;
            _copyTradingStore = copyTradingStore;
            _upstoxAuthService = upstoxAuthService;
        }

        /// <summary>Saves (replaces) the full list of Dhan account credentials.</summary>
        [HttpPost("accounts")]
        public async Task<IActionResult> SaveAccounts([FromBody] SaveDhanAccountsRequest request, CancellationToken cancellationToken)
        {
            if (request?.Accounts is null || request.Accounts.Count == 0)
            {
                return BadRequest("At least one account is required.");
            }

            if (request.Accounts.Count > MaxAccounts)
            {
                return BadRequest($"A maximum of {MaxAccounts} accounts is supported.");
            }

            var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in request.Accounts)
            {
                if (!string.IsNullOrWhiteSpace(account.Broker) &&
                    !string.Equals(account.Broker, "DHAN", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(account.Broker, "ANGEL", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(account.Broker, "ANGELONE", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(account.Broker, "UPSTOX", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("broker must be DHAN, ANGELONE, or UPSTOX.");
                }
                var isAngelOne = string.Equals(account.Broker, "ANGELONE", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(account.Broker, "ANGEL", StringComparison.OrdinalIgnoreCase);
                var isUpstox = string.Equals(account.Broker, "UPSTOX", StringComparison.OrdinalIgnoreCase);
                var hasCredentials = isAngelOne
                    ? !string.IsNullOrWhiteSpace(account.ClientCode) &&
                      !string.IsNullOrWhiteSpace(account.Password) &&
                      !string.IsNullOrWhiteSpace(account.ApiKey) &&
                      !string.IsNullOrWhiteSpace(account.TotpSecret)
                    : isUpstox
                    ? !string.IsNullOrWhiteSpace(account.ClientCode) &&
                      !string.IsNullOrWhiteSpace(account.ApiKey) &&
                      !string.IsNullOrWhiteSpace(account.ApiSecret)
                    : !string.IsNullOrWhiteSpace(account.DhanClientId) &&
                      !string.IsNullOrWhiteSpace(account.Pin) &&
                      !string.IsNullOrWhiteSpace(account.TotpSecret);
                if (!hasCredentials)
                {
                    return BadRequest(isAngelOne
                        ? "clientCode, password, apiKey, and totpSecret are required for every Angel One account."
                        : isUpstox
                        ? "clientCode, apiKey, and apiSecret are required for every Upstox account."
                        : "dhanClientId, pin, and totpSecret are required for every Dhan account.");
                }
                var accountId = isAngelOne || isUpstox ? account.ClientCode.Trim() : account.DhanClientId.Trim();
                if (!accountIds.Add(accountId))
                {
                    return BadRequest("Each broker account ID can only be configured once.");
                }
            }

            await _accountStore.SaveAllAsync(request.Accounts, cancellationToken);
            _logger.LogInformation("Saved {Count} Dhan account(s)", request.Accounts.Count);
            return Ok();
        }

        /// <summary>Returns the saved accounts (never includes secrets), for pre-filling the setup form.</summary>
        [HttpGet("accounts")]
        public async Task<ActionResult<List<SaveDhanAccountsRequest>>> GetAccounts(CancellationToken cancellationToken)
        {
            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            var result = accounts.Select(a => new
            {
                a.AccountName,
                a.Broker,
                a.DhanClientId
            }).ToList();
            return Ok(result);
        }

        /// <summary>Returns the saved accounts and their current token connection status (never includes secrets).</summary>
        [HttpGet("accounts/status")]
        public async Task<ActionResult<List<DhanAccountStatusDto>>> GetAccountsStatus(CancellationToken cancellationToken)
        {
            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            var tokens = await _tokenStore.GetAllAsync(cancellationToken);
            var tokenByClientId = tokens.ToDictionary(t => $"{t.Broker}:{t.DhanClientId}", StringComparer.OrdinalIgnoreCase);

            var result = accounts.Select(a =>
            {
                tokenByClientId.TryGetValue($"{a.Broker}:{a.DhanClientId}", out var token);
                return new DhanAccountStatusDto
                {
                    AccountName = a.AccountName,
                    Broker = a.Broker,
                    DhanClientId = a.DhanClientId,
                    IsConnected = token is not null && !token.IsExpired,
                    TokenExpiryUtc = token?.ExpiryTimeUtc
                };
            }).ToList();

            return Ok(result);
        }

        /// <summary>
        /// Generates (or refreshes) access tokens for every saved account. Also merges in
        /// any accounts configured via "DhanTrading:Accounts" (user-secrets/Key Vault), so
        /// this alone is enough - no manual account entry required.
        /// </summary>
        [HttpPost("login")]
        public async Task<ActionResult<List<DhanLoginResultDto>>> Login(CancellationToken cancellationToken)
        {
            var seeded = await _accountStore.SeedFromConfigurationAsync(cancellationToken);
            if (seeded > 0)
            {
                _logger.LogInformation("Seeded {Count} Dhan account(s) from configuration before login", seeded);
            }

            var results = await _authService.LoginAllAsync(cancellationToken);
            return Ok(results.ToList());
        }

        [HttpPost("accounts/{clientId}/upstox/authorize")]
        public async Task<IActionResult> AuthorizeUpstoxAccount(
            string clientId, CancellationToken cancellationToken)
        {
            try
            {
                var authorizationUrl = await _upstoxAuthService.CreateAuthorizationUrlAsync(clientId, cancellationToken);
                return Ok(new { authorizationUrl });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [AllowAnonymous]
        [HttpGet("upstox/callback")]
        public async Task<IActionResult> UpstoxCallback(
            [FromQuery] string? code,
            [FromQuery] string? state,
            [FromQuery] string? error,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                return Content("Upstox authorization was not completed. Return to DhanAI and start a new connection.",
                    "text/plain");
            }

            try
            {
                var result = await _upstoxAuthService.CompleteAuthorizationAsync(
                    code ?? string.Empty, state ?? string.Empty, cancellationToken);
                return Content(
                    $"Upstox connected for {result.AccountName}. You can close this tab and return to DhanAI.",
                    "text/plain");
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or HttpRequestException or JsonException)
            {
                _logger.LogWarning(ex, "Upstox OAuth callback could not be completed.");
                return BadRequest("Upstox connection failed. Return to DhanAI, check the account and redirect URI, and try again.");
            }
        }

        /// <summary>
        /// Sets/updates the static IP (required by Dhan for order placement/modification/cancellation)
        /// for every saved account, using each account's own cached access token. If no IP is supplied,
        /// auto-detects this server's own current public/outbound IP.
        /// </summary>
        [HttpPost("accounts/set-ip")]
        public async Task<ActionResult<List<DhanSetIpResultDto>>> SetStaticIp(
            [FromBody] SetDhanStaticIpRequest request, CancellationToken cancellationToken)
        {
            var ipFlag = string.IsNullOrWhiteSpace(request?.IpFlag) ? "PRIMARY" : request.IpFlag.Trim().ToUpperInvariant();
            if (ipFlag != "PRIMARY" && ipFlag != "SECONDARY")
            {
                return BadRequest("ipFlag must be PRIMARY or SECONDARY.");
            }

            var ip = request?.Ip?.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                try
                {
                    ip = await _authService.GetPublicIpAddressAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    return StatusCode(502, $"Could not auto-detect this server's public IP, and none was provided: {ex.Message}");
                }
            }

            var results = await _authService.SetStaticIpForAllAccountsAsync(ip, ipFlag, cancellationToken);
            return Ok(results.ToList());
        }

        /// <summary>Returns this server's current public/outbound IP address (the IP Dhan sees when orders are placed).</summary>
        [HttpGet("accounts/my-ip")]
        public async Task<ActionResult<string>> GetMyIp(CancellationToken cancellationToken)
        {
            try
            {
                var ip = await _authService.GetPublicIpAddressAsync(cancellationToken);
                return Ok(ip);
            }
            catch (Exception ex)
            {
                return StatusCode(502, $"Could not detect public IP: {ex.Message}");
            }
        }

        /// <summary>Returns the currently-configured static IP(s) and next-allowed-change date for every saved account.</summary>
        [HttpGet("accounts/ip-status")]
        public async Task<ActionResult<List<DhanIpStatusResultDto>>> GetIpStatus(CancellationToken cancellationToken)
        {
            var results = await _authService.GetStaticIpStatusForAllAccountsAsync(cancellationToken);
            return Ok(results.ToList());
        }

        /// <summary>Fetches open positions for one saved account straight from Dhan (uses/refreshes its cached token).</summary>
        [HttpGet("accounts/{dhanClientId}/positions")]
        public async Task<ActionResult<DhanPositionsResultDto>> GetPositions(string dhanClientId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(dhanClientId))
            {
                return BadRequest("dhanClientId is required.");
            }

            var result = await _positionService.GetPositionsAsync(dhanClientId.Trim(), cancellationToken);
            return Ok(result);
        }

        /// <summary>Fetches the order book for one saved account straight from Dhan (uses/refreshes its cached token).</summary>
        [HttpGet("accounts/{dhanClientId}/orders")]
        public async Task<ActionResult<DhanOrdersResultDto>> GetOrders(string dhanClientId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(dhanClientId))
            {
                return BadRequest("dhanClientId is required.");
            }

            var result = await _orderService.GetOrdersAsync(dhanClientId.Trim(), cancellationToken);
            return Ok(result);
        }

        /// <summary>Returns the currently tradable futures contracts (NSE, BSE or MCX) for the dropdown.</summary>
        [HttpGet("instruments")]
        public async Task<ActionResult<List<FuturesInstrument>>> GetInstruments([FromQuery] string exchange = "NSE", CancellationToken cancellationToken = default)
        {
            var instruments = await _instrumentService.GetFuturesAsync(exchange, cancellationToken);
            return Ok(instruments.ToList());
        }

        /// <summary>Returns the last traded price for one instrument (used to prefill LIMIT order prices).</summary>
        [HttpGet("instruments/{securityId}/ltp")]
        public async Task<ActionResult<DhanLtpResultDto>> GetLtp(string securityId, [FromQuery] string exchangeSegment = "NSE_FNO", CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(securityId))
            {
                return BadRequest("securityId is required.");
            }

            var result = await _marketQuoteService.GetLtpAsync(securityId.Trim(), exchangeSegment, cancellationToken);
            return Ok(result);
        }

        /// <summary>Scans current-expiry NSE stock futures using Dhan intraday candles. This endpoint only reads market data and never places orders.</summary>
        [HttpGet("scanner/stock-futures")]
        [ProducesResponseType(typeof(MarketScannerResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<MarketScannerResponse>> ScanStockFutures(CancellationToken cancellationToken)
        {
            try
            {
                return Ok(await _marketScannerService.ScanStockFuturesAsync(cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Dhan market scan could not be completed");
                return Problem(title: "Market scan unavailable", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Dhan market-data request failed");
                return Problem(title: "Market data unavailable", detail: "The market-data provider could not complete the scan. Try again later.", statusCode: StatusCodes.Status502BadGateway);
            }
        }

        /// <summary>Places the same BUY/SELL futures order (same lots) across selected saved accounts.</summary>
        [HttpPost("order")]
        public async Task<ActionResult<List<DhanOrderResultDto>>> PlaceOrder(
            [FromBody] PlaceDhanOrderRequest request, CancellationToken cancellationToken)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.SecurityId))
            {
                return BadRequest("securityId is required.");
            }

            if (request.AccountClientIds is null || request.AccountClientIds.Count == 0 ||
                request.AccountClientIds.Any(string.IsNullOrWhiteSpace))
            {
                return BadRequest("At least one accountClientId is required.");
            }

            if (request.Lots <= 0)
            {
                return BadRequest("lots must be greater than zero.");
            }

            if (request.LotSize <= 0)
            {
                return BadRequest("lotSize must be greater than zero.");
            }

            if (!string.Equals(request.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.TransactionType, "SELL", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("transactionType must be BUY or SELL.");
            }

            var orderType = string.IsNullOrWhiteSpace(request.OrderType) ? "MARKET" : request.OrderType;
            if (!new[] { "MARKET", "LIMIT", "STOP_LOSS", "STOP_LOSS_MARKET" }
                    .Contains(orderType, StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest("orderType must be MARKET, LIMIT, STOP_LOSS, or STOP_LOSS_MARKET.");
            }

            if ((string.Equals(orderType, "LIMIT", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(orderType, "STOP_LOSS", StringComparison.OrdinalIgnoreCase)) &&
                (request.Price is null || request.Price <= 0))
            {
                return BadRequest("price must be greater than zero for LIMIT and STOP_LOSS orders.");
            }

            if ((string.Equals(orderType, "STOP_LOSS", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(orderType, "STOP_LOSS_MARKET", StringComparison.OrdinalIgnoreCase)) &&
                (request.TriggerPrice is null || request.TriggerPrice <= 0))
            {
                return BadRequest("triggerPrice must be greater than zero for stop-loss orders.");
            }

            var copySettings = await _copyTradingStore.GetSettingsAsync(cancellationToken);
            if (copySettings.Enabled &&
                request.AccountClientIds.Contains(copySettings.MasterClientId, StringComparer.OrdinalIgnoreCase))
            {
                var enabledChildren = copySettings.Children
                    .Where(child => child.Enabled)
                    .Select(child => child.DhanClientId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                request.AccountClientIds = request.AccountClientIds
                    .Where(clientId => !enabledChildren.Contains(clientId))
                    .ToList();
            }

            _logger.LogInformation("Placing {TransactionType} {OrderType} order: {Symbol} x{Lots} lots for {AccountCount} selected account(s)",
                request.TransactionType, orderType, request.TradingSymbol, request.Lots, request.AccountClientIds.Count);

            try
            {
                var results = await _orderService.PlaceOrderForAccountsAsync(request, cancellationToken);
                return Ok(results.ToList());
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
