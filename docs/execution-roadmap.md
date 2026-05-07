# Execution Roadmap

This is the current TurtleQuest story shape and gap-closing plan.

## North Star

TurtleQuest should become an embodied Agentica harness where:

```text
user goal
  -> Agentica compiles TurtleQuest IR
  -> host validates legal bounded actions
  -> turtle executes primitives
  -> receipts and world diffs become evidence
  -> deterministic validators close known contracts
  -> LLM/human judge handles semantic ambiguity later
```

The behavior catalog is not a permanent hardcoded menu. It is a cookbook of known-good examples and reusable procedures that the LLM can retrieve from and compose beyond.

## Done So Far

### Foundation

Status: done.

Definition of done:

```text
NeoForge client launches with CC:Tweaked.
Bridge starts locally.
Mod can bind nearest turtle.
Turtle command receipts reach bridge.
```

### TQ-001: Dig Line Return

Status: done.

Definition of done:

```text
/tq ask nearest Dig a straight tunnel 5 blocks forward and return.
turtle digs 5 blocks
turtle turns around
turtle moves forward back to start
run completes with receipt trail
```

### Catalog-Backed Behavior

Status: done.

Definition of done:

```text
dig_line_return loads from behaviors/turtlequest.dig_line_return.json
bridge expands catalog steps into primitive commands
same TQ-001 smoke still passes
```

### Compiled Plan v0

Status: done.

Definition of done:

```text
/planner/compile can produce valid TurtleQuest IR
5x5 shallow pit compiles to legal primitive actions
compiled plan executes through same receipt queue as catalog behavior
```

### Board And Session

Status: done.

Definition of done:

```text
GET /boards lists board definitions
POST /sessions starts a quest-backed run
GET /sessions/{id} returns session + run state
```

### Snapshot And Diff

Status: done.

Definition of done:

```text
/tq snapshot nearest <x> <y> <z>
/tq diff <before> <after>
bridge stores world fragments
bridge diffs changed block states
```

### Deterministic Evaluation

Status: done.

Definition of done:

```text
POST /sessions/{id}/evaluate
checks completion artifact
checks receipt counts
checks final position when required
checks changed-to-air block counts from diff
returns evidence package
```

## Current Slice: IR Cookbook

Status: done for the first LLM-boundary cookbook.

Purpose:

```text
Create enough examples that Agentica can compile novel goals from patterns instead of matching one prompt to one behavior.
```

Definition of done:

```text
examples/cookbook contains at least 6 reusable IR examples
each example has goal, assumptions, IR, expected receipts, expected diff/invariant
bridge exposes cookbook listing or docs describe retrieval shape
```

Initial examples:

```text
assist
dig_line_return
excavate_rectangular_pit
return_by_breadcrumbs
build_column
build_wall
```

Current note:

```text
build_column and build_wall are cookbook examples, not executable plans yet.
The validator rejects their placement/upward movement primitives until the mod supports them.
```

## Low-Hanging Deterministic IR Test: Assist

Status: implemented as first diagnostic static behavior.

Command:

```text
/tq ask nearest Assist me.
```

Definition of done:

```text
behaviorId = turtlequest.assist
executes startBehavior, inspect, inspectDown, emitStatus, completeObjective
does not mutate world
completes from inspection receipts
```

Why this matters:

```text
It proves a non-mutating behavior path.
It gives the LLM a safe "look around and report" primitive.
It is a good fallback before promoting to LLM planning.
```

## Follow

Status: known intent, not executable.

Definition of done before execution:

```text
target tracking exists
movement policy exists
max distance and stop distance are explicit
blocked path returns receipts instead of improvising
follow can be cancelled
```

Reason:

```text
Follow sounds simple but needs live target state and path safety. It should promote to LLM or host pathing only after mobility policy exists.
```

## LLM Boundary

Start LLM-backed planning as soon as this is true:

```text
1. deterministic compiled IR can execute locally
2. IR validator rejects illegal/unbounded plans
3. evaluator can produce an evidence package
4. at least 6 cookbook examples exist
```

Do not wait for:

```text
storage
upkeep
pathfinding
house building
diamond mining
multi-turtle coordination
```

