# Planner Boundary

TurtleQuest should let Agentica compile plans when no catalog behavior fits, but the bridge must make the boundary explicit.

## Current Slice

The bridge exposes:

```text
GET  /behaviors
POST /planner/preview
POST /planner/compile
```

`/behaviors` lists catalog-backed behaviors currently loaded from `behaviors/*.json`.

`/planner/preview` classifies a player request without executing it.

`/planner/compile` returns a bounded TurtleQuest IR plan and validation result without executing it.

Current plan kinds:

```text
catalog
not_executable
compiled_behavior
needs_clarification
compile_blocked
unsupported
```

Current behavior ids:

```text
turtlequest.dig_line_return
turtlequest.excavate_rectangular_pit
turtlequest.unsupported
```

## Why This Exists

The bridge should not silently map a novel task to the nearest old behavior. For example:

```text
Dig a 5x5 pit and return.
```

must not become:

```text
Dig a straight tunnel 5 blocks forward and return.
```

The correct current response is:

```text
known intent: turtlequest.excavate_rectangular_pit
executable: false
arguments: width=5, length=5, depth missing or parsed
warning: no catalog-backed executor exists yet
```

The current compile boundary supports:

```text
catalog behavior expansion
deterministic baseline compilation for shallow rectangular pits
validation of legal primitive actions
validation of finite command budget
explicit blocking when the request crosses the LLM-backed execution boundary
```

Example:

```text
Dig a 5x5 pit 1 block deep.
  -> compiled_behavior
  -> source deterministic_baseline
  -> valid true

Dig a 5x5 pit 2 blocks deep and return.
  -> compile_blocked
  -> deterministic baseline refuses
  -> first LLM-backed execution boundary
```

## Target Shape

The future flow should be:

```text
1. Try exact catalog behavior.
2. Try known intent with missing parameter detection.
3. If no catalog behavior fits, ask Agentica to compile TurtleQuest IR.
4. Validate the compiled IR against host policy.
5. Execute validated primitive commands only.
6. Replan only from receipts.
```

The LLM is allowed to make choices and compile behavior trees. The host remains the safety kernel.

## LLM Testing Boundary

Start full LLM-backed planning and execution immediately after these two local checks pass:

```text
1. /planner/compile returns a valid compiled plan for a shallow pit.
2. the bridge can execute a validated compiled plan through the same receipt loop used by catalog behaviors.
```

That means the hard boundary is:

```text
after CompiledPlanExecutor v0
before storage/upkeep
```

At that point, the first LLM test is:

```text
Given "Dig a 5x5 pit 1 block deep," can the LLM produce valid TurtleQuest IR equivalent to or better than the deterministic baseline?
```

Current status: `CompiledPlanExecutor v0` is wired for valid deterministic compiled plans. The first local execution smoke is:

```text
/tq ask nearest Dig a 5x5 pit 1 block deep.
```

If that completes with receipt-backed `inspectDown`, `digDown`, movement, and `completeObjective`, the next slice is LLM-backed TurtleQuest IR generation.

The second LLM test is:

```text
Given "Dig a 5x5 pit 2 blocks deep and return," can the LLM ask for or use bounded assumptions, compile legal IR, and preserve a receipt-backed return plan?
```

## Validation Rules

Compiled plans should eventually be rejected unless they meet rules like:

```text
only legal primitive actions
bounded command count
bounded area
required parameters present
no unsupported movement macros
no unbounded while loops
receipt expectation for completion
```

This keeps TurtleQuest from becoming either a fully scripted demo or an unbounded world-command system.
