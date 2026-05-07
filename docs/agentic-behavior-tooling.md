# Agentic Behavior Tooling

TurtleQuest keeps behaviors as first-class host skills, but exposes them through Agentica tools so the agent can choose and compose them.

## Current Shape

```text
Agentica planner host
  turtlequest.get_context
  turtlequest.get_receipts
  turtlequest.behavior.find_tree
  turtlequest.behavior.harvest_tree
  turtlequest.behavior.build_column
  turtlequest.behavior.excavate_rectangular_pit
  future: turtlequest.behavior.bootstrap_home_storage
  future: turtlequest.behavior.tunnel_line
  future: turtlequest.behavior.create_mineshaft
  turtlequest.emit_compiled_plan

Bridge
  validates TurtleCompiledPlan
  queues primitive commands

NeoForge mod
  executes primitive commands against CC:T
  returns receipts
```

## Tool Roles

`turtlequest.get_context`

Returns the public planning surface:

```text
goal
behavior id
behavior arguments
command budget
supported primitives
execution rules
behavior tool surface
runtime failure, when present
```

`turtlequest.get_receipts`

Returns receipt evidence from the current planning context. For initial plans this is normally empty. For runtime replans this becomes the primary evidence surface.

`turtlequest.behavior.*`

Behavior tools are planner assists. They do not mutate Minecraft. They return:

```text
behavior id
normalized arguments
transition contract
recommended primitive steps
completion rule
```

`turtlequest.emit_compiled_plan`

Emits the final `TurtleCompiledPlan` artifact. The bridge validates this artifact before anything touches Minecraft.

## Why This Matters

The agent should not invent turtle robotics from raw atoms every time. It should be able to choose durable skills:

```text
find_tree
harvest_tree
build_column
excavate_rectangular_pit
bootstrap_home_storage
tunnel_line
create_mineshaft
```

Those skills can still expand into receipt-backed primitives:

```text
scanNearby
moveTowardRelative
digRememberedTarget
fellRememberedTree
getInventory
returnHome
completeObjective
```

Long-running skills should expose requirement/blocker surfaces rather than silently failing. Example:

```text
branch_mine requires home_storage
Agentica sees home_storage missing
Agentica chooses bootstrap_home_storage
bootstrap_home_storage crafts or places a chest/barrel when possible
receipt marks requirementStatus=satisfied and resumeRecommended=true
Agentica resumes branch_mine
```

This keeps the agent in the driver seat while giving it enough host truth to reason about workflow prerequisites.

This is the intended split:

```text
Agentica chooses the behavior and composes the plan.
Behavior tools expose known safe procedures and invariants.
Bridge validation prevents illegal IR.
Minecraft remains the authoritative executor.
Receipts determine success.
```

## Current Trace Gap

The bridge trace records the generated final plan and execution receipts.

Player-facing progress is separate from traces. See:

```text
docs/player-progress-surface.md
```

That surface exposes operational status, not hidden model chain-of-thought. It can include command stages, behavior choices, receipt summaries, blocked reasons, and explicit LLM plan/status artifacts.

The planner host now also records Agentica-side tool events under:

```text
run/traces/planner-host/<trace-id>/events.jsonl
```

Use:

```powershell
./scripts/show-latest-planner-host-trace.ps1
```

The trace captures:

```text
planner host start
Agentica runner events
step.started with tool id
tool input
receipt status
observation summary
artifact id
final emitted plan id
```

The current tool sequence is enforced by receipts:

```text
turtlequest.get_context
turtlequest.get_receipts      runtime replans only
turtlequest.behavior.*
turtlequest.emit_compiled_plan
```

If the agent tries to call a behavior tool or emit a plan before context, the tool returns a refused receipt and the stepwise planner must recover.

For runtime replans, behavior tools and plan emission are also refused until `turtlequest.get_receipts` has been called.

The planner policy permits a small read-only batch of two steps. This allows runtime replans to gather context and receipts together while still forcing behavior selection and plan emission through receipt-backed observations.

## Definition Of Done For This Layer

The layer is good enough when:

```text
Agentica can inspect current context through a tool.
Agentica can inspect receipts during replan.
Agentica can select a behavior tool.
Agentica can emit validated flattened IR.
The bridge rejects unsupported primitives or unsafe behavior-specific shapes.
The mod emits receipt-backed world/inventory evidence.
```

The layer is not complete until runtime replanning uses the same tool surface after real failed Minecraft receipts.
