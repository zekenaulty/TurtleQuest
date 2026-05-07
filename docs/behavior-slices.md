# Behavior Slices

This document captures the current TurtleQuest behavior direction so implementation can proceed in small testable slices.

## Core Split

TurtleQuest should not make the LLM drive raw turtle primitives for normal work. The LLM should choose scoped behavior-level goals, and the host should execute deterministic behavior trees or state machines against the authoritative Minecraft world.

```text
Agentica
  chooses goals, behavior parameters, recovery strategy

TurtleQuest behavior catalog
  owns reusable behavior trees, state machines, and work loops

Turtle executor
  executes primitive turtle operations and returns receipts

Minecraft / CC:Tweaked
  owns world truth, turtle inventory, movement, digging, placement
```

Primitive commands remain important because they are the auditable execution language. They are not the primary orchestration surface.

## Behavior Contract

A behavior spec should eventually define:

```text
id
kind
inputs with types and constraints
preconditions
tree or state-machine body
primitive actions used
receipt expectations
success criteria
failure modes
upkeep hooks
```

The first behavior is:

```text
turtlequest.dig_line_return(length, returnHome)
```

Current expansion:

```text
startBehavior
repeat length:
  inspect
  dig
  moveForward
turnRight
turnRight
repeat length:
  moveForward
completeObjective
```

This is intentionally narrow. It proves the player prompt to bridge to turtle execution to receipt to completion loop before we add richer planning.

## Behavior Tree Shape

The minimum useful behavior tree vocabulary is:

```text
sequence
selector
repeat
condition
action
```

Example future IR shape:

```yaml
id: turtlequest.dig_line_return
kind: behavior
params:
  length:
    type: int
    min: 1
    max: 64
tree:
  sequence:
    - action: mark_home
    - repeat:
        count: $length
        do:
          - action: inspect
            direction: forward
          - selector:
              - sequence:
                  - condition: block_forward.mineable
                  - action: dig
                    direction: forward
              - condition: block_forward.passable
          - action: move
            direction: forward
          - action: emit_status
    - action: return_home
    - action: complete_objective
```

For now, the bridge can keep hardcoded command expansion. The durable target is a catalog-backed behavior runtime.

## Work Loops

After `dig_line_return`, the next meaningful abstraction is a long-running activity loop:

```text
mine_loop
  refresh state
  evaluate upkeep goals
  if upkeep is blocking:
    run upkeep behavior
  else:
    advance primary objective
  emit progress receipt
```

This lets a turtle keep working without making Agentica micromanage every block.

Useful first work loops:

```text
mine_until_inventory_threshold(item, threshold)
dig_tunnel(width, height, length, returnHome)
clear_area(width, depth)
build_wall(width, height, material)
explore_with_return_budget(maxDistance)
```

## Upkeep Goals

Upkeep is a first-class goal category, not a generic error handler.

Examples:

```text
inventory_free_slots >= 4
torch_count >= 16
pickaxe_available == true
home_storage_reachable == true
return_budget_remaining >= safe_margin
```

The initial upkeep slice should be inventory management:

```text
if inventory fullness >= threshold:
  return_home
  deposit_inventory
  resume_or_complete
```

This creates the first real activity loop without introducing complex pathfinding.

## Storage

Storage should be represented explicitly.

```text
StorageSite
  id
  worldId
  position
  type: chest | barrel | turtle_inventory | depot
  accessFace
  knownItems
  capacity
```

The first storage slice can be metadata plus receipts. It does not need a general warehouse planner.

Initial behavior:

```text
register_home_storage(position)
return_home
deposit_inventory(storageId)
```

Expected receipt evidence:

```text
storageId
itemsDeposited
inventoryAfter
position
success
```

## Quest Board

The planned guild hall or quest board should be a structured benchmark contract, not just a prompt list.

Example:

