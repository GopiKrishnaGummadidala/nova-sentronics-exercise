# Assumptions

This document lists key assumptions made in the design and implementation.

## Rule Evaluation Semantics

- When multiple rules match on a sensor update, the system executes the **union** of all stages indicated by matching rules.
- This behavior is implemented by `UnionRuleEvaluationPolicy`.
- The design allows alternative policies (e.g., priority‑based) by implementing `IRuleEvaluationPolicy`.

## Stage Lifecycle

- At most **one instance of a given stage** runs at any time, enforced by an
  atomic `ConcurrentDictionary.TryAdd` reservation in `StageScheduler` (a
  plain check‑then‑act here would let two sensors' near‑simultaneous updates
  both start the same stage — see [concurrency.md](concurrency.md)).
- If a stage is already running and is requested again, the new request is ignored.
- Running stages execute to completion even if sensor conditions change mid‑execution.
- New sensor readings only affect **future** stage starts.

## Resource Contention

- A stage waits until **all** its required resources are available, up to a
  single timeout **shared across the whole request** (not re‑applied in full
  per resource — acquiring 2 resources with a 5s timeout can wait up to 5s
  total, not 10s).
- If a resource is transiently **Busy**, acquisition polls until it frees up
  or the shared timeout elapses, then throws `TimeoutException`.
- If any required resource is in **Error** state, the stage cannot start —
  this fails immediately rather than waiting out the timeout, since retrying
  won't help, and raises `InvalidOperationException`.
- An already-cancelled `CancellationToken` always throws `OperationCanceledException`
  immediately, even if every requested resource is currently free — cancellation
  is checked before the first acquisition attempt, not only while polling a busy
  resource, so it can't be silently ignored by a lucky timing.
- Resources are released immediately after stage execution completes.

## Error Handling

- If a resource enters **Error** state:
  - No new stages requiring that resource will start.
  - Running stages are allowed to complete (simplified model).
- Exceptions in stage execution are caught inside `StageScheduler` and logged
  via `IAuditLogger.LogStageFailed`; they do not crash the process.
  `OperationCanceledException` is treated as expected shutdown noise and is
  not logged as a failure. (Earlier in development this task's exceptions
  were unobserved — the executing `Task` was stored but never awaited or
  inspected for faults — so failures were silently dropped instead of logged;
  this is now covered by an explicit `catch`.)
- The same class of bug existed one layer up: `RuleEngine.EvaluateAndScheduleAsync`
  is invoked fire-and-forget (its returned `Task` is discarded), so an
  exception anywhere in it — a throwing rule predicate, or `StageScheduler`
  itself throwing (e.g. a rule pointing at a `StageId` it has no case for) —
  would otherwise fault that `Task` silently. Both are real risks, since rules
  and stage requirements are explicit extensibility points. Both are now
  caught by one try/catch around the whole evaluate-then-schedule sequence and
  logged via `IAuditLogger.LogRuleEvaluationFailed`; the reading that
  triggered it is simply not acted on, and later readings are unaffected.
- Sensors are a third instance of the same shape: `SimulatedSensor.RunLoop`
  calls a caller-supplied value-generator and invokes `ReadingChanged` on its
  own background thread, with no supervisor watching it. A throwing generator
  or a throwing subscriber used to end that sensor's ticking permanently and
  silently — every consumer would keep using its last stale reading forever
  with no record anything had gone wrong. Now each tick is individually
  caught and logged via `IAuditLogger.LogSensorReadingFailed`; the loop
  continues to the next tick 100ms later rather than dying.

## Sensors & Extensibility

- The system supports **dynamic registration, removal, and replacement** of
  sensors via `ISensorRegistry`, including while a `RuleEngine` is already
  running — `ISensorRegistry.Register`/`Unregister` raise
  `SensorRegistered`/`SensorUnregistered`, and `RuleEngine` reacts to them
  rather than only reading the registry once at construction.
- The demo uses two simulated sensors (Temperature, Pressure), but additional sensors can be added without changing core logic.
- Sensor updates are treated as fire‑and‑forget events; the latest value is always used for rule evaluation.
- Concurrent `Register`/`Unregister` calls **for the same `SensorType`** from
  multiple threads at once aren't specifically hardened against beyond basic
  correctness (no corruption, no duplicate subscriptions) — registration
  changes are assumed to be infrequent, effectively serialized,
  operator-initiated events, not high-frequency traffic like sensor readings
  are.

## Persistence

- No database or persistent storage is used.
- The system operates entirely in memory.
- Audit information is written to the console via `AuditLogger`, logged by
  `StageScheduler` at the moment a stage actually **starts** (not merely when
  a rule matches it) — including which stage, the sensor values at that time,
  and the required resources — plus a `LogStageFailed` entry if execution
  throws.
- This console-based audit logging could be replaced by a persistent logging mechanism (file, database, or centralized logging service) without changing the core logic.

## Concurrency Model

- .NET `Task`‑based asynchronous pattern is used for stage execution.
- Shared mutable state is protected using `lock` and concurrent collections (`ConcurrentDictionary`).
- The design prioritizes clarity and correctness over maximum throughput, aligning with the exercise’s focus on engineering judgment and concurrency safety.
