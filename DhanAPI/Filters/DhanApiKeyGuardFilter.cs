using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DhanService.Configuration;

namespace DhanAPI.Filters
{
    /// <summary>
    /// Lightweight guard for the DhanTrading controller. When "DhanTrading:ApiKey"
    /// is configured, every request must include a matching "X-Dhan-Api-Key" header.
    /// This is defense-in-depth only (the key is also known to the browser client) -
    /// it is NOT a substitute for real user authentication.
    /// </summary>
    public class DhanApiKeyGuardFilter : IAsyncActionFilter
    {
        private const string HeaderName = "X-Dhan-Api-Key";
        private readonly DhanTradingOptions _options;
        private readonly ILogger<DhanApiKeyGuardFilter> _logger;

        public DhanApiKeyGuardFilter(IOptions<DhanTradingOptions> options, ILogger<DhanApiKeyGuardFilter> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                await next();
                return;
            }

            if (string.IsNullOrEmpty(_options.ApiKey))
            {
                _logger.LogWarning("DhanTrading:ApiKey is not configured - endpoints are reachable without a shared secret.");
                await next();
                return;
            }

            var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
            if (string.IsNullOrEmpty(provided) || !FixedTimeEquals(provided, _options.ApiKey))
            {
                context.Result = new Microsoft.AspNetCore.Mvc.UnauthorizedResult();
                return;
            }

            await next();
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            var bytesA = Encoding.UTF8.GetBytes(a);
            var bytesB = Encoding.UTF8.GetBytes(b);
            if (bytesA.Length != bytesB.Length)
            {
                // Still compare to avoid leaking length via early-exit timing.
                CryptographicOperations.FixedTimeEquals(bytesA, bytesA);
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
        }
    }
}