```json
{
  "questId": "TQ-001",
  "title": "Dig straight tunnel and return",
  "assignee": "turtle-01",
  "objective": {
    "behavior": "turtlequest.dig_line_return",
    "args": { "length": 5, "returnHome": true }
  },
  "constraints": {
    "maxCommands": 100,
    "returnHomeRequired": true
  },
  "successCriteria": {
    "completionArtifact": "turtlequest.objective_completed",
    "finalPosition": "start",
    "distanceDug": 5
  }
}
```

This aligns TurtleQuest with the Agentica.CLI quest benchmark pattern:

```text
Board -> Scenario -> Session -> ToolCatalog -> AgenticaRunner -> Receipts -> OutcomeReporter
```

The bridge and NeoForge mod are the remote host executor behind that pattern.

## Slice Order

### Slice 1: Prove TQ-001 In Game

Goal:

```text
player prompt -> bridge behavior -> real turtle execution -> receipts -> completion artifact
```

Smoke command:

```text
/tq ask nearest Dig a straight tunnel 5 blocks forward and return.
```

Pass condition:

```text
turtle digs/moves five blocks
turtle returns to start
run completes with turtlequest.objective_completed
receipts show inspect/dig/forward/turn/return-forward/complete
```

If the current Java-side CC:T execution path is brittle, pivot to a Lua-side polling agent that runs inside the turtle and executes commands through native `turtle.*` APIs.

### Slice 2: Catalog-Backed `dig_line_return`

Move the hardcoded bridge expansion into a behavior catalog entry. Keep the runtime small and local.

Pass condition:

```text
same TQ-001 behavior
commands are produced from a behavior spec
receipts remain unchanged
```

### Slice 3: Live State Reflection

Status: partially started through planner preview endpoints.

Before live reflection grows, the bridge now exposes a plan preview boundary:

```text
POST /planner/preview
GET  /behaviors
POST /planner/compile
```

The preview classifies a request into:

```text
catalog-backed behavior
known but not executable behavior
unsupported request
missing required parameters
validated compiled IR
```

Current guardrail:

```text
"Dig a 5x5 pit."
  -> turtlequest.excavate_rectangular_pit
  -> missing depth
  -> not executable yet

"Dig a 5x5 pit 2 blocks deep and return."
  -> turtlequest.excavate_rectangular_pit
  -> width=5, length=5, depth=2
  -> deterministic compile blocked; reserved for first LLM-backed execution test

"Dig a straight tunnel 5 blocks forward and return."
  -> turtlequest.dig_line_return
  -> catalog executable
```

Hard boundary for LLM-backed planning:

```text
Start full LLM-backed planning immediately after CompiledPlanExecutor v0 can execute a validated compiled plan locally.
Do not wait for storage, upkeep, pathfinding, or quest board.
```

Expose a compact run-local turtle state:

```text
position
orientation
home position
inventory summary
selected slot
known storage
current behavior
last blocker
```

Pass condition:

```text
/tq status <runId> shows useful behavior and state reflection
```

### Slice 4: Storage v0

Add a home storage site and deposit behavior.

Pass condition:

```text
turtle can return home and deposit inventory into a known adjacent chest
receipt records items deposited
```

### Slice 5: Upkeep v0

Add inventory threshold handling to a mining loop.

Pass condition:

```text
turtle starts mining
inventory threshold triggers return/deposit
turtle resumes or completes with receipt-backed explanation
```

### Slice 6: Quest Board v0

Represent TQ scenarios as structured contracts and map them to behavior runs.

Pass condition:

```text
TQ-001 runs from a quest definition instead of only a prompt heuristic
outcome reporter validates success criteria from receipts
```

## Pathfinding Position

Do not start with general pathfinding.

The first return path is straight-line backtracking for TQ-001. The next useful navigation layer is waypoint breadcrumbs and return budgets, not global A*.

Pathfinding becomes necessary when behaviors require:

```text
nonlinear cave exploration
obstacle detours
multi-level routes
known storage away from the work line
recovery after displacement
```

Until then, prefer constrained behaviors that own their movement assumptions and report blockers clearly.
