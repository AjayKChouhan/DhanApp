using DhanAPI.Filters;
using DhanService.Models;
using DhanService.Services;
using Microsoft.AspNetCore.Mvc;

namespace DhanAPI.Controllers
{
    [ApiController]
    [Route("api/dhantrading/scanner")]
    [TypeFilter(typeof(DhanApiKeyGuardFilter))]
    public sealed class DhanBreakoutScannerController : ControllerBase
    {
        private readonly BreakoutScannerRuntimeState _runtimeState;

        public DhanBreakoutScannerController(BreakoutScannerRuntimeState runtimeState)
        {
            _runtimeState = runtimeState;
        }

        /// <summary>Returns the latest in-memory 5-minute NSE stock-futures scan.</summary>
        [HttpGet("breakouts")]
        [ProducesResponseType(typeof(BreakoutScannerResponse), StatusCodes.Status200OK)]
        public ActionResult<BreakoutScannerResponse> GetBreakouts() => Ok(_runtimeState.GetStatus());
    }
}