First LLM test:

```text
Given "Dig a 5x5 pit 1 block deep,"
can the LLM compile valid IR equivalent to the deterministic baseline?
```

Second LLM test:

```text
Given "Dig a 5x5 pit 2 blocks deep and return,"
can the LLM compose a bounded plan or ask for missing assumptions?
```

## Next Targets

### Target 1: IR Cookbook

Definition of done:

```text
examples exist for assist, dig line, pit, return, column, wall
examples include expected receipts and invariants
```

### Target 2: IR Validator Hardening

Status: done for the first execution gate.

Definition of done:

```text
validator rejects unknown actions
validator rejects over-budget plans
validator rejects plans without completeObjective
validator rejects unbounded repeat/while constructs
validator surfaces clear errors for Agentica repair
```

Current endpoint:

```text
POST /planner/validate
```

Current validator checks:

```text
supported executable primitive actions only
positive command budget
command count within budget
first step is startBehavior
exactly one completeObjective
completeObjective is final step
```

Unbounded repeat/while constructs are rejected at this boundary because executable IR must arrive as a flattened primitive step list. Catalog examples may still show repeat notation as authoring shorthand.

### Target 3: Agentica Planner Hook

Status: in progress.

Definition of done:

```text
bridge can call local Agentica planner adapter or CLI path
planner receives goal, state, primitive schema, cookbook examples
planner returns TurtleQuest IR
IR validates
IR can be executed or rejected with repair feedback
```

Done in this slice:

```text
POST /planner/context packages goal, matched behavior, supported primitives, cookbook, and execution rules
POST /planner/generate supports deterministic and mock-llm planner modes
POST /planner/generate can enqueue a valid generated plan when execute = true
POST /planner/generate mode = agentica calls a bridge-side subprocess adapter when configured
POST /runs/from-plan accepts a validated flattened TurtleQuest plan
invalid plans are rejected with validator errors and supported primitives
accepted plans enter the same run queue as catalog and deterministic plans
```

Remaining before LLM-backed execution:

```text
an Agentica planner command exists and accepts TurtleAgenticaPlannerCommandRequest JSON on stdin
the command returns TurtleCompiledPlan JSON on stdout
the bridge adapter is configured with AGENTICA_TURTLEQUEST_PLANNER_COMMAND
```

Future cookbook growth loop:

```text
classify successful run
refine into reusable behavior shape
decorate with assumptions, invariants, and failure modes
store as gated cookbook candidate
promote only after deterministic validation or human review
```

### Target 4: Construction Primitives

Definition of done:

```text
build_column executable
build_wall executable
world diff verifies placed block footprint
```

### Target 5: Resource/Storage Loop

Definition of done:

```text
known home storage site exists
depositInventory behavior exists
mine loop can pause for inventory upkeep
evaluator verifies deposit receipts and storage diff
```

### Target 5.25: Branch Mining V0

Status: implemented for bounded horizontal branch mining.

Definition of done:

```text
Agentica can choose turtlequest.behavior.branch_mine_pattern from an open mining prompt
bridge compiles branch_mine_pattern to flattened TurtleQuest IR
mod executes branchMinePattern as one host-owned procedure
receipt trail includes waypoint, inventory checks, route evidence, blocks removed, and completion
bridge route memory records branch mining waypoints and route segments
route memory is visible to future Agentica planning context
```

Current smoke:

```text
/tq ask nearest dig a main tunnel 9 blocks, then make two 6 block side branches and return here
```

Remaining:

```text
route persistence across bridge restarts
return_to_waypoint
storage/deposit loop
descending stair/mineshaft route
torch placement and hazard policy
```

### Target 5.5: Room Candidate Planning

Definition of done:

```text
Agentica can choose propose_room_box from blueprint/context
host returns proposed_only room candidate with 9x9x9 bounds and entry point
candidate attaches to current route or turtle anchor
no world mutation occurs
candidate can later feed validate_room_box and staged carving
```

### Target 6: Mission Behaviors

Definition of done:

```text
find_diamonds can compile a bounded search plan
build_tower can compile from column/wall primitives
build_house can compile a small constrained footprint with entry and roof invariants
```
