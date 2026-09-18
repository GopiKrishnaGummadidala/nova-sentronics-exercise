using NovaExercise.ConcurrencyDemos.Demos;

await DeadlockDemo.RunAsync();
Console.WriteLine();
await AtomicityRaceDemo.RunAsync();
Console.WriteLine();
await OrderViolationDemo.RunAsync();
Console.WriteLine();
await ResourceContentionDemo.RunAsync();
