# Mining Skill Surface

Mining is the first large TurtleQuest behavior family that needs route planning, recovery, storage, and long-running progress. The agent should not drive every block primitive directly. It should choose from mining skills with rich descriptors, then reason from receipts when a skill succeeds, blocks, or returns partial evidence.

Storage/upkeep has its own companion surface:

```text
docs/storage-and-upkeep-skill-surface.md
```

Mining behaviors should treat durable storage as a clearable requirement. If no home chest/barrel exists, Agentica should be able to choose `turtlequest.bootstrap_home_storage`, then resume the mining workflow after the storage waypoint is recorded.

## Dispatch Shape

```text
player prompt
  -> Agentica intent parse
  -> TurtleQuest capability/context projection
  -> Agentica chooses skill/tool calls stepwise
  -> bridge compiles skill calls to flattened TurtleQuest IR
  -> mod executor runs bounded host-owned procedures
  -> receipts + world fragments + route learnings feed the next step
```

Agentica owns intent, sequencing, recovery choice, and stopping criteria. TurtleQuest owns Minecraft legality, bounded procedure execution, route evidence, and deterministic receipts.

## Good Nouns

These are the concepts the agent should see in descriptors and receipts.

```text
route
waypoint
home
shaft
stair
landing
branch
gallery
room
deposit
chest
barrel
storage waypoint
requirement
blocker
resource target
hazard
budget
support
clearance
volume
vein signal
world fragment
route learning
```

## Good Verbs

These are the behavior verbs that should become skill/tool descriptors.

```text
mark
face
scan
probe
navigate
descend
ascend
tunnel
clear
excavate
brace
branch
deposit
withdraw
craft
bootstrap
resume
return
recover
record
validate
report
```

## Tier Zero Executor Primitives

These are command receipts, not planning abstractions.

```text
inspect / inspectUp / inspectDown
dig / digUp / digDown
moveForward / moveUp / moveDown
turnLeft / turnRight / face
place / placeUp / placeDown
selectSlot / getInventory
discardJunk
scanNearby
recoverToGround
returnHome
emitStatus
completeObjective
```

Near-term missing executor primitives for mining:

```text
drop / dropUp / dropDown
suck / suckUp / suckDown
craft
detect / detectUp / detectDown
compare / compareUp / compareDown
markWaypoint
captureWorldFragment
```

## Tier One Base Skills

These should be small, locally verifiable, and composable.

### `turtlequest.mark_waypoint`

Purpose:

```text
Bind a named route point to the current turtle/world/dimension/position/facing.
```

Inputs:

```text
name: home | shaft_entry | landing_N | branch_N | deposit_chest
scope: run | world | turtle
```

Receipts:

```text
waypointId
position
facing
dimension
routeId
```

### `turtlequest.navigate_relative`

Purpose:

```text
Move toward a bounded relative target without tunneling.
```

Inputs:

```text
dx
dy
dz
budget
stopAdjacent
```

Receipts:

```text
startPosition
finalPosition
remainingDelta
path
blockedStep
success
```

### `turtlequest.tunnel_line`

Purpose:

```text
Move forward through mineable material for a bounded length, clearing turtle-sized passage.
```

Inputs:

```text
length
height: 1 | 2 | 3
width: 1
returnHome: bool
hazardPolicy: stop_on_fluid
```

Receipts:

```text
affectedVolume
blocksRemoved
inventoryDelta
path
hazards
stoppedAt
```

### `turtlequest.clear_rectangle`

Purpose:

```text
Clear a bounded rectangular footprint or wall plane.
```

Inputs:

```text
width
heightOrLength
plane: floor | wall_forward | ceiling
materialPolicy: mineable_only
```

Receipts:

```text
expectedCells
visitedCells
clearedCells
blockedCells
coverage
inventoryDelta
```

### `turtlequest.descend_stair`

Purpose:

```text
Create a navigable descending stair route while preserving a path back up.
```

Inputs:

```text
targetY
maxSteps
stairStyle: straight | switchback | spiral
landingEvery
hazardPolicy: stop_on_fluid
```

Receipts:

```text
routeId
startY
finalY
landings
path
affectedVolume
hazards
returnable
```

### `turtlequest.build_branch_gallery`

Purpose:

```text
Create repeated branch tunnels from a main shaft or landing.
```

Inputs:

```text
branchCount
branchLength
spacing
sidePattern: left_right | left_only | right_only
height
returnToMainRoute
```

Receipts:

```text
routeId
branchesStarted
branchesCompleted
branchReceipts
inventoryDelta
hazards
```

Current executable slice:

```text
turtlequest.branch_tunnel
turtlequest.branch_mine_pattern
markWaypoint
branchTunnel
branchMinePattern
```

Detailed contract:

```text
docs/branch-mining-behavior.md
```

### `turtlequest.deposit_inventory`

Purpose:

```text
Return to a known storage waypoint and deposit selected inventory.
```

Inputs:

```text
storageWaypoint
includeTags
keepSlots
returnToWorksite
```

Receipts:

```text
inventoryBefore
inventoryAfter
depositedItems
storagePosition
returnPosition
```

### `turtlequest.ensure_home_storage`

