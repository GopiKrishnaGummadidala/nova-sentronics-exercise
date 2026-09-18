namespace NovaExercise.ConcurrencyDemos.Demos;

/// <summary>
/// Shows an order violation: the intended order - Initialize() completes,
/// then GetSetting() is called - is never enforced by anything, so a worker
/// thread can run before the initializer thread finishes and observe
/// not-yet-published state. See concurrency.md's "Order-Violation Example"
/// for the same shape as a code snippet; this reproduces it as something you
/// can actually watch happen.
/// </summary>
public static class OrderViolationDemo
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Demo 3: Order Violation (NaiveLazyConfig) ===");

        var config = new NaiveLazyConfig();

        var initializer = Task.Run(() =>
        {
            Console.WriteLine("Initializer: starting Initialize() (takes a moment)...");
            config.Initialize();
            Console.WriteLine("Initializer: done.");
        });

        var worker = Task.Run(() =>
        {
            // No wait, no synchronization - nothing stops this from running
            // before the initializer above has finished, which is exactly the
            // point: nothing in NaiveLazyConfig enforces that it should.
            var value = config.GetSetting();
            Console.WriteLine($"Worker: read setting = \"{value}\"");
            return value;
        });

        var workerValue = await worker;
        await initializer;

        Console.WriteLine(workerValue == "<<NOT INITIALIZED YET>>"
            ? "BUG REPRODUCED: the worker read the setting before initialization finished - nothing enforced the required order."
            : $"Worker happened to read \"{workerValue}\" after initialization completed this run - timing-dependent; nothing here guarantees that ordering either way.");
        Console.WriteLine("Demo 3 completed.");
    }
}
