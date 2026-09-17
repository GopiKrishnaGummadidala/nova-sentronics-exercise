namespace NovaExercise.Core.Resources;

public interface IResourceManager
{
    Task<IDisposable> AcquireAsync(
        IReadOnlyCollection<ResourceId> required,
        TimeSpan timeout,
        CancellationToken ct = default
    );

    void SetError(ResourceId id);
    void ClearError(ResourceId id);
    ResourceState GetState(ResourceId id);
}