namespace NovaExercise.Core.Sensors;

public sealed class SimulatedSensor : ISensor, IDisposable
{
    public SensorType Type { get; }
    public SensorReading? CurrentReading { get; private set; }

    public event Action<SensorReading>? ReadingChanged;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _tickerTask;
    private readonly Func<double> _valueGenerator;

    public SimulatedSensor(SensorType type, Func<double> valueGenerator)
    {
        Type = type;
        _valueGenerator = valueGenerator;
        _tickerTask = Task.Run(() => RunLoop(_cts.Token));
    }

    private void RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var value = _valueGenerator();
            var reading = new SensorReading(Type, value, DateTimeOffset.Now);
            CurrentReading = reading;
            ReadingChanged?.Invoke(reading);
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _tickerTask.Wait(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}