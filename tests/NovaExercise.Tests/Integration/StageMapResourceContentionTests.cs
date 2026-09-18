using System.Diagnostics;
using NovaExercise.Core.Logging;
using NovaExercise.Core.Resources;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Integration;

public class StageMapResourceContentionTests
{
    [Fact]
    public async Task AllThreeStages_ScheduledRepeatedlyAndConcurrently_NeverDeadlockOrOverlapASharedResource()
    {
        // Stage1={R_A,R_B}, Stage2={R_C,R_B}, Stage3={R_A,R_C} deliberately form a
        // resource cycle (A-B-C-A) - the same shape that makes ResourceManagerNaive
        // deadlock (see concurrency.md). Every pair of these three stages shares
        // exactly one resource, so at most one of them can legitimately hold its
        // resources at any instant - this drives all three repeatedly through the
        // REAL ResourceManager + StageScheduler (not fakes) and checks what a
        // single run can't reliably catch: the whole run always completes (no
        // deadlock), and no two different stages are ever recorded holding a
        // shared resource at the same time.
        var rm = new ResourceManager();
        var tracker = new ResourceUsageTracker();
        IStageExecutor executor = new TrackingStageExecutor(rm, tracker);
        IStageScheduler scheduler = new StageScheduler(executor, new SystemLogger());
        var sensorValues = new Dictionary<SensorType, double>();
        var stages = new[] { StageId.Stage1, StageId.Stage2, StageId.Stage3 };

        const int attempts = 50;
        for (var i = 0; i < attempts; i++)
        {
            // Fired back-to-back, not spaced out: StageScheduler's own atomicity
            // guarantee means a batch can legitimately no-op for a stage still
            // running from an earlier batch - that's expected throttling, not a
            // bug, which is why the assertions below don't require an exact count.
            await scheduler.ScheduleStagesAsync(stages, sensorValues, CancellationToken.None);
        }

        Assert.True(
            await tracker.WaitUntilQuietAsync(TimeSpan.FromSeconds(10)),
            "expected every in-flight execution to finish without deadlocking");

        Assert.False(tracker.SawOverlap, "two different stages held a shared resource at the same time");
        Assert.True(tracker.TotalExecutions > 0, "expected at least some stage executions to have actually run");

        // Every pair of these three stages shares a resource, so nothing in this
        // set can ever legitimately run two-at-a-time - a stronger, structural
        // check on top of SawOverlap.
        Assert.True(tracker.MaxConcurrent <= 1, $"expected at most 1 concurrent execution, saw {tracker.MaxConcurrent}");

        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_A));
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_B));
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_C));
    }

    /// <summary>Acquires resources through the real ResourceManager like a production
    /// executor, then records which resources are "in use" around a brief simulated
    /// work delay - long enough to create a real window for two stages to overlap if
    /// ResourceManager's mutual exclusion were ever broken.</summary>
    private sealed class TrackingStageExecutor : IStageExecutor
    {
        private readonly IResourceManager _resourceManager;
        private readonly ResourceUsageTracker _tracker;

        public TrackingStageExecutor(IResourceManager resourceManager, ResourceUsageTracker tracker)
        {
            _resourceManager = resourceManager;
            _tracker = tracker;
        }

        public async Task ExecuteAsync(StageDefinition stage, CancellationToken ct)
        {
            // EnterAttempt() happens before Acquire, not after it succeeds: if it
            // started counting only once a resource was already held, there would
            // be a real window - between Acquire marking a resource Busy and this
            // method's next line running - where "wait until quiet" could observe
            // zero in-flight attempts while a resource was still genuinely held by
            // an attempt that just hasn't recorded itself yet. That's exactly what
            // was observed: the quiet check passing while GetState() still returned
            // Busy, roughly 1 run in 3 under stress.
            _tracker.EnterAttempt();
            try
            {
                // RecordAcquired fires strictly after the real Acquire succeeds, and
                // RecordReleased fires strictly before the real Dispose - in other
                // words, the tracker's claimed "in use" window is always a *subset*
                // of the real one, on both ends, never a superset. Getting this
                // backwards on the release side (Dispose then RecordReleased, tried
                // first) creates a real window where this stage's resource is
                // already free in reality but still "claimed" by the tracker - if
                // another stage's real acquisition and RecordAcquired call land in
                // that window, the tracker sees a false collision even though the
                // real system correctly serialized access. Confirmed by observing
                // exactly that: an intermittent false SawOverlap under load.
                var lease = await _resourceManager.AcquireAsync(stage.RequiredResources, TimeSpan.FromSeconds(5), ct);
                _tracker.RecordAcquired(stage.RequiredResources);
                try
                {
                    await Task.Delay(20, ct);
                }
                finally
                {
                    _tracker.RecordReleased(stage.RequiredResources);
                    lease.Dispose();
                }
            }
            finally
            {
                _tracker.ExitAttempt();
            }
        }
    }

    private sealed class ResourceUsageTracker
    {
        private readonly object _lock = new();
        private readonly HashSet<ResourceId> _inUse = new();
        private int _inFlight;
        private int _concurrentHolders;
        private int _maxConcurrent;
        private volatile bool _everStarted;

        public bool SawOverlap { get; private set; }
        public int TotalExecutions;
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public void EnterAttempt()
        {
            _everStarted = true;
            Interlocked.Increment(ref _inFlight);
        }

        public void ExitAttempt() => Interlocked.Decrement(ref _inFlight);

        public void RecordAcquired(IReadOnlyCollection<ResourceId> resources)
        {
            lock (_lock)
            {
                foreach (var r in resources)
                {
                    if (!_inUse.Add(r))
                        SawOverlap = true;
                }
            }

            Interlocked.Increment(ref TotalExecutions);
            var now = Interlocked.Increment(ref _concurrentHolders);
            InterlockedMax(ref _maxConcurrent, now);
        }

        public void RecordReleased(IReadOnlyCollection<ResourceId> resources)
        {
            lock (_lock)
            {
                foreach (var r in resources)
                    _inUse.Remove(r);
            }

            Interlocked.Decrement(ref _concurrentHolders);
        }

        public async Task<bool> WaitUntilQuietAsync(TimeSpan timeout)
        {
            // Scheduling returns before the background work is even dispatched, so
            // "quiet" must mean "started, then settled back to zero in-flight
            // attempts" - not just "zero," which would also be true before
            // anything has run at all.
            var sw = Stopwatch.StartNew();
            while (!_everStarted || Volatile.Read(ref _inFlight) > 0)
            {
                if (sw.Elapsed >= timeout)
                    return false;
                await Task.Delay(10);
            }
            return true;
        }

        private static void InterlockedMax(ref int target, int candidate)
        {
            int initial;
            do
            {
                initial = Volatile.Read(ref target);
                if (candidate <= initial)
                    return;
            } while (Interlocked.CompareExchange(ref target, candidate, initial) != initial);
        }
    }
}
