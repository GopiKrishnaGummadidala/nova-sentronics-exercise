using System.Collections.Concurrent;

namespace NovaExercise.Core.Resources;

/// <summary>
/// A naive resource manager that can genuinely deadlock. It locks resources in the
/// order they are requested (not a global order) and HOLDS each lock while it waits
/// to acquire the next one - classic hold-and-wait plus circular-wait.
///
/// Example deadlock scenario:
/// - Thread 1: acquire {R_A, R_B} -&gt; locks R_A, holds it, blocks waiting for R_B
/// - Thread 2: acquire {R_B, R_A} -&gt; locks R_B, holds it, blocks waiting for R_A
/// Both threads now hold one resource and wait on the other - a real deadlock.
///
/// It resolves after `timeout` only because Monitor.TryEnter is given a timeout as
/// a safety valve for this demo (so it doesn't hang a reviewer's machine forever).
/// With Monitor.Enter (no timeout) this exact code would hang indefinitely.
///
/// In our stage map:
/// - stage_1: {R_A, R_B}
/// - stage_2: {R_C, R_B}
/// - stage_3: {R_A, R_C}
/// These form a cycle (A-B-C-A), which is why requesting them in inconsistent
/// orders is enough to trigger circular wait. See tests/NovaExercise.ConcurrencyDemos.
/// </summary>
public sealed class ResourceManagerNaive : IResourceManager
{
    // Artificial gap between acquiring one lock and attempting the next, purely so
    // the demo's interleaving is deterministic instead of a timing coin flip. Real
    // deadlocks don't need this - the window is naturally created by whatever work
    // happens between acquiring each lock.
    private static readonly TimeSpan ArtificialWorkBetweenLocks = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentDictionary<ResourceId, Resource> _resources = new();

    public ResourceManagerNaive()
    {
        foreach (ResourceId id in Enum.GetValues(typeof(ResourceId)))
        {
            _resources[id] = new Resource(id);
        }
    }

    public ResourceState GetState(ResourceId id) =>
        _resources.TryGetValue(id, out var r) ? r.GetState() : ResourceState.Error;

    public void SetError(ResourceId id)
    {
        if (_resources.TryGetValue(id, out var r))
            r.SetError();
    }

    public void ClearError(ResourceId id)
    {
        if (_resources.TryGetValue(id, out var r))
            r.ClearError();
    }

    public Task<IDisposable> AcquireAsync(
        IReadOnlyCollection<ResourceId> required,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        // Monitor.TryEnter is a synchronous, thread-affine primitive with no async
        // form - genuinely blocking a real OS thread is the entire point of this
        // demo, since that's what makes the deadlock real rather than simulated.
        // Task.Run only relocates which thread blocks. ct is deliberately not passed
        // to Task.Run itself: a pre-cancelled token there would skip this delegate
        // entirely rather than run it - the exact bug this codebase already found
        // and fixed once in StageScheduler (see concurrency.md).
        return Task.Run(() => AcquireCore(required, timeout));
    }

    private IDisposable AcquireCore(IReadOnlyCollection<ResourceId> required, TimeSpan timeout)
    {
        var toLock = required.ToList(); // Naive: order as requested, not globally sorted
        var lockedMonitors = new List<Resource>();
        var busyResources = new List<Resource>();

        try
        {
            foreach (var id in toLock)
            {
                if (!_resources.TryGetValue(id, out var resource))
                    throw new InvalidOperationException($"Unknown resource {id}");

                // Held until Dispose() or the rollback below - NOT released here.
                // This is what makes a real hold-and-wait deadlock possible.
                bool taken = false;
                Monitor.TryEnter(resource, timeout, ref taken);
                if (!taken)
                    throw new TimeoutException(
                        $"Could not acquire resource {id} within {timeout} - likely deadlock " +
                        "(another thread is holding it while waiting on a resource we hold)");

                lockedMonitors.Add(resource);

                if (!resource.TryMarkBusy())
                    throw new InvalidOperationException($"Resource {id} not idle");

                busyResources.Add(resource);
                Thread.Sleep(ArtificialWorkBetweenLocks);
            }

            return new ResourceLease(lockedMonitors, busyResources);
        }
        catch
        {
            foreach (var r in busyResources)
                r.MarkIdle();
            foreach (var r in lockedMonitors)
                Monitor.Exit(r);
            throw;
        }
    }

    private sealed class ResourceLease : IDisposable
    {
        private readonly List<Resource> _lockedMonitors;
        private readonly List<Resource> _busyResources;
        private int _disposed;

        public ResourceLease(List<Resource> lockedMonitors, List<Resource> busyResources)
        {
            _lockedMonitors = lockedMonitors;
            _busyResources = busyResources;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            for (int i = _busyResources.Count - 1; i >= 0; i--)
                _busyResources[i].MarkIdle();
            for (int i = _lockedMonitors.Count - 1; i >= 0; i--)
                Monitor.Exit(_lockedMonitors[i]);
        }
    }
}