Purpose:

```text
Ensure a usable storage waypoint exists before a long mining workflow starts.
```

Inputs:

```text
preferredStorage: minecraft:barrel | minecraft:chest
allowCrafting
allowPlacement
resumeWorkflowId
```

Receipts:

```text
requirementId: home_storage
status: satisfied | blocked
storageWaypoint
strategyUsed: found_existing | placed_from_inventory | crafted_and_placed | blocked_missing_materials
resumeRecommended
```

### `turtlequest.record_route_learning`

Purpose:

```text
Persist useful route facts from a successful mining action.
```

Inputs:

```text
routeId
routeKind
waypoints
passablePath
hazards
resourceSignals
```

Receipts:

```text
routeLearningId
knownWaypointsAdded
knownHazardsAdded
resourceSignalsAdded
```

## Tier Two Mining Macro Skills

These compose tier-one skills and should be exposed as Agentica behavior tools once their descriptors are stable.

### `turtlequest.create_mineshaft`

Purpose:

```text
Create a reusable access route from home to a target Y level.
```

Likely composition:

```text
mark_waypoint(home)
descend_stair(targetY, stairStyle, landingEvery)
mark_waypoint(shaft_bottom)
record_route_learning(routeKind=mineshaft)
returnHome()
```

Success surface:

```text
route exists
targetY reached or explicit blocked reason
route is returnable
waypoints recorded
```

### `turtlequest.branch_mine`

Purpose:

```text
Mine a controlled branch pattern from a known shaft or landing.
```

Likely composition:

```text
ensure_home_storage(preferredStorage=minecraft:barrel)
navigate_to_waypoint(shaft_bottom)
build_branch_gallery(branchCount, branchLength, spacing)
deposit_inventory(storageWaypoint)
record_route_learning(routeKind=branch_mine)
emit report
```

Success surface:

```text
branch count attempted/completed
blocks removed
inventory/resource deltas
hazards encountered
route remains returnable
```

### `turtlequest.mine_until_threshold`

Purpose:

```text
Continue mining while budget remains and inventory/storage thresholds allow work.
```

Likely composition:

```text
inspect inventory
ensure_home_storage if no storage waypoint exists and inventory pressure is expected
discard_junk if a valuable pickup is blocked by inventory pressure and default junk is present
choose branch/tunnel action
execute bounded mining skill
deposit when inventory threshold reached
stop on hazard/budget/goal threshold
report
```

Success surface:

```text
stop reason
resources gathered
distance mined
deposits completed
current known route
```

## Big Prompt Example

User prompt:

```text
descend to y 118, tunnel in a winding spiral staircase, connect floors by creating empty rooms and connected floor while mining, return resources to chest x,y,z, record route learnings, record scout signals
```

Agentica should decompose that into:

```text
1. establish home/deposit waypoint
2. create_mineshaft(targetY=118, stairStyle=spiral_or_switchback)
3. create_landing_or_room at major Y intervals
4. branch_mine or tunnel_line from each landing
5. ensure_home_storage if no storage waypoint exists
6. deposit_inventory when threshold is reached
7. record_route_learning for shaft, landings, branches, hazards, resource signals
8. emit completion or partial-completion report
```

The host should reject any single skill call that is too broad. The agent can still satisfy broad intent by chaining bounded skills and reading receipts.

## Routing Concerns

Mining must be route-first. Every destructive skill should know whether it is creating a useful route, extending a route, or just clearing a local volume.

Required routing evidence:

```text
routeId
parentRouteId
startWaypoint
endWaypoint
pathCells
returnable
blockedAt
hazards
```

The first reusable map can be simple:

```text
knownWaypoints: named positions
knownRoutes: ordered path cells between waypoints
knownHazards: positions and hazard types
knownResourceSignals: ore/log/fluid/chest sightings
```

This lets turtles reuse mineshafts, stairs, roads, and player-accessible paths without needing global pathfinding first.

## Room Candidates And Scout Mapping

Room planning should use route-attached candidates before carving:

```text
blueprint -> proposed bounding box -> bounded scan/ray validation -> accepted room record -> staged carve
```

A turtle can eventually map existing bases and rooms the same way: sample bounded volumes, infer possible room bounds, attach entrances to known routes, and record the result as `observed_existing` instead of `carved_by_turtle`.

Detailed contract:

```text
docs/room-candidate-and-scouting-surface.md
```

## Definition Of Done For Mining V0

Mining V0 is real when:

```text
Agentica can choose create_mineshaft or tunnel_line from descriptors
the host compiles it to bounded IR
the turtle changes world blocks through CC:T receipts
the run records a routeId and path cells
the run can declare and clear home_storage as a workflow requirement
the turtle can recover or stop cleanly after blocked movement/felling/digging
the final report includes route, mined volume, inventory delta, hazards, and current position
```

First smoke target:

```text
/tq ask nearest dig a 1 wide 2 high tunnel 6 blocks forward, record the route, and return here
```

Second smoke target:

```text
/tq ask nearest create a descending stair route down 6 blocks and return here
```

Current branch-mining smoke target:

```text
/tq ask nearest dig a main tunnel 9 blocks, then make two 6 block side branches and return here
```
