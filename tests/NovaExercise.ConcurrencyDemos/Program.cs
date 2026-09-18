using NovaExercise.Core.Resources;
using NovaExercise.Core.Stages;

// Demo 1: deadlock. This shows how ResourceManagerNaive can deadlock.
// It acquires resources in different orders from two threads, each holding
// its first resource while it blocks waiting for its second - genuine
// hold-and-wait plus circular-wait. Expect this run to take ~5 seconds: both
// threads block for the full acquisition timeout before failing with
// TimeoutException. That timeout is only a safety valve for this demo - the
// same lock pattern with Monitor.Enter (no timeout) would hang forever.
Console.WriteLine("=== Demo 1: Deadlock (ResourceManagerNaive) ===");

var rm = new ResourceManagerNaive();

var t1 = Task.Run(async () =>
{
    try
    {
        Console.WriteLine("T1: trying to acquire {R_A, R_B}");
        using var lease1 = await rm.AcquireAsync(
            new[] { ResourceId.R_A, ResourceId.R_B },
            TimeSpan.FromSeconds(5)
        );
        Console.WriteLine("T1: acquired {R_A, R_B}");
        Thread.Sleep(2000);
        Console.WriteLine("T1: releasing");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"T1: error - {ex.Message}");
    }
});

var t2 = Task.Run(async () =>
{
    try
    {
        Console.WriteLine("T2: trying to acquire {R_B, R_A}");
        using var lease2 = await rm.AcquireAsync(
            new[] { ResourceId.R_B, ResourceId.R_A },
            TimeSpan.FromSeconds(5)
        );
        Console.WriteLine("T2: acquired {R_B, R_A}");
        Thread.Sleep(2000);
        Console.WriteLine("T2: releasing");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"T2: error - {ex.Message}");
    }
});

await Task.WhenAll(t1, t2);
Console.WriteLine("Demo 1 completed (or timed out).");

// Demo 2: atomicity violation. This shows the real check-then-act race
// StageScheduler.ScheduleStagesAsync originally had (see concurrency.md),
// reproduced in isolation via NaiveStageTracker so it can be watched directly
// rather than only read about or trusted via a passing unit test. Two threads
// call TryStart for the SAME stage id at (almost) the same time; naively, both
// can see "not running yet" before either records that it now is.
Console.WriteLine();
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