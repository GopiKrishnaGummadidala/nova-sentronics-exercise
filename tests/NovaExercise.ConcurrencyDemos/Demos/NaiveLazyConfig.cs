namespace NovaExercise.ConcurrencyDemos.Demos;

/// <summary>
/// A naive, deliberately-unsynchronized "lazily initialized" shared value. It
/// reproduces the order-violation shape from concurrency.md's illustrative
/// example: one thread is supposed to finish initializing shared state before
/// another thread uses it, but nothing here actually enforces that order - no
/// lock, no Volatile, no Interlocked, nothing establishing a happens-before
/// relationship between the write and the read.
///
/// Unlike ResourceManagerNaive and NaiveStageTracker, this doesn't mirror a
/// bug that ever existed in this codebase's real components - RuleEngine's
/// constructor-then-subscribe sequencing and SensorRegistry's locked
/// Register/Unregister were always properly ordered. It lives here rather
/// than in NovaExercise.Core for exactly that reason: it's a generic
/// illustration of the bug category, not a stand-in for a real Core risk.
/// </summary>
public sealed class NaiveLazyConfig
{
    // Artificial delay before publishing, purely so the demo's race is
    // deterministic instead of a timing coin flip - same reasoning as
    // ResourceManagerNaive's ArtificialWorkBetweenLocks and NaiveStageTracker's
    // ArtificialGapBetweenCheckAndWrite. Real initialization work (loading
    // config, warming a cache) doesn't need this - it's naturally slow enough
    // on its own to create the same window.
    private static readonly TimeSpan ArtificialInitializationDelay = TimeSpan.FromMilliseconds(200);

    private string? _setting;

    /// <summary>
    /// BUGGY: nothing about this method or GetSetting() establishes a
    /// happens-before relationship between this write and that read. A caller
    /// that calls GetSetting() before this completes - which nothing here
    /// prevents - sees the pre-initialization value.
    /// </summary>
    public void Initialize()
    {
        Thread.Sleep(ArtificialInitializationDelay);
        _setting = "production-config-v1";
    }

    public string GetSetting() => _setting ?? "<<NOT INITIALIZED YET>>";
}
