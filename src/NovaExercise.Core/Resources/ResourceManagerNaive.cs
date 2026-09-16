using System.Collections.Concurrent;

namespace NovaExercise.Core.Resources;

/// <summary>
/// A naive resource manager that can deadlock.
/// It locks resources in the order they are requested, not in a global order.
/// With stages requiring overlapping resources, this can create circular wait.
///
/// Example deadlock scenario:
/// - Thread 1: acquire {R_A, R_B}  -> locks R_A, then tries to lock R_B
/// - Thread 2: acquire {R_B, R_A}  -> locks R_B, then tries to lock R_A
/// Both threads hold one resource and wait for the other -> deadlock.
///
/// In our stage map:
/// - stage_1: {R_A, R_B}
/// - stage_2: {R_C, R_B}
/// - stage_3: {R_A, R_C}
/// If two stages request resources in different orders, circular wait can occur.
/// </summary>
public sealed class ResourceManagerNaive : IResourceManager
{
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

    public IDisposable Acquire(
        IReadOnlyCollection<ResourceId> required,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        // Naive: lock in the given order, not globally sorted
        var toLock = required.ToList();
        var acquired = new List<Resource>();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            foreach (var id in toLock)
            {
                if (!_resources.TryGetValue(id, out var resource))
                    throw new InvalidOperationException($"Unknown resource {id}");

                var lockObj = resource;
                bool taken = false;
                try
                {
                    Monitor.TryEnter(lockObj, timeout, ref taken);
                    if (!taken)
                        throw new TimeoutException($"Could not acquire resource {id} in {timeout}");

                    if (resource.GetState() != ResourceState.Idle)
                        throw new InvalidOperationException($"Resource {id} not idle");

                    resource.MarkBusy();
                    acquired.Add(resource);
                }
                finally
                {
                    if (taken)
                        Monitor.Exit(lockObj);
                }
            }

            return new ResourceLease(acquired, toLock);
        }
        catch
        {
            foreach (var r in acquired)
            {
                r.MarkIdle();
            }
            throw;
        }
    }

    private sealed class ResourceLease : IDisposable
    {
        private readonly List<Resource> _resources;
        private int _disposed;

        public ResourceLease(List<Resource> resources, List<ResourceId> order)
        {
            _resources = resources;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            for (int i = _resources.Count - 1; i >= 0; i--)
            {
                var r = _resources[i];
                r.MarkIdle();
            }
        }
    }
}