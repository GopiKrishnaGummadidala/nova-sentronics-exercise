using NovaExercise.Core.Resources;

namespace NovaExercise.Tests.Resources;

public class ResourceManagerTests
{
    [Fact]
    public void Acquire_SuccessfullyAcquiresSingleResource()
    {
        var rm = new ResourceManager();
        var required = new[] { ResourceId.R_A };

        using var lease = rm.Acquire(required, TimeSpan.FromSeconds(1));

        Assert.NotNull(lease);
        Assert.Equal(ResourceState.Busy, rm.GetState(ResourceId.R_A));
    }

    [Fact]
    public void Acquire_SuccessfullyAcquiresMultipleResources()
    {
        var rm = new ResourceManager();
        var required = new[] { ResourceId.R_A, ResourceId.R_B };

        using var lease = rm.Acquire(required, TimeSpan.FromSeconds(1));

        Assert.NotNull(lease);
        Assert.Equal(ResourceState.Busy, rm.GetState(ResourceId.R_A));
        Assert.Equal(ResourceState.Busy, rm.GetState(ResourceId.R_B));
    }

    [Fact]
    public void Acquire_ReleasesAllOnFailure()
    {
        var rm = new ResourceManager();

        // First acquisition succeeds
        var required1 = new[] { ResourceId.R_A };
        using var lease1 = rm.Acquire(required1, TimeSpan.FromSeconds(1));

        // Second acquisition tries to take R_A again -> should fail
        var required2 = new[] { ResourceId.R_A, ResourceId.R_B };
        Assert.Throws<InvalidOperationException>(() =>
            rm.Acquire(required2, TimeSpan.FromMilliseconds(100))
        );

        // R_B must not be left in Busy state
        Assert.Equal(ResourceState.Busy, rm.GetState(ResourceId.R_A));
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_B));
    }

    [Fact]
    public void Acquire_ThrowsWhenResourceIsInErrorState()
    {
        var rm = new ResourceManager();
        rm.SetError(ResourceId.R_A);

        var required = new[] { ResourceId.R_A };

        Assert.Throws<InvalidOperationException>(() =>
            rm.Acquire(required, TimeSpan.FromMilliseconds(100))
        );
    }

    [Fact]
    public void SetError_ChangesStateToError()
    {
        var rm = new ResourceManager();

        rm.SetError(ResourceId.R_B);

        Assert.Equal(ResourceState.Error, rm.GetState(ResourceId.R_B));
    }

    [Fact]
    public void ClearError_ChangesStateFromErrorToIdle()
    {
        var rm = new ResourceManager();

        rm.SetError(ResourceId.R_C);
        rm.ClearError(ResourceId.R_C);

        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_C));
    }
}