using System.Collections.Concurrent;

namespace NovaExercise.Core.Resources;

public sealed class ResourceManager : IResourceManager
{
    private readonly ConcurrentDictionary<ResourceId, Resource> _resources = new();

    public ResourceManager()
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
        var sorted = required.OrderBy(x => x).ToList();
        var acquired = new List<Resource>();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            foreach (var id in sorted)
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

            return new ResourceLease(acquired, sorted);
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
        private readonly List<ResourceId> _order;
        private int _disposed;

        public ResourceLease(List<Resource> resources, List<ResourceId> order)
        {
            _resources = resources;
            _order = order;
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