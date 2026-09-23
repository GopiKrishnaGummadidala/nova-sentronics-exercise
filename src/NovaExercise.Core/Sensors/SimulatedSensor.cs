using NovaExercise.Core.Logging;

namespace NovaExercise.Core.Sensors;

public sealed class SimulatedSensor : ISensor, IDisposable
{
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(100);

    public SensorType Type { get; }
    public SensorReading? CurrentReading { get; private set; }

    public event Action<SensorReading>? ReadingChanged;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _tickerTask;
    private readonly Func<double> _valueGenerator;
    private readonly ISystemLogger _logger;
    private readonly TimeSpan _tickInterval;

    /// <param name="tickInterval">How often to generate a new reading. Defaults to
    /// 100ms (the exercise's stated sensor-module cadence) when not specified -
    /// callers that need it externally configurable (see Program.cs) pass it in
    /// explicitly instead of this class reading configuration itself.</param>
    public SimulatedSensor(SensorType type, Func<double> valueGenerator, ISystemLogger logger, TimeSpan? tickInterval = null)
    {
        Type = type;
        _valueGenerator = valueGenerator;
        _logger = logger;
        _tickInterval = tickInterval ?? DefaultTickInterval;
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
                _logger.LogSensorReadingFailed(Type, ex, DateTimeOffset.Now);
            }

            Thread.Sleep(_tickInterval);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _tickerTask.Wait(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}