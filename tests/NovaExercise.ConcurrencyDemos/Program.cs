using NovaExercise.Core.Resources;

// This demo shows how ResourceManagerNaive can deadlock.
// It acquires resources in different orders from two threads.
//
// DO NOT run this for long; it may hang. It's for demonstration only.

var rm = new ResourceManagerNaive();

var t1 = Task.Run(() =>
{
    try
    {
        Console.WriteLine("T1: trying to acquire {R_A, R_B}");
        using var lease1 = rm.Acquire(
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

var t2 = Task.Run(() =>
{
    try
    {
        Console.WriteLine("T2: trying to acquire {R_B, R_A}");
        using var lease2 = rm.Acquire(
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
Console.WriteLine("Demo completed (or timed out).");