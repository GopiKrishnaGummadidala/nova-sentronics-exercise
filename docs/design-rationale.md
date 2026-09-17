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

### Sensor publication is in-process, not cross-process

**Decision:** `ISensor.ReadingChanged` is an in-process .NET event (a
multicast delegate). There is no message broker, queue, or IPC mechanism
between a sensor and its consumers — everything runs inside one process.

**Why:** the exercise's exact wording is "support for multiple independent
consumers **(i.e., processes)** of the same sensor data." The section above
already covers why a multicast event satisfies "multiple independent
consumers" structurally — any number of handlers can subscribe without the
sensor knowing they exist. The parenthetical "(i.e., processes)" is a
separate, more literal question: does this design let independent *OS
processes* consume the same stream? As implemented, no — read narrowly,
that would need cross-process transport. Read in the context of the rest of
the brief, though — a single console app, resources explicitly "faked"
with an in-memory class, no persistence, no mention anywhere of a
deployment topology with multiple services — "processes" is far more
likely informal shorthand for "independent consumer logic" than a literal
OS-process requirement. Given that, and given the exercise's own
instruction to avoid external resources "unless absolutely necessary,"
introducing a real broker (RabbitMQ/Kafka) to hedge against the narrower
reading would mean adding infrastructure the rest of this design
deliberately avoids, to solve a problem the brief never actually describes.

**Trade-off:** if a reviewer does mean literal process isolation — e.g., a
real deployment where a UI, a logger, and a controller run as separate
services — this implementation doesn't reach that on its own, and that
boundary is named here explicitly rather than glossed over. `ISensor`'s
only real obligation is "raise `ReadingChanged`, notify whoever's
listening"; replacing that one publication step with a message broker (a
small adapter that republishes each reading, e.g. to a queue or pub/sub
topic) would let independent processes each run their own consumer without
changing `RuleEngine`, `StageScheduler`, or anything downstream:

```
Sensor Gateway
      ↓
Sensor Message Bus
      ├── Process A
      ├── Process B
      └── Process C
```

The gateway publishes each reading once; `RuleEngine` and any other
consumer would each subscribe to the bus independently — the same
subscribe-and-react shape they already use for the in-process event today.
Only what sits behind `ISensor` would change.

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

### RuleEngine reacts to registry changes, not just a one-time constructor scan

**Decision:** `ISensorRegistry` raises `SensorRegistered`/`SensorUnregistered`;
`RuleEngine` subscribes to both and keeps its own sensor subscriptions in
sync for as long as it runs, instead of reading `_sensors.Sensors` once in
its constructor and never again.

**Why:** this was flagged in review, and the first instinct was to document
it as a boundary rather than fix it — `SensorRegistry.Register`/`Unregister`
already worked at any time, so "sensor registration/management," one of the
exercise's named required capabilities, was already demonstrably satisfied;
the gap was only that a running `RuleEngine` never noticed. Documenting a
boundary was the right call earlier in this project for a much bigger gap
(cross-process consumers, above) precisely because closing it meant adopting
an entire message-broker dependency the rest of the design deliberately
avoids. This is a different shape of decision: the fix is a small, in-process
extension of a pattern the code already uses everywhere else — subscribe to
an event, react to it — not a new architectural layer. Weighed against the
exercise's explicit, named requirement ("new sensors may be introduced...
existing sensors may be replaced"), a small fix that directly satisfies a
literal reading beat a documented excuse for not having it.

**Trade-off:** subscribing to `SensorRegistered` before the constructor
snapshots the current sensor list means a sensor registered in that exact
gap would both fire the event and appear in the snapshot — without a guard,
double-subscribing it and processing every one of its readings twice.
`RuleEngine` now tracks which `SensorType`s it's subscribed to and only acts
once per type, which closes that window, but it's a second piece of state
(a lock-guarded `HashSet<SensorType>`) purely to make an ordering question
not matter — one more thing a future reader has to understand alongside the
subscription logic itself. Also out of scope: two threads calling
`Register`/`Unregister` for the *same* `SensorType` at the same instant
aren't specifically hardened beyond basic correctness — see
[assumptions.md](assumptions.md). Replacing a live sensor is treated as an
infrequent, operator-initiated action, not something happening at
sensor-reading frequency.

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

