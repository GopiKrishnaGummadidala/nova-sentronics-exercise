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

`ResourceManager` acquires multiple resources in a **global order** (by `ResourceId`: R_A < R_B < R_C). This eliminates the possibility of circular wait, one of the four necessary conditions for deadlock.

Key points:

- Resources are sorted before locking.
- Each resource is locked using `Monitor.TryEnter` with a timeout.
- If any resource cannot be acquired, all previously acquired resources are released.

### Deadlock‑Prone Alternative: `ResourceManagerNaive`

`ResourceManagerNaive` locks resources in the **order requested**, not in a global order. This can lead to deadlock.

**Example scenario:**

- Thread 1: acquire `{R_A, R_B}`
  - Locks R_A, then tries to lock R_B.
- Thread 2: acquire `{R_B, R_A}`
  - Locks R_B, then tries to lock R_A.

Result:

- Thread 1 holds R_A, waits for R_B.
- Thread 2 holds R_B, waits for R_A.
- Circular wait → deadlock.

This is demonstrated in `tests/NovaExercise.ConcurrencyDemos`. Running that demo may hang by design.

### Why the Stage Map Encourages Deadlock

Stage resource requirements:

- `stage_1`: {R_A, R_B}
- `stage_2`: {R_C, R_B}
- `stage_3`: {R_A, R_C}

These form a cycle (A–B–C–A), which is a classic setup for deadlock if locks are taken in inconsistent orders. The global ordering in `ResourceManager` explicitly prevents this.

## Non‑Deadlock Concurrency Bugs

### Atomicity‑Violation Example

A common bug pattern is “check‑then‑act” without synchronization:

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

- All state transitions in `Resource` occur inside `lock (_lock) { ... }`.
- `ResourceManager.Acquire` checks state and marks busy within the same locked region.

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

- `ResourceManagerTests` verify correct behavior under contention (e.g., failed acquisitions release all resources).
- `EndToEndWorkflowTests` exercise the full pipeline with concurrent sensor updates and stage executions.
- `NovaExercise.ConcurrencyDemos` provides a manual demonstration of deadlock with `ResourceManagerNaive`.

## Summary

- Deadlocks are prevented by **global lock ordering** in `ResourceManager`.
- Atomicity violations are avoided by **encapsulating state transitions behind locks**.
- Order violations are avoided by **careful construction and publication** of shared objects.
- The design is intentionally simple and explicit to make concurrency reasoning straightforward.