using NovaExercise.Core.Resources;

namespace NovaExercise.ConcurrencyDemos.Demos;

/// <summary>
/// Shows how ResourceManagerNaive can deadlock. It acquires resources in different
/// orders from two threads, each holding its first resource while it blocks waiting
/// for its second - genuine hold-and-wait plus circular-wait. Expect this to take
/// ~5 seconds: both threads block for the full acquisition timeout before failing
/// with TimeoutException. That timeout is only a safety valve for this demo - the
/// same lock pattern with Monitor.Enter (no timeout) would hang forever.
/// </summary>
public static class DeadlockDemo
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Demo 1: Deadlock (ResourceManagerNaive) ===");

        var rm = new ResourceManagerNaive();

        var t1 = Task.Run(async () =>
        {
            try
            {
                Console.WriteLine("T1: trying to acquire {R_A, R_B}");
                using var lease1 = await rm.AcquireAsync(
                    new[] { ResourceId.R_A, ResourceId.R_B },
                    TimeSpan.FromSeconds(5)
                );
                Console.WriteLine("T1: acquired {R_A, R_B}");
                Thread.Sleep(2000);
                Console.WriteLine("T1: releasing");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"T1: error - {ex.Message}");
            }
        });

        var t2 = Task.Run(async () =>
        {
            try
            {
                Console.WriteLine("T2: trying to acquire {R_B, R_A}");
                using var lease2 = await rm.AcquireAsync(
                    new[] { ResourceId.R_B, ResourceId.R_A },
                    TimeSpan.FromSeconds(5)
                );
                Console.WriteLine("T2: acquired {R_B, R_A}");
                Thread.Sleep(2000);
                Console.WriteLine("T2: releasing");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"T2: error - {ex.Message}");
            }
        });

        await Task.WhenAll(t1, t2);
        Console.WriteLine("Demo 1 completed (or timed out).");
    }
}
