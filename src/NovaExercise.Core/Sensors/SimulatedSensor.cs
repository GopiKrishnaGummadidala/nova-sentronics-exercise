using NovaExercise.Core.Logging;

namespace NovaExercise.Core.Sensors;

public sealed class SimulatedSensor : ISensor, IDisposable
{
    public SensorType Type { get; }
    public SensorReading? CurrentReading { get; private set; }

    public event Action<SensorReading>? ReadingChanged;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _tickerTask;
    private readonly Func<double> _valueGenerator;
    private readonly IAuditLogger _audit;

    public SimulatedSensor(SensorType type, Func<double> valueGenerator, IAuditLogger audit)
    {
        Type = type;
        _valueGenerator = valueGenerator;
        _audit = audit;
        _tickerTask = Task.Run(() => RunLoop(_cts.Token));
    }

    private void RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var value = _valueGenerator();
                var reading = new SensorReading(Type, value, DateTimeOffset.Now);
                CurrentReading = reading;
                ReadingChanged?.Invoke(reading);
            }
            catch (Exception ex)
            {
                // A throwing generator or a throwing ReadingChanged subscriber must
                // not silently end this sensor's ticking forever - without this,
                // the while loop above would simply exit, and every consumer would
                // keep using a stale CurrentReading indefinitely with no record
                // anywhere that anything had gone wrong. Thread.Sleep(100) below
                // stays outside this try so a persistently-throwing generator still
                // paces itself at the normal tick rate instead of spinning.
                _audit.LogSensorReadingFailed(Type, ex, DateTimeOffset.Now);
            }

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