using NovaExercise.Core.Resources;

namespace NovaExercise.ConcurrencyDemos.Demos;

/// <summary>
/// Shows the real, fixed ResourceManager correctly handling the same resource-cycle
/// contention that deadlocks ResourceManagerNaive in DeadlockDemo: three stages
/// competing for overlapping resources (stage1={A,B}, stage2={C,B}, stage3={A,C} -
/// the A-B-C-A cycle), run concurrently through the actual production code.
///
/// StageMapResourceContentionTests covers this same scenario, but fires 50 rapid,
/// unnarrated attempts to maximize the odds of catching a bug. This demo optimizes
/// for being watchable instead: a handful of narrated rounds, so the serialization -
/// only one stage ever holding resources at a time, no hangs - is visible directly
/// rather than only proven by a passing test.
/// </summary>
public static class ResourceContentionDemo
{
    private const int Rounds = 3;

    public static async Task RunAsync()
    {
        Console.WriteLine("=== Demo 3: Resource Contention, Handled Correctly (ResourceManager) ===");

        var rm = new ResourceManager();
        var heldResources = new HashSet<ResourceId>();
        var trackerLock = new object();
        var overlapDetected = false;
        void OnOverlap() => overlapDetected = true;

        for (var round = 1; round <= Rounds; round++)
        {
            Console.WriteLine($"-- Round {round} --");
            var stage1 = RunStageAsync(rm, "Stage1", new[] { ResourceId.R_A, ResourceId.R_B }, heldResources, trackerLock, OnOverlap);
            var stage2 = RunStageAsync(rm, "Stage2", new[] { ResourceId.R_C, ResourceId.R_B }, heldResources, trackerLock, OnOverlap);
            var stage3 = RunStageAsync(rm, "Stage3", new[] { ResourceId.R_A, ResourceId.R_C }, heldResources, trackerLock, OnOverlap);
            await Task.WhenAll(stage1, stage2, stage3);
        }

        Console.WriteLine(overlapDetected
            ? "UNEXPECTED: two stages held a shared resource at the same time."
            : $"VERIFIED: {Rounds} rounds completed, no two stages ever overlapped on a shared resource - and no deadlock, unlike Demo 1.");
        Console.WriteLine("Demo 3 completed.");
    }

    private static async Task RunStageAsync(
        IResourceManager rm,
        string label,
        ResourceId[] resources,
        HashSet<ResourceId> heldResources,
        object trackerLock,
        Action onOverlap)
    {
        var resourceList = string.Join(", ", resources);
        Console.WriteLine($"{label}: trying to acquire {{{resourceList}}}");
        using var lease = await rm.AcquireAsync(resources, TimeSpan.FromSeconds(5));

        lock (trackerLock)
        {
            foreach (var r in resources)
            {
                if (!heldResources.Add(r))
                    onOverlap();
            }
        }

        Console.WriteLine($"{label}: acquired {{{resourceList}}}, working...");
        await Task.Delay(300);

        lock (trackerLock)
        {
            foreach (var r in resources)
                heldResources.Remove(r);
        }

        Console.WriteLine($"{label}: released {{{resourceList}}}");
    }
}
