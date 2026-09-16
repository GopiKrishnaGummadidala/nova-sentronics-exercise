namespace NovaExercise.Core.Resources;

public interface IResourceManager
{
    IDisposable Acquire(
        IReadOnlyCollection<ResourceId> required,
        TimeSpan timeout,
        CancellationToken ct = default
    );

    void SetError(ResourceId id);
    void ClearError(ResourceId id);
    ResourceState GetState(ResourceId id);
}