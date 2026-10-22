namespace Permora.Core;

internal static class ReviewScheduling
{
    // A window never exceeds its interval, so at most one started cycle is
    // still open. Coalesce missed, overdue cycles separately from that window.
    internal static (DateTimeOffset? OverdueStart, DateTimeOffset? OpenStart,
        DateTimeOffset NextStartAt, long SkippedCycles) CatchUp(AccessReviewSchedule schedule, DateTimeOffset now)
    {
        var interval = TimeSpan.FromDays(schedule.IntervalDays).Ticks;
        var elapsedIntervals = (now - schedule.NextStartAt).Ticks / interval;
        var latestStart = schedule.NextStartAt.AddTicks(checked(elapsedIntervals * interval));
        var latestOverdue = latestStart.AddDays(schedule.ReviewWindowDays) <= now;
        DateTimeOffset? overdueStart = latestOverdue ? latestStart
            : elapsedIntervals > 0 ? latestStart.AddTicks(-interval) : null;
        DateTimeOffset? openStart = latestOverdue ? null : latestStart;
        var createdCycles = (overdueStart.HasValue ? 1 : 0) + (openStart.HasValue ? 1 : 0);
        return (overdueStart, openStart, latestStart.AddTicks(interval), elapsedIntervals + 1 - createdCycles);
    }
}
