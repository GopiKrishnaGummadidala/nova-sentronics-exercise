using System.Collections.Concurrent;
using System.Diagnostics;

namespace NovaExercise.Core.Resources;

public sealed class ResourceManager : IResourceManager
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

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

    public async Task<IDisposable> AcquireAsync(
        IReadOnlyCollection<ResourceId> required,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        // Sorted order is defense-in-depth (keeps contention patterns deterministic
        // across callers); the actual deadlock-freedom comes from never holding one
        // resource while waiting on another - see WaitUntilIdleAsync.
        var sorted = required.OrderBy(x => x).ToList();
        var acquired = new List<Resource>();
        var budget = Stopwatch.StartNew();

        try
        {
            foreach (var id in sorted)
            {
                if (!_resources.TryGetValue(id, out var resource))
                    throw new InvalidOperationException($"Unknown resource {id}");

                var remaining = timeout - budget.Elapsed;
                if (!await WaitUntilIdleAsync(resource, remaining, ct))
                {
                    if (resource.GetState() == ResourceState.Error)
                        throw new InvalidOperationException($"Resource {id} is in Error state");

                    throw new TimeoutException(
                        $"Timed out waiting for resource {id} to become available " +
                        $"(requested: {string.Join(", ", sorted)}, timeout: {timeout})");
                }

                acquired.Add(resource);
            }

            return new ResourceLease(acquired);
        }
        catch
        {
            foreach (var r in acquired)
                r.MarkIdle();
            throw;
        }
    }

    /// <summary>
    /// Tries to atomically claim the resource. If it's transiently Busy, polls until
    /// it frees up or the remaining budget runs out; an Error resource is never worth
    /// waiting on and is reported back to the caller immediately (via GetState()).
    /// Crucially, this never blocks while holding a *different* resource, so this
    /// manager can never hold-and-wait and therefore can never deadlock. Waiting is
    /// done via Task.Delay rather than Thread.Sleep, so a caller polling a busy
    /// resource frees its thread-pool thread between attempts instead of parking it.
    /// </summary>
    private static async Task<bool> WaitUntilIdleAsync(Resource resource, TimeSpan remaining, CancellationToken ct)
    {
        // Checked before the first attempt too, not just while polling: an
        // already-cancelled token must never silently succeed just because the
        // resource happened to be free.
        ct.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (resource.TryMarkBusy())
                return true;

            if (resource.GetState() == ResourceState.Error)
                return false;

            if (sw.Elapsed >= remaining)
                return false;

            // Task.Delay itself observes ct, throwing TaskCanceledException (not the
            // plain OperationCanceledException the eager check above throws) if
            // cancelled here - callers matching on the exception type need
            // ThrowsAnyAsync, not ThrowsAsync, to allow for either.
            await Task.Delay(PollInterval, ct);
        }
    }

    private sealed class ResourceLease : IDisposable
    {
        private readonly List<Resource> _resources;
        private int _disposed;

        public ResourceLease(List<Resource> resources)
        {
            _resources = resources;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            for (int i = _resources.Count - 1; i >= 0; i--)
            {
                _resources[i].MarkIdle();
            }
        }
    }
}
