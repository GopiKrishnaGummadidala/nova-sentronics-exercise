using NovaExercise.Core.Resources;

namespace NovaExercise.Tests.Resources;

public class ResourceManagerTests
{
    [Fact]
    public async Task Acquire_SuccessfullyAcquiresSingleResource()
    {
        var rm = new ResourceManager();
        var required = new[] { ResourceId.R_A };

        using var lease = await rm.AcquireAsync(required, TimeSpan.FromSeconds(1));

        Assert.NotNull(lease);
        Assert.Equal(ResourceState.Busy, rm.GetState(ResourceId.R_A));
    }

    [Fact]
    public async Task Acquire_SuccessfullyAcquiresMultipleResources()
    {
        var rm = new ResourceManager();
        var required = new[] { ResourceId.R_A, ResourceId.R_B };

        using var lease = await rm.AcquireAsync(required, TimeSpan.FromSeconds(1));

        Assert.NotNull(lease);
        Assert.Equal(ResourceState.Busy, rm.GetState(ResourceId.R_A));
        Assert.Equal(ResourceState.Busy, rm.GetState(ResourceId.R_B));
    }

    [Fact]
    public async Task Acquire_ReleasesAllOnFailure()
    {
        var rm = new ResourceManager();

        // First acquisition succeeds
        var required1 = new[] { ResourceId.R_A };
        using var lease1 = await rm.AcquireAsync(required1, TimeSpan.FromSeconds(1));

        // Second acquisition waits for R_A to free up, then times out since it's
        // held by lease1 for the whole call.
        var required2 = new[] { ResourceId.R_A, ResourceId.R_B };
        await Assert.ThrowsAsync<TimeoutException>(() =>
            rm.AcquireAsync(required2, TimeSpan.FromMilliseconds(100))
        );

        // R_B must not be left in Busy state
        Assert.Equal(ResourceState.Busy, rm.GetState(ResourceId.R_A));
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_B));
    }

    [Fact]
    public async Task Acquire_ThrowsWhenResourceIsInErrorState()
    {
        var rm = new ResourceManager();
        rm.SetError(ResourceId.R_A);

        var required = new[] { ResourceId.R_A };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rm.AcquireAsync(required, TimeSpan.FromMilliseconds(100))
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

    [Fact]
    public async Task Acquire_WithAlreadyCancelledToken_ThrowsImmediately_EvenWhenResourceIsIdle()
    {
        var rm = new ResourceManager();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // R_A is completely free - without an eager check, TryMarkBusy would
        // succeed instantly and silently ignore the cancellation entirely. This
        // path never reaches Task.Delay, so it's guaranteed to be the exact
        // OperationCanceledException type, not a TaskCanceledException.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            rm.AcquireAsync(new[] { ResourceId.R_A }, TimeSpan.FromSeconds(1), cts.Token));

        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_A));
    }

    [Fact]
    public async Task Acquire_CancelledWhileWaitingForABusyResource_ThrowsAndRollsBackAnyAlreadyAcquired()
    {
        var rm = new ResourceManager();

        // R_B is held for the whole test so the second Acquire has to wait on it.
        using var holdB = await rm.AcquireAsync(new[] { ResourceId.R_B }, TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // Sorted acquisition order is R_A, R_B: R_A succeeds immediately, then the
        // wait for R_B (held by holdB) gets cancelled mid-poll - via the awaited
        // Task.Delay, which throws TaskCanceledException (a subclass), not the
        // eager check's plain OperationCanceledException - hence ThrowsAnyAsync.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rm.AcquireAsync(new[] { ResourceId.R_A, ResourceId.R_B }, TimeSpan.FromSeconds(5), cts.Token));

        // R_A was acquired before cancellation hit - it must be rolled back, not left Busy.
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_A));
        Assert.Equal(ResourceState.Busy, rm.GetState(ResourceId.R_B)); // still held by holdB
    }

    [Fact]
    public async Task Acquire_TimesOutWaitingForASecondResource_RollsBackTheFirstOneAlreadyAcquired()
    {
        // The cancellation test above exercises the same rollback code path, but
        // via cancellation. This exercises it via the *default* failure mode
        // documented in assumptions.md - a resource stays Busy long enough that
        // the shared timeout simply elapses, no cancellation involved - which
        // Acquire_ReleasesAllOnFailure doesn't actually cover: there, R_A (sorted
        // first) is already held, so it fails on the very first resource and
        // nothing is ever partially acquired. This test needs R_A to succeed
        // *before* the failure, so there's something real to roll back.
        var rm = new ResourceManager();

        // R_B is held for the whole test so the second Acquire has to wait on it
        // until its own timeout elapses.
        using var holdB = await rm.AcquireAsync(new[] { ResourceId.R_B }, TimeSpan.FromSeconds(5));

        // Sorted acquisition order is R_A, R_B: R_A succeeds immediately (free),
        // then the wait for R_B (held by holdB) runs out its own short timeout.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            rm.AcquireAsync(new[] { ResourceId.R_A, ResourceId.R_B }, TimeSpan.FromMilliseconds(100)));

        // R_A was acquired before the timeout hit - it must be rolled back, not left Busy.
        Assert.Equal(ResourceState.Idle, rm.GetState(ResourceId.R_A));
        Assert.Equal(ResourceState.Busy, rm.GetState(ResourceId.R_B)); // still held by holdB
    }
}
