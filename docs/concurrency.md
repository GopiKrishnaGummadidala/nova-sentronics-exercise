# Concurrency Design

This document explains how the solution handles concurrency, prevents deadlocks, and avoids common concurrency bugs.

## Shared State & Concurrency Hotspots

The main concurrency concerns are:

- **Resource state** (Idle/Busy/Error) in `Resource` and `ResourceManager`.
- **Stage scheduling** in `StageScheduler` (tracking running stages).
- **Sensor readings** in `RuleEngine` (current sensor values dictionary).

All shared mutable state is protected using locks or concurrent collections.

## Deadlock Prevention

### Resource Acquisition Strategy

`ResourceManager` avoids one of the four necessary conditions for deadlock —
**hold‑and‑wait** — entirely: it never blocks on one resource while holding
another. Each resource is claimed with a single atomic
`TryMarkBusy()` (idle‑check + busy‑flag in one locked step); if a resource is
transiently Busy it polls with a *shared* timeout budget across the whole
request, and if a resource is unavailable when the budget runs out, every
resource already claimed for this request is released before the exception
propagates. Because no lock is ever held across a wait for a different
resource, circular wait cannot form, regardless of acquisition order.

Key points:

- Resources are still sorted by `ResourceId` before acquiring them. This is
  defense‑in‑depth (keeps contention patterns deterministic across callers
  requesting overlapping sets) rather than the actual deadlock‑freedom
  mechanism — the hold‑and‑wait‑free design above is what matters.
- The acquisition timeout is a single deadline shared across all requested
  resources, not re‑applied in full to each one.
- If any required resource cannot be claimed, all previously‑claimed
  resources for that request are released before the exception propagates.

### Deadlock‑Prone Alternative: `ResourceManagerNaive`

`ResourceManagerNaive` locks resources in the **order requested** (not a
global order) and, unlike `ResourceManager`, **holds each lock while waiting
for the next one** — genuine hold‑and‑wait.

**Example scenario:**

- Thread 1: acquire `{R_A, R_B}`
  - Locks R_A, holds it, blocks trying to lock R_B.
- Thread 2: acquire `{R_B, R_A}`
  - Locks R_B, holds it, blocks trying to lock R_A.

Result:

- Thread 1 holds R_A, waits for R_B.
- Thread 2 holds R_B, waits for R_A.
- Circular wait → real deadlock: both threads are genuinely blocked at the
  same time, each holding what the other needs.

This is demonstrated in `tests/NovaExercise.ConcurrencyDemos`, and verified by
running it: both threads block for the full acquisition timeout (~5s) before
failing with `TimeoutException`. The timeout is a deliberate safety valve for
running this as an unattended demo — with `Monitor.Enter` (no timeout) in
place of `Monitor.TryEnter`, the same code would hang forever, which is the
"may hang" behavior this class originally described in comments but did not
actually exhibit (see note below).

> **Note on an earlier version of this demo:** the naive manager originally
> released each resource's lock before moving on to the next one, so no lock
> was ever held while waiting for another — meaning it could never actually
> deadlock. Two racing threads would instead fail near‑instantly with
> `InvalidOperationException` (a busy‑state race, not a hang). That was itself
> a live example of an unintended atomicity violation standing in for the
> deadlock the class was meant to demonstrate. The current version fixes this
> by holding each lock for the duration of the whole acquisition.

### Why the Stage Map Encourages Deadlock

Stage resource requirements:

- `stage_1`: {R_A, R_B}
- `stage_2`: {R_C, R_B}
- `stage_3`: {R_A, R_C}

These form a cycle (A–B–C–A), which is a classic setup for deadlock if locks are taken in inconsistent orders. The global ordering in `ResourceManager` explicitly prevents this.

## Non‑Deadlock Concurrency Bugs

### Atomicity‑Violation Example (found in this codebase, not just illustrative)

`StageScheduler.ScheduleStagesAsync` originally did a check‑then‑act on a
`ConcurrentDictionary` that tracks in‑flight stages:

```csharp
// BUGGY: ContainsKey (check) and the later indexer assignment (act) are two
// separate operations - not atomic together, even though each one alone is
// thread-safe on a ConcurrentDictionary.
if (_running.ContainsKey(id))
    continue;
...
_running[id] = task; // a second thread can reach here before this line runs
```

