using NovaExercise.Core.Stages;

namespace NovaExercise.ConcurrencyDemos.Demos;

/// <summary>
/// Shows the real check-then-act race StageScheduler.ScheduleStagesAsync originally
/// had (see concurrency.md), reproduced in isolation via NaiveStageTracker so it can
/// be watched directly rather than only read about or trusted via a passing unit
/// test. Two threads call TryStart for the SAME stage id at (almost) the same time;
/// naively, both can see "not running yet" before either records that it now is.
/// </summary>
public static class AtomicityRaceDemo
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Demo 2: Atomicity Violation (NaiveStageTracker) ===");

        var tracker = new NaiveStageTracker();

        var s1 = Task.Run(() =>
        {
            var started = tracker.TryStart("stage_1");
            Console.WriteLine($"S1: TryStart(\"stage_1\") -> {started}");
        });

        var s2 = Task.Run(() =>
        {
            var started = tracker.TryStart("stage_1");
            Console.WriteLine($"S2: TryStart(\"stage_1\") -> {started}");
        });

        await Task.WhenAll(s1, s2);
        Console.WriteLine(tracker.TotalStarts > 1
            ? $"BUG REPRODUCED: TotalStarts = {tracker.TotalStarts} - both calls started \"stage_1\" at once, violating \"at most one instance runs at a time\"."
            : $"TotalStarts = {tracker.TotalStarts} (didn't race this run - timing-dependent; StageScheduler's real fix, TryAdd, closes this window entirely rather than relying on timing).");
        Console.WriteLine("Demo 2 completed.");
    }
}
