using System.Collections.Concurrent;

namespace NovaExercise.Core.Stages;

/// <summary>
/// A naive stage tracker that reproduces the atomicity violation
/// StageScheduler.ScheduleStagesAsync itself once had: TryStart does a check (is
/// this id already running?) and an act (record that it now is) as two SEPARATE
/// operations on a ConcurrentDictionary, not one atomic step - so two concurrent
/// callers can both pass the check before either one's write lands.
///
/// This is the real bug found and fixed in StageScheduler during development (see
/// concurrency.md's "Atomicity-Violation Example"), reproduced here in isolation so
/// it can be run and watched directly rather than only read about or trusted via a
/// passing unit test. StageScheduler itself no longer has this shape - it closes
/// the window with a single ConcurrentDictionary.TryAdd instead.
///
/// Example scenario:
/// - Thread 1: TryStart("stage_1") -&gt; sees "not running", about to record it...
/// - Thread 2: TryStart("stage_1") -&gt; ALSO sees "not running", before Thread 1 writes
/// - Both threads proceed believing they're the one instance now running "stage_1".
/// See tests/NovaExercise.ConcurrencyDemos.
/// </summary>
public sealed class NaiveStageTracker
{
    // Artificial gap between the check and the write, purely so the demo's race
    // window is deterministic instead of a timing coin flip - same reasoning as
    // ResourceManagerNaive's ArtificialWorkBetweenLocks. Real atomicity violations
    // don't need this: the window is naturally created by whatever work happens
    // between checking and acting (in StageScheduler's real bug, that was building
    // the stage's Task).
    private static readonly TimeSpan ArtificialGapBetweenCheckAndWrite = TimeSpan.FromMilliseconds(200);

    private readonly ConcurrentDictionary<string, bool> _running = new();
    private int _totalStarts;

    public int TotalStarts => _totalStarts;

    /// <summary>
    /// Naively "starts" a stage by id. Returns true if this call believes it's the
    /// one that started it - under the race this class deliberately doesn't close,
    /// more than one concurrent call for the same id can each get true back.
    /// </summary>
    public bool TryStart(string stageId)
    {
        // BUGGY: ContainsKey (check) and the later indexer assignment (act) are two
        // separate operations - not atomic together, even though each one alone is
        // thread-safe on a ConcurrentDictionary.
        if (_running.ContainsKey(stageId))
            return false;

        Thread.Sleep(ArtificialGapBetweenCheckAndWrite);

        _running[stageId] = true; // a second thread can reach here before this line runs
        Interlocked.Increment(ref _totalStarts);
        return true;
    }

    public void Finish(string stageId) => _running.TryRemove(stageId, out _);
}