Because `RuleEngine` reacts to sensor events, and the Temperature and Pressure
sensors tick on two independent threads, two calls to `ScheduleStagesAsync`
for the *same* stage can happen within microseconds of each other. Both can
pass the `ContainsKey` check before either writes to the dictionary, so both
start a `Task.Run` for the same stage — violating the "at most one instance
of a given stage runs at a time" invariant (see
[assumptions.md](assumptions.md)). This was verified with a standalone
200,000‑iteration repro of the same check‑then‑act shape (5 double‑passes
observed), and empirically in this app: with the race in place, the audit log
would show the same stage "scheduled" twice at the same timestamp whenever
both sensors ticked close together.

**How we fixed it:** replace the check‑then‑act with a single atomic
operation, `_running.TryAdd(id, ...)`. `TryAdd` either claims the slot
exclusively or fails if another caller already owns it — there is no window
between checking and acting because there is only one call.

### Cancellation Edge Case (also found in this codebase)

`StageScheduler` originally passed `ct` as `Task.Run`'s own cancellation
token, not just to the awaited work inside:

```csharp
var task = Task.Run(async () => { /* ... finally { _running.TryRemove(id, out _); } */ }, ct);
```

Verified against the BCL: if `ct` is *already* cancelled at the moment
`Task.Run` is called, .NET returns an already‑`Canceled` `Task` **without
ever invoking the delegate** — so the `finally` block never runs either.
The stage's slot in `_running` is left permanently occupied, and `TryAdd`
for that stage id fails forever after, blocking it from ever being
scheduled again for the lifetime of that `StageScheduler`. In the current
app this is low‑impact (the whole process exits shortly after `Stop()`),
but it's a real, reachable bug: a sensor reading delivered after
`RuleEngine.Stop()` cancels its token, but before `Dispose()` unsubscribes,
carries an already‑cancelled token straight into this path.

**How we fixed it:** stopped passing `ct` to `Task.Run` itself, so the
delegate body always runs regardless of the token's state; cancellation is
still fully honored via `await _executor.ExecuteAsync(def, ct)` inside,
where the `finally` is guaranteed to execute either way.

### Atomicity‑Violation Example (resource state)

The same bug shape applies to resource state:

```csharp
// BUGGY pseudo-code
if (resource.State == ResourceState.Idle)
{
    resource.State = ResourceState.Busy; // not atomic with the check
    // use resource
}
```

Two threads could both see `Idle` and both transition to `Busy`, violating exclusivity.

**How we avoid it:**

- `Resource.TryMarkBusy()` performs the check and the transition inside a
  single `lock (_lock) { ... }` block, so there is no window between them.
- `ResourceManager.Acquire` only ever calls this one atomic method — it never
  reads `State` and decides separately whether to call a setter.

### Order‑Violation Example

An order‑violation can occur if initialization and usage are not properly synchronized:

```csharp
// Thread 1: initialization
resources = InitializeResources();
isReady = true;

// Thread 2: worker
while (!isReady) Thread.Yield();
Use(resources); // may see partially initialized resources
```

**How we avoid it:**

- Shared objects (e.g., `SensorRegistry`, `ResourceManager`) are constructed before being shared.
- `RuleEngine` subscribes to sensors in its constructor; no background thread uses it before construction completes.
- We rely on .NET’s memory model and proper task scheduling; no unsafe “publish then initialize” patterns are used.

## Testing Concurrency

- `ResourceManagerTests` verify correct behavior under contention (e.g., failed acquisitions release all resources, busy resources cause a `TimeoutException` rather than corrupting state).
- `StageSchedulerTests.ScheduleStagesAsync_ConcurrentCallsForSameStage_NeverRunsMoreThanOneAtOnce` fires 50 concurrent `ScheduleStagesAsync` calls for the same stage and asserts the executor never observes more than one concurrent execution — a regression test for the atomicity violation above.
- `EndToEndWorkflowTests` exercise the full pipeline with concurrent sensor updates and stage executions.
- `NovaExercise.ConcurrencyDemos` provides a manual, genuinely‑reproducing demonstration of deadlock with `ResourceManagerNaive`.

## Summary

- Deadlocks are prevented in `ResourceManager` by **never holding one resource while waiting on another** (avoiding hold‑and‑wait), with sorted acquisition order kept as harmless defense‑in‑depth.
- `ResourceManagerNaive` deliberately does the opposite — holds a lock while waiting for the next one — to give a genuine, reproducible deadlock for the exercise's "present at least one deadlock" requirement.
- Atomicity violations are avoided by using single atomic operations (`TryMarkBusy`, `ConcurrentDictionary.TryAdd`) instead of separate check‑then‑act steps — including a real one found and fixed in `StageScheduler` during review, not just the textbook `Resource.State` example.
- Order violations are avoided by **careful construction and publication** of shared objects.
- The design is intentionally simple and explicit to make concurrency reasoning straightforward.
