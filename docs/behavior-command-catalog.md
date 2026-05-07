# Behavior Command Catalog

This translates the old MazeQuest shape vocabulary into TurtleQuest turtle terms. The goal is a small set of verifiable skills that compose into larger missions without forcing the LLM to micromanage every block.

## Source Lesson

The useful old pattern is:

```text
shape intent -> bounded coordinate volume -> repeated set/place operations -> optional cutouts/details -> final shape invariant
```

MazeQuest examples:

```text
line.plot
rectangle.flat
rectangle.flatHallow
rectangle.x_to_x
rectangle.z_to_z
floor
wallNorth/East/South/West
trim
roof
door cutout
pillar
book wall
light/detail pass
```

TurtleQuest should not copy that implementation. It should expose turtle-executable equivalents with receipts and world-diff validation.

## Primitive Executor Actions

These are the lowest-level command receipts. They are the bytecode of TurtleQuest.

Already started:

```text
startBehavior
inspect
inspectUp
inspectDown
moveForward
moveBackward
turnLeft
turnRight
dig
digUp
digDown
returnHome
emitStatus
completeObjective
```

Tier-one missing executor actions:

```text
moveUp
moveDown
face
moveTowardRelative
digRememberedTarget
place
placeUp
placeDown
selectSlot
getInventory
scanNearby
```

Tier-one still missing:

```text
compare
compareUp
compareDown
```

Recently added executor actions:

```text
drop / dropUp / dropDown
suck / suckUp / suckDown
craft
detect / detectUp / detectDown
markWaypoint
returnToPosition
placeStorage
depositInventory
```

The critical remaining inventory/storage gap is no longer adjacent transfer. It is route-aware storage use: return to a known storage waypoint, deposit, and resume the prior worksite.

## Tier-One Base Skills

These should be exposed as behavior/tool commands because they are small, useful, and locally verifiable.

### Navigation

```text
face(direction)
move_line(length)
move_to_relative(dx, dy, dz)
return_by_breadcrumbs()
mark_home()
return_home()
```

Receipts:

```text
startPosition
finalPosition
startFacing
finalFacing
path
blockedStep
success
```

### Local Sensing

```text
inspect_cell(direction)
scan_column(height)
scan_footprint(width, length)
scan_volume(width, height, length)
check_clearance(width, height, length)
```

Receipts:

```text
sampledCoordinates
blockCounts
hazards
blockedCells
passableCells
```

### Digging

```text
dig_cell(direction)
dig_line(length, direction=forward)
dig_column(depthOrHeight, direction=down|up)
clear_floor(width, length)
clear_wall(width, height)
clear_volume(width, height, length)
excavate_rectangular_pit(width, length, depth)
dig_tunnel(width, height, length)
```

Receipts:

```text
affectedVolume
blocksRemoved
inventoryDelta
unmineableBlocks
hazards
coverage
```

### Placement

```text
place_cell(direction, material)
place_line(length, material)
place_column(height, material)
place_floor(width, length, material)
place_wall(width, height, material)
place_hollow_rectangle(width, length, material)
place_ceiling(width, length, material)
```

Receipts:

```text
affectedVolume
blocksPlaced
materialsConsumed
failedPlacements
coverage
```

### Inventory And Storage

```text
select_material(material)
inventory_summary()
ensure_material(material, count)
discard_junk(defaultAllowlist)
craft_item(item, count)
place_storage(storageKind, waypointName)
bootstrap_home_storage(preferredStorage)
return_and_deposit(storageId)
withdraw_material(storageId, material, count)
top_up_materials(recipeOrBillOfMaterials)
```

Receipts:

```text
inventoryBefore
inventoryAfter
storageId
itemsMoved
missingMaterials
requirementStatus
resumeRecommended
discardedItem
```

### Status And Evidence

```text
emit_progress(stage, completed, total)
capture_world_fragment(radiusOrVolume)
validate_shape_invariant(invariantId, expectedVolume)
emit_completion_report()
```

Receipts:

```text
stage
progress
worldFragmentId
validationResult
evidenceRefs
```

## Tier-Two Shape Skills

These are the Minecraft turtle equivalents of MazeQuest shape helpers.

```text
draw_line_3d(from, to, material)
fill_plane(axis, width, heightOrDepth, material)
outline_plane(axis, width, heightOrDepth, material)
fill_box(width, height, length, material)
hollow_box(width, height, length, material)
cut_opening(face, offset, width, height)
place_trim(faceOrEdgeSet, material)
place_light(positionOrPattern)
```

Implementation note: these should compile to tier-one movement, placement, and digging skills. They are not primitive executor actions.

## Tier-Three Mission Behaviors

### Find Nearby Tree

Stages:

```text
1. mark current scoped turtle position implicitly through startBehavior
2. scanNearby(radius=12, tag=minecraft:logs)
3. emit scan completion status
4. complete with nearest log evidence
```

