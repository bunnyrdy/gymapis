namespace GymApis.Services.Attendance;

/// <summary>
/// Runs the end-of-day closeout on a timer.
///
/// The spec requires that a day nobody marked becomes Absent by itself. There
/// was no scheduler in this API before this module, and rather than take on
/// Hangfire or Quartz for a single hourly statement, this is the framework's
/// own BackgroundService: no package, no database tables of its own, no
/// dashboard to secure.
///
/// Hourly rather than nightly on purpose. Shifts end at different times — the
/// morning shift is done at 14:00 — so a single midnight run would leave the
/// roster showing "not marked" all afternoon for people whose day finished
/// hours ago. An hourly tick closes each shift out shortly after it ends.
///
/// Everything about it is safe to run twice: close_out_attendance() is one
/// INSERT ... ON CONFLICT DO NOTHING, so overlapping runs, a second server, or
/// the read-path catch-up in AttendanceService all converge on the same rows.
/// </summary>
public class AttendanceCloseoutWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// Long enough for the app to finish starting and short enough that a
    /// restart still closes out promptly. The first tick matters: a server that
    /// was down overnight backfills here.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AttendanceCloseoutWorker> _log;

    public AttendanceCloseoutWorker(IServiceScopeFactory scopes, ILogger<AttendanceCloseoutWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;     // shutting down before the first tick
        }

        using var timer = new PeriodicTimer(Interval);

        do
        {
            await RunOnceAsync(ct);
        }
        while (await SafeWaitAsync(timer, ct));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            // The service is scoped (it holds a DbContext); a hosted service is
            // a singleton, so it must open its own scope per tick rather than
            // capture one for the lifetime of the process.
            using var scope = _scopes.CreateScope();
            var attendance = scope.ServiceProvider.GetRequiredService<IAttendanceService>();

            await attendance.SweepAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
        catch (Exception ex)
        {
            // A failed tick must never take the host down — the next one is an
            // hour away and the read path will also retry before then.
            _log.LogError(ex, "Attendance closeout tick failed; will retry on the next interval.");
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
