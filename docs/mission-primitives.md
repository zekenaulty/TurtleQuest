# Mission Primitives

This document captures larger TurtleQuest missions and the primitive/action-set surfaces they should compile into.

The MazeQuest lesson is useful but should not be copied wholesale. The reusable idea is:

```text
metadata/query surface -> plan/command chain -> validation -> receipts -> evidence package
```

For TurtleQuest, the equivalent is:

```text
turtle state + world fragment + behavior catalog -> TurtleQuest IR -> primitive turtle actions -> receipts + world diff
```

## Mission Classes

Mining now has its own skill-surface plan:

```text
docs/mining-skill-surface.md
```

That document defines the route-first nouns, verbs, base skills, macro skills, and definitions of done for mineshaft and branch-mining behavior work.

### Find Nearby Tree

Behavior id:

```text
turtlequest.find_tree
```

This is the first resource-fetch slice. It does not harvest yet; it creates a bounded evidence receipt for nearby log-like blocks.

Useful primitive/action sets:

```text
scanNearby
emit_report
```

Hard deterministic checks:

```text
scanNearby receipt exists
match count and nearest candidates are reported
no movement or digging occurs
completion receipt is emitted
```

This feeds the next pathing and treecapitator behaviors.

### Find Diamonds

Behavior id:

```text
turtlequest.find_diamonds
```

Agentica should know enough Minecraft context to propose a plan, but the host should provide examples and constraints.

Useful primitive/action sets:

```text
inspect/scan
dig_line_return
dig_stair_down
branch_mine_pattern
safe_return
deposit_inventory
hazard_check
emit_report
```

Hard deterministic checks:

```text
bounded command count
returned or reported final position
receipt-backed scanned/mined coordinates
inventory delta or explicit report if no diamonds found
no lava/fluid hazard entered
```

LLM-backed planning starts here once compiled-plan execution is stable. The LLM can choose Y-level strategy, branch pattern, search budget, and stop criteria.

### Build Tower

Behavior id:

```text
turtlequest.build_tower
```

This is a good early construction mission because the invariant is simple.

Useful primitive/action sets:

```text
select_slot
place_column
build_floor_ring
spiral_stair_or_ladder_column
return_to_base
emit_report
```

Hard deterministic checks:

```text
footprint exists
height >= requested/minimum
placed block count meets threshold
world diff mostly within expected column/footprint
```

### Build House

Behavior id:

```text
turtlequest.build_house
```

This is intentionally later. A house has semantic ambiguity: shape, materials, entry, roof, interior, and "looks good" concerns.

Useful primitive/action sets:

```text
clear_footprint
build_wall_rectangle
leave_door_opening
place_floor
place_roof_or_ceiling
place_lights
emit_report
```

Hard deterministic checks:

```text
bounded footprint
walls present on perimeter
entry opening present
ceiling/roof coverage exists
no unexpected edits outside footprint margin
```

Semantic validation is likely required later for "good house" quality.

## Current Code Boundary

The bridge now recognizes these as known intents:

```text
turtlequest.find_tree
turtlequest.find_diamonds
turtlequest.build_tower
turtlequest.build_house
```

They are not executable yet. They are marked as mission-class behaviors requiring LLM planning instead of falling into generic unsupported text.

This prevents accidental behavior mapping while preserving the intended direction:

```text
LLM compiles larger missions into TurtleQuest IR.
Host validates legal primitive actions and bounds.
Executor returns receipts.
World diff and deterministic invariant checks provide evidence.
```
