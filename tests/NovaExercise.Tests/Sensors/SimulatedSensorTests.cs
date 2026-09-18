using NovaExercise.Core.Logging;
using NovaExercise.Core.Resources;
using NovaExercise.Core.Sensors;
using NovaExercise.Core.Stages;

namespace NovaExercise.Tests.Sensors;

public class SimulatedSensorTests
{
    [Fact]
    public async Task RunLoop_GeneratorThrowsOnce_LogsTheFailure_AndKeepsTickingAfterward()
    {
        // Before this fix, RunLoop had no try/catch at all: a single throwing
        // generator call (or a throwing ReadingChanged subscriber) let the
        // exception escape the while loop's body entirely, silently and
        // permanently ending that sensor's ticking - every consumer would keep
        // using its last stale CurrentReading forever with no record anywhere
        // that anything had gone wrong.
        var callCount = 0;
        var logger = new SpySystemLogger();

        using var sensor = new SimulatedSensor(SensorType.Temperature, () =>
        {
            var n = Interlocked.Increment(ref callCount);
            if (n == 1)
                throw new InvalidOperationException("Simulated sensor fault");
            return 42.0;
        }, logger);

        var readingReceived = new SemaphoreSlim(0);
        sensor.ReadingChanged += _ => readingReceived.Release();

        Assert.True(await logger.WaitForFailureAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(logger.LastFailureException);
        Assert.Equal(SensorType.Temperature, logger.LastFailureSensorType);

        // The loop must still be alive and ticking after the failed attempt.
        Assert.True(await readingReceived.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(42.0, sensor.CurrentReading!.Value);
    }

    private sealed class SpySystemLogger : ISystemLogger
    {
        private readonly SemaphoreSlim _signal = new(0);

        public Exception? LastFailureException { get; private set; }
        public SensorType? LastFailureSensorType { get; private set; }

        public void LogStageScheduled(
            StageId stageId,
            IReadOnlyDictionary<SensorType, double> sensorValues,
            IReadOnlyCollection<ResourceId> requiredResources,
            DateTimeOffset timestamp)
        {
        }

        public void LogStageFailed(StageId stageId, Exception exception, DateTimeOffset timestamp)
        {
        }

        public void LogRuleEvaluationFailed(
            Exception exception,
            IReadOnlyDictionary<SensorType, double> sensorValues,
            DateTimeOffset timestamp)
        {
        }

        public void LogSensorReadingFailed(SensorType sensorType, Exception exception, DateTimeOffset timestamp)
        {
            LastFailureException = exception;
            LastFailureSensorType = sensorType;
            _signal.Release();
        }

        // WaitAsync, not the blocking Wait: a synchronous wait here would tie up a
        // real thread-pool thread inside an async test, and under a full parallel
        // test run that can starve the pool badly enough to blow past even a
        // multi-second timeout.
        public Task<bool> WaitForFailureAsync(TimeSpan timeout) => _signal.WaitAsync(timeout);
    }
}