**Decision:** `ResourceManager.AcquireAsync` claims resources one at a time
with a single atomic `Resource.TryMarkBusy()`, rolling back everything
already claimed the moment one can't be had. It never holds one resource's
lock while blocked waiting on another.

**Why:** this was originally going to be "sort resources by `ResourceId`
and lock in that order" — the textbook fix for circular wait. But the
resource lease has to survive an `await` in `SimulatedStageExecutor`
(`await AcquireAsync(...)` then `await Task.Delay(...)` before releasing).
`Monitor` is thread-affine: a lock taken before an `await` can end up
released from a different thread after the continuation resumes on a
different pool thread, which throws `SynchronizationLockException`. That
rules out "hold a real lock across the whole operation" as a safe option
here at all — ordering the locks wouldn't have mattered, because holding
them was the actual problem. Never holding two at once sidesteps the issue
entirely and happens to give a *stronger* guarantee than ordering: it's
deadlock-free regardless of acquisition order, not just the order the code
happens to enforce.

**Trade-off:** giving up on "wait for a signal when a resource frees up" in
favor of polling (`WaitUntilIdleAsync` spins on `await Task.Delay(5ms, ct)`).
Polling via `Task.Delay` rather than blocking on `Thread.Sleep` means a
caller waiting on a busy resource no longer parks a thread-pool thread for
the wait, but it's still not signal-based: acquiring can still take up to
one `PollInterval` of avoidable latency after the resource actually frees
up. Correct and cheap enough at this scale (3 resources, sub-second
timeouts); a `SemaphoreSlim`-per-resource would be the next step if this
needed to scale to many more resources or much tighter acquire-latency
requirements.

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
that runs until Ctrl+C — no ASP.NET Core, no generic host, no
`IHostedService`.

**Why:** the actual domain is a long-lived reactive loop over sensor events,
not request/response — there's no client making calls into this process.
Pulling in a hosting framework wouldn't add a capability this problem needs;
it would add configuration binding and middleware concepts to explain that
don't map to anything in the business scenario. Simplicity and clarity are
explicit evaluation criteria, and "smallest thing that models the domain
honestly" was weighed above "looks more like a typical service."

### Composition root uses `Microsoft.Extensions.DependencyInjection`

**Decision:** `Program.cs` registers each component's interface against its
implementation in a `ServiceCollection` and resolves `IRuleEngine` from the
built `ServiceProvider`, rather than a chain of `new` calls.

**Why:** this was originally manual construction — the reasoning was that
~8 components is small enough that a container's main value (managing a
large, tangled graph) doesn't really kick in, and explicit `new` calls are
arguably *more* transparent (no runtime resolution to trace through).
That's still true as far as it goes, but registration reads more clearly
than an equivalent-length chain of locals once every dependency needs to be
findable by its interface rather than by scrolling to where it was
constructed, and `Microsoft.Extensions.DependencyInjection` costs nothing
extra to bring in — no hosting, configuration, or middleware attached to it,
just the container. Net effect: the object graph is still fully visible in
one file, just declared as registrations instead of imperative statements.

**Trade-off:** the two `SimulatedSensor` instances don't fit the container
well - each needs its own `SensorType` and value-generator lambda, so
there's no single "the" `ISensor` implementation to register. They're
constructed directly and only the `ISensorRegistry` they're published
through is resolved from the container. Forcing instance-specific
configuration through DI (e.g. keyed services) would have been more
ceremony for no real benefit - not everything needs to go through the
container just because a container exists.

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
