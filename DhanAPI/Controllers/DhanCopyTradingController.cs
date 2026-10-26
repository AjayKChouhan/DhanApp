using Microsoft.AspNetCore.Mvc;
using DhanAPI.Filters;
using DhanService.Models;
using DhanService.Services;

namespace DhanAPI.Controllers
{
    [ApiController]
    [Route("api/dhantrading/copy-trading")]
    [TypeFilter(typeof(DhanApiKeyGuardFilter))]
    public class DhanCopyTradingController : ControllerBase
    {
        private readonly IDhanAccountStore _accountStore;
        private readonly IDhanTokenStore _tokenStore;
        private readonly ICopyTradingStore _copyTradingStore;
        private readonly ICopyTradingService _copyTradingService;
        private readonly CopyTradingRuntimeStatus _runtimeStatus;

        public DhanCopyTradingController(
            IDhanAccountStore accountStore,
            IDhanTokenStore tokenStore,
            ICopyTradingStore copyTradingStore,
            ICopyTradingService copyTradingService,
            CopyTradingRuntimeStatus runtimeStatus)
        {
            _accountStore = accountStore;
            _tokenStore = tokenStore;
            _copyTradingStore = copyTradingStore;
            _copyTradingService = copyTradingService;
            _runtimeStatus = runtimeStatus;
        }

        [HttpGet]
        public async Task<ActionResult<CopyTradingDashboardDto>> GetDashboard(CancellationToken cancellationToken)
        {
            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            var tokens = await _tokenStore.GetAllAsync(cancellationToken);
            var tokenByClientId = tokens.ToDictionary(token => token.DhanClientId, StringComparer.OrdinalIgnoreCase);
            var accountStatuses = accounts.Select(account =>
            {
                tokenByClientId.TryGetValue(account.DhanClientId, out var token);
                return new DhanAccountStatusDto
                {
                    AccountName = account.AccountName,
                    DhanClientId = account.DhanClientId,
                    IsConnected = token is not null && !token.IsExpired,
                    TokenExpiryUtc = token?.ExpiryTimeUtc
                };
            }).ToList();

            var settings = await _copyTradingStore.GetSettingsAsync(cancellationToken);
            var listenerMessage = _runtimeStatus.Get(settings.MasterClientId);
            var orders = await _copyTradingStore.GetRecentOrdersAsync(cancellationToken);

            return Ok(new CopyTradingDashboardDto
            {
                Settings = settings,
                Accounts = accountStatuses,
                Orders = orders.Take(100).ToList(),
                ListenerConnected = listenerMessage == "Listening for Dhan order updates.",
                ListenerMessage = listenerMessage
            });
        }

        [HttpPost("settings")]
        public async Task<IActionResult> SaveSettings(
            [FromBody] CopyTradingSettings settings, CancellationToken cancellationToken)
        {
            if (settings is null)
            {
                return BadRequest("Copy-trading settings are required.");
            }

            var accounts = await _accountStore.GetAllAsync(cancellationToken);
            if (accounts.Count < 2)
            {
                return BadRequest("Save at least two Dhan accounts before configuring copy trading.");
            }

            settings.Children ??= new List<CopyTradingChildSetting>();
            if (settings.Children.Count > 24)
            {
                return BadRequest("A maximum of 24 child accounts is supported.");
            }
            if (settings.Children.Any(child => child is null))
            {
                return BadRequest("Child account settings cannot be empty.");
            }

            var accountById = accounts.ToDictionary(account => account.DhanClientId, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(settings.MasterClientId))
            {
                var masterId = settings.MasterClientId.Trim();
                if (!accountById.TryGetValue(masterId, out var master))
                {
                    return BadRequest("The selected master account is not saved.");
                }
                settings.MasterClientId = master.DhanClientId;
            }
            else if (settings.Enabled)
            {
                return BadRequest("Select a master account before enabling copy trading.");
            }

            var childIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in settings.Children)
            {
                var childId = child.DhanClientId?.Trim();
                if (string.IsNullOrWhiteSpace(childId) || !accountById.TryGetValue(childId, out var savedChild))
                {
                    return BadRequest("Every child account must be a saved Dhan account.");
                }
                if (!childIds.Add(childId))
                {
                    return BadRequest("Each child account can only be configured once.");
                }
                if (string.Equals(childId, settings.MasterClientId, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("The master account cannot also be a child account.");
                }
                child.DhanClientId = savedChild.DhanClientId;
            }

            if (settings.Enabled && !settings.Children.Any(child => child.Enabled))
            {
                return BadRequest("Enable at least one child account before enabling copy trading.");
            }

            await _copyTradingService.SaveSettingsAsync(settings, cancellationToken);
            return Ok();
        }

        [HttpPost("orders/{masterClientId}/{masterOrderId}/children/{childClientId}/retry")]
        public async Task<IActionResult> RetryChildOrder(
            string masterClientId,
            string masterOrderId,
            string childClientId,
            CancellationToken cancellationToken)
        {
            var retried = await _copyTradingService.RetryChildCopyAsync(
                masterClientId, masterOrderId, childClientId, cancellationToken);
            return retried
                ? Accepted()
                : Conflict("This failed child order is no longer eligible for retry. Refresh the copy-trading page.");
        }
    }
}