Definition of done:

```text
scanNearby receipt exists
receipt reports radius, query, match count, and nearest relative candidates
no movement or digging occurs
completion artifact emitted
```

This is the first resource-fetch slice. It exists to prove bounded perception before pathing and harvesting.

### Harvest Tree V0

Stages:

```text
1. scanNearby(radius=12, tag=minecraft:logs)
2. remember nearest candidate in the turtle executor binding
3. moveTowardRelative(source=lastScanNearest, stopAdjacent=true, budget=12)
4. digRememberedTarget(source=lastScanNearest)
5. emit single-log cut status
6. complete with scan, approach, and cut evidence
```

Definition of done:

```text
scanNearby receipt exists
moveTowardRelative receipt exists
digRememberedTarget receipt exists
only one remembered log is cut
completion artifact emitted on successful cut
```

### Dig 5x5 Pit

Stages:

```text
1. mark_home
2. scan_footprint(width=5, length=5)
3. excavate_rectangular_pit(width=5, length=5, depth=1)
4. validate pit footprint changed/passable
5. emit completion report
```

Definition of done:

```text
25 expected cells visited
25 digDown successes or explicit already-air/passable receipts
world diff covers expected footprint
no unsupported primitive action
completion artifact emitted
```

### Build Tower

Stages:

```text
1. choose footprint and height
2. check_clearance
3. ensure_material
4. inspect clearance for each vertical step
5. place_column or hollow_box shell
6. optionally add ladder/stair/light
7. validate height and footprint
8. emit completion report
```

Good first tower:

```text
build_column(height=8, material=selected)
```

The first column pattern is deliberately collision-aware:

```text
repeat height:
  inspectUp
  moveUp
  placeDown
```

If `moveUp` fails, the failed receipt and prior `inspectUp` become the replan boundary.

Then:

```text
build_tower(width=3, length=3, height=8)
```

### Build House

Stages:

```text
1. choose bounded footprint
2. clear_footprint
3. place_floor
4. build four walls
5. cut or preserve door opening
6. place_ceiling_or_roof
7. place_lights
8. validate footprint, walls, entry, roof coverage
9. emit completion report
```

House should not be the first live complex behavior. The first useful subset is:

```text
build_shelter_box(width=5, length=5, height=4, door=true, roof=flat)
```

### Find Diamonds

Stages:

```text
1. reflect starting state and constraints
2. choose search budget and mining pattern
3. ensure return path and storage policy
4. descend or start at suitable depth if already there
5. run bounded branch_mine_pattern
6. hazard checks before each forward/side dig
7. deposit or report inventory
8. return or emit final known position
```

First deterministic subset:

```text
branch_mine_pattern(mainLength=16, branchLength=4, spacing=3)
```

The LLM chooses strategy and budget. The host executes and validates the pattern.

## Exposed Agentica Tool Commands

Expose these first as Agentica-visible capabilities:

```text
turtlequest.assist
turtlequest.dig_line_return
turtlequest.excavate_rectangular_pit
turtlequest.build_column
turtlequest.place_wall
turtlequest.clear_volume
turtlequest.scan_volume
turtlequest.return_and_deposit
turtlequest.validate_shape
```

Then add:

```text
turtlequest.build_tower
turtlequest.build_shelter_box
turtlequest.branch_mine_pattern
turtlequest.find_diamonds
```

Do not expose `build_house` as a freeform one-shot until `build_shelter_box`, roof/door invariants, and material upkeep work.

## Status Update Pattern

Every long behavior should emit stage receipts:

```text
planned
preflight
materials
navigation
work_started
work_progress
upkeep
validation
completed
blocked
```

Example:

```json
{
  "action": "emitStatus",
  "arguments": {
    "stage": "work_progress",
    "behaviorId": "turtlequest.excavate_rectangular_pit",
    "completedCells": 12,
    "totalCells": 25
  }
}
```

## Implementation Order

Tier-one implementation should move in this order:

```text
1. face(direction)
2. place/placeUp/placeDown [started]
3. selectSlot/getInventory [started]
4. mark_home + return_by_breadcrumbs
5. scan_footprint + scan_volume
6. validate_shape_invariant for pit and wall
7. build_column [started]
8. place_wall
9. clear_volume
10. return_and_deposit
```

Current storage/upkeep slice:

```text
bootstrap_home_storage places a chest/barrel already in inventory
deposit_inventory drops non-protected inventory into adjacent storage
route memory records storage waypoints
```

This gets us to interesting behavior quickly:

```text
dig pit -> validate footprint
build column -> validate height
build wall -> validate plane
clear room -> validate volume
deposit inventory -> validate storage transfer
```

After that, LLM-backed composition has enough reliable pieces to attempt:

```text
build tower
build simple shelter
branch mine
find diamonds with bounded search budget
```
