namespace NovaExercise.Core.Resources;

public sealed class Resource
{
    public ResourceId Id { get; }
    public ResourceState State { get; private set; }

    private readonly object _lock = new();

    public Resource(ResourceId id)
    {
        Id = id;
        State = ResourceState.Idle;
    }

    public void SetError()
    {
        lock (_lock)
        {
            if (State == ResourceState.Busy)
                return;
            State = ResourceState.Error;
        }
    }

    public void ClearError()
    {
        lock (_lock)
        {
            if (State == ResourceState.Error)
                State = ResourceState.Idle;
        }
    }

    /// <summary>
    /// Atomically checks Idle and transitions to Busy in one locked step, so callers
    /// never see a window between checking state and acting on it.
    /// </summary>
    internal bool TryMarkBusy()
    {
        lock (_lock)
        {
            if (State != ResourceState.Idle)
                return false;
            State = ResourceState.Busy;
            return true;
        }
    }

    internal void MarkIdle()
    {
        lock (_lock)
        {
            if (State == ResourceState.Error)
                return;
            State = ResourceState.Idle;
        }
    }

    public ResourceState GetState()
    {
        lock (_lock) return State;
    }
}