# Design Rationale

[architecture.md](architecture.md), [concurrency.md](concurrency.md), and
[assumptions.md](assumptions.md) describe **what** the system does and
**what** it assumes. This document is the **why**: for each notable
decision, the alternative that was considered and the reason it lost. It
exists mainly as interview prep — the exercise explicitly asks candidates to
explain design decisions and trade-offs, not just produce working code.

## Sensors

### Event-driven (`ReadingChanged`), not polled

**Decision:** `ISensor` exposes an event; `SimulatedSensor` pushes readings
out on its own timer instead of consumers pulling a `CurrentValue` property
on their own schedule.

**Why:** the exercise explicitly requires "support for multiple independent
consumers of the same sensor data." An event is a multicast delegate — any
number of consumers can subscribe without the sensor knowing they exist or
how many there are. A pull model would need the sensor (or a mediator) to
track a list of registered consumers and their individual poll cadences,
which is strictly more machinery for the same guarantee.

**Trade-off:** event handlers run synchronously on the sensor's own thread
(see `SimulatedSensor.RunLoop`). A slow or throwing consumer would delay or
break that sensor's ticking. `RuleEngine` sidesteps this by doing the
minimum possible work in the handler (record the value, fire-and-forget the
rest) rather than doing real work inline — see the next section.

### Fire-and-forget evaluation, not synchronous-in-handler

**Decision:** `RuleEngine.OnReadingChanged` records the value and calls
`_ = EvaluateAndScheduleAsync()` rather than awaiting it inline.

**Why:** the handler runs on the sensor's ticker thread. If it blocked that
thread — e.g. waiting on `IResourceManager.Acquire`, which can legitimately
wait up to its timeout — the sensor would stop ticking on time, silently
degrading the "every 100ms" contract the exercise specifies.

**Trade-off:** fire-and-forget means the caller can't observe failures
directly. That's why failure reporting was moved into `StageScheduler`
(logged via `IAuditLogger.LogStageFailed`) instead of being left to whoever
happens to be awaiting — see "Audit logging" below. It's also why two
sensors ticking close together can both trigger an evaluation pass; that's
handled at the scheduling layer, not by trying to debounce sensor events.

### Closed `SensorType` enum, not a string-keyed registry

**Decision:** sensors are identified by a `SensorType` enum (`Temperature`,
`Pressure`), not an open string key or a runtime type registry.

**Why:** the exercise's extensibility requirement is "new sensors may be
introduced, existing sensors may be replaced" — not "sensor types must be
addable without recompiling." An enum keeps rule predicates
(`DefaultRules`) type-checked and typo-proof, and keeps `switch` expressions
exhaustive (the compiler flags a missing case). Adding a sensor is a small,
localized, compile-time-checked change: add the enum value, implement
`ISensor`, register it, optionally reference it from a new rule.

**Trade-off:** this is a closed-for-modification choice — a genuinely
plugin-based system (sensors loaded from external assemblies at runtime)
would need string keys or a type registry instead. That's more flexibility
than this exercise's stated requirement calls for, and it would cost the
compile-time safety above; not worth it here.

## Rules

### Rules and their combination policy are separate types

**Decision:** `StageRule` (a predicate → set of stages) is a plain data
type; *how* multiple matching rules combine is a separate strategy,
`IRuleEvaluationPolicy`, with `UnionRuleEvaluationPolicy` as the only
implementation.

**Why:** these are two independent axes of change. The business rules
themselves will change often (thresholds, new sensor combinations) — that's
`DefaultRules.Create()`. How conflicts between rules resolve changes far
less often, and choosing it independently (union vs. priority vs.
first-match) shouldn't require touching every rule definition. Splitting
them is a Strategy pattern applied to the one place in this system where the
exercise's "process is expected to evolve" note clearly applies to *policy*,
not just data.

**Trade-off:** for exactly three rules, this is more indirection than a
single `if/else` chain would need. It's justified here because the
interfaces exist as an intentional extension point (see
[architecture.md](architecture.md) "Extensibility Points"), not because
three rules alone demand it.

## Resources & concurrency

### Deadlock-freedom via "never hold-and-wait," not lock ordering

**Decision:** `ResourceManager.Acquire` claims resources one at a time with
a single atomic `Resource.TryMarkBusy()`, rolling back everything already
claimed the moment one can't be had. It never holds one resource's lock
while blocked waiting on another.

**Why:** this was originally going to be "sort resources by `ResourceId`
and lock in that order" — the textbook fix for circular wait. But the
resource lease has to survive an `await` in `SimulatedStageExecutor`
(`Acquire(...)` then `await Task.Delay(...)` before releasing). `Monitor` is
thread-affine: a lock taken before an `await` can end up released from a
different thread after the continuation resumes on a different pool thread,
which throws `SynchronizationLockException`. That rules out "hold a real
lock across the whole operation" as a safe option here at all — ordering the
locks wouldn't have mattered, because holding them was the actual problem.
Never holding two at once sidesteps the issue entirely and happens to give a
*stronger* guarantee than ordering: it's deadlock-free regardless of
acquisition order, not just the order the code happens to enforce.

**Trade-off:** giving up on "wait for a signal when a resource frees up" in
favor of polling (`WaitUntilIdle` spins with a 5ms sleep). That's simple and
correct at this scale (3 resources, sub-second timeouts) but wastes a thread
under real contention; a `SemaphoreSlim`-per-resource would be the next step
if this needed to scale to many more resources or longer waits.

### The naive manager is a real, separate class — not a comment

**Decision:** `ResourceManagerNaive` is a fully working, independently
runnable implementation of the same interface, exercised by its own console
demo, rather than a code comment describing "the bug we didn't write."

**Why:** the exercise explicitly asks candidates to *present* a deadlock,
not just describe one in prose. A class that actually deadlocks when run is
verifiable — anyone can run `NovaExercise.ConcurrencyDemos` and watch both
threads block for the real acquisition timeout before failing. It also
means the "bad" hold-and-wait pattern never has a path into the production
`ResourceManager` — there's no shared code or shortcut between the two, so
fixing one can't silently reintroduce the bug in the other.

**Trade-off:** some duplication between the two resource managers (both
implement `IResourceManager`, both loop over requested resources). Given
one of them exists purely to demonstrate a failure mode, sharing code
between "demonstrates a bug on purpose" and "must never have that bug" felt
like the wrong kind of DRY — a shared helper touched while fixing the real
manager could silently patch the demo too.

### Stage scheduling owns "did this actually start," not the rule engine

**Decision:** `StageScheduler.ScheduleStagesAsync` reserves a stage slot
with an atomic `_running.TryAdd(id, ...)` and only then logs it as scheduled
and runs it. `RuleEngine` just asks for stages to run; it has no idea
whether the request turned into a real execution.

**Why:** "at most one instance of a stage runs at a time" and "the audit
log reflects what actually ran" are the same invariant looked at from two
angles, and there's exactly one place that can honestly answer "is this
stage running right now" — whichever component holds the running-stages
table. Putting the audit log anywhere else (e.g., logging every time a rule
*matches*, which is what the first version did) means it can fire more than
once for a single real execution whenever two sensors tick close together,
since rule evaluation runs once per sensor event, independent of whether
the stage it points at is already running.

**Trade-off:** `RuleEngine` gave up the ability to log "a rule matched" as
distinct from "a stage started" — those are no longer separately observable
without adding that back deliberately (e.g., a distinct
`LogRuleMatched` if that granularity is ever needed).

## Overall shape

### Plain console app, not a hosted service / web API

**Decision:** `NovaExercise.App` is a top-level-statements console program
that wires up dependencies by hand and runs until Ctrl+C — no ASP.NET Core,
no generic host, no `IHostedService`.

**Why:** the actual domain is a long-lived reactive loop over sensor events,
not request/response — there's no client making calls into this process.
Pulling in a web/hosting framework wouldn't add a capability this problem
needs; it would add configuration, middleware, and DI-container concepts to
explain that don't map to anything in the business scenario. Simplicity and
clarity are explicit evaluation criteria, and "smallest thing that models
the domain honestly" was weighed above "looks more like a typical service."

**Trade-off:** manual wiring in `Program.cs` doesn't scale gracefully much
past the current handful of components — a real product with more moving
parts would likely want a proper DI container at that point. For this
scope, manual composition is more transparent (every dependency is visible
at the call site) than a container would be.

### In-memory only; audit trail is the durability story

**Decision:** no database, no file persistence. `IAuditLogger` writes to the
console.

**Why:** nothing in the business scenario calls for state to survive a
restart — sensor readings are transient by nature, and "what ran and when"
is the one thing worth keeping, which is exactly what the audit log
captures. Making it an interface rather than a concrete `Console.WriteLine`
call means swapping in file- or database-backed persistence later is a new
`IAuditLogger` implementation, not a change to `StageScheduler` or
`RuleEngine`.

**Trade-off:** if the audit trail needs to survive a process crash or be
queried later, a `Console`-only implementation obviously doesn't cut it.
That's a deliberate "document the assumption, defer the implementation"
call for this exercise's scope (see [assumptions.md](assumptions.md)), not
an oversight.
