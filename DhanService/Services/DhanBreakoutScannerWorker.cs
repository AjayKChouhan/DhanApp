using DhanService.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DhanService.Services
{
    public sealed class BreakoutScannerRuntimeState
    {
        private readonly object _lock = new();
        private BreakoutScannerResponse _status = new(null, 0, 0, 0,
            "The background scanner has not completed its first scan yet.", Array.Empty<BreakoutSignal>());

        public BreakoutScannerResponse GetStatus()
        {
            lock (_lock)
            {
                return _status;
            }
        }

        public void SetStatus(BreakoutScannerResponse status)
        {
            lock (_lock)
            {
                _status = status;
            }
        }
    }

    public sealed class DhanBreakoutScannerWorker : BackgroundService
    {
        private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly BreakoutScannerRuntimeState _runtimeState;
        private readonly ILogger<DhanBreakoutScannerWorker> _logger;

        public DhanBreakoutScannerWorker(
            IServiceScopeFactory scopeFactory,
            BreakoutScannerRuntimeState runtimeState,
            ILogger<DhanBreakoutScannerWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _runtimeState = runtimeState;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var runStartupScan = true;
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow.ToOffset(IndiaOffset);
                try
                {
                    if (runStartupScan || IsMarketOpen(now))
                    {
                        runStartupScan = false;
                        await using var scope = _scopeFactory.CreateAsyncScope();
                        var scanner = scope.ServiceProvider.GetRequiredService<IDhanBreakoutScannerService>();
                        _runtimeState.SetStatus(await scanner.ScanAsync(stoppingToken));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The 5-minute NSE stock-futures scan failed; it will retry at the next interval.");
                    _runtimeState.SetStatus(new BreakoutScannerResponse(
                        now, 0, 0, 0, $"The most recent scan failed: {ex.Message}", Array.Empty<BreakoutSignal>()));
                }

                var delay = TimeUntilNextInterval(DateTimeOffset.UtcNow.ToOffset(IndiaOffset));
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private static bool IsMarketOpen(DateTimeOffset now) =>
            now.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
            now.TimeOfDay >= new TimeSpan(9, 15, 0) &&
            now.TimeOfDay < new TimeSpan(15, 31, 0);

        private static TimeSpan TimeUntilNextInterval(DateTimeOffset now)
        {
            var minutes = now.TimeOfDay.TotalMinutes;
            var nextBoundary = (Math.Floor(minutes / 5) + 1) * 5;
            var next = now.Date.AddMinutes(nextBoundary).AddSeconds(10);
            return next - now;
        }
    }
}
