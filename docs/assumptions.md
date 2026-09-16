# Assumptions

This document lists key assumptions made in the design and implementation.

## Rule Evaluation Semantics

- When multiple rules match on a sensor update, the system executes the **union** of all stages indicated by matching rules.
- This behavior is implemented by `UnionRuleEvaluationPolicy`.
- The design allows alternative policies (e.g., priority‑based) by implementing `IRuleEvaluationPolicy`.

## Stage Lifecycle

- At most **one instance of a given stage** runs at any time.
- If a stage is already running and is requested again, the new request is ignored.
- Running stages execute to completion even if sensor conditions change mid‑execution.
- New sensor readings only affect **future** stage starts.

## Resource Contention

- A stage waits until **all** its required resources are available.
- Acquisition uses a timeout to avoid indefinite blocking.
- If any required resource is in **Error** state, the stage cannot start.
- Resources are released immediately after stage execution completes.

## Error Handling

- If a resource enters **Error** state:
  - No new stages requiring that resource will start.
  - Running stages are allowed to complete (simplified model).
- Exceptions in stage execution are caught and logged; they do not crash the process.

## Sensors & Extensibility

- The system supports **dynamic registration** of sensors via `ISensorRegistry`.
- The demo uses two simulated sensors (Temperature, Pressure), but additional sensors can be added without changing core logic.
- Sensor updates are treated as fire‑and‑forget events; the latest value is always used for rule evaluation.

## Persistence

- No database or persistent storage is used.
- The system operates entirely in memory.
- Audit information (which stages were scheduled, sensor values at that time, and required resources) is written to the console via `AuditLogger`.
- This console-based audit logging could be replaced by a persistent logging mechanism (file, database, or centralized logging service) without changing the core logic.

## Concurrency Model

- .NET `Task`‑based asynchronous pattern is used for stage execution.
- Shared mutable state is protected using `lock` and concurrent collections (`ConcurrentDictionary`).
- The design prioritizes clarity and correctness over maximum throughput, aligning with the exercise’s focus on engineering judgment and concurrency safety.