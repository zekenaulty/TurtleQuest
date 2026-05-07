# Storage And Upkeep Skill Surface

Storage is not a hardcoded prerequisite. It is a capability requirement the agent can discover, clear, and then continue the original workflow.

The intended shape is:

```text
agent wants to run workflow X
  -> workflow requires durable storage
  -> agent checks known storage
  -> no home chest/barrel exists
  -> agent gathers/crafts/places storage
  -> agent records storage waypoint
  -> agent resumes workflow X
```

This only works if TurtleQuest exposes storage as a tool surface with clear requirements, blockers, and receipts.

## Core Nouns

```text
home chest
barrel
storage waypoint
deposit target
inventory threshold
bill of materials
crafting turtle
recipe
upkeep goal
resource blocker
workflow requirement
```

## Core Verbs

```text
inspect inventory
classify inventory
declare requirement
find storage
craft storage
place storage
mark storage
deposit
withdraw
top up
resume workflow
```

## Capability Reflection

The context surface should eventually include live storage state:

```json
{
  "storage": {
    "knownHomeStorage": null,
    "inventoryUsedSlots": 11,
    "inventoryFreeSlots": 5,
    "craftingAvailable": true,
    "knownRecipes": ["minecraft:chest", "minecraft:barrel"],
    "requirements": [
      {
        "id": "home_storage",
        "status": "missing",
        "canClearWith": ["turtlequest.bootstrap_home_storage"]
      }
    ]
  }
}
```

This is prompt/context engineering, not hidden automation. The agent sees a missing requirement and chooses a tool or behavior to clear it.

## Required Executor Primitives

Near-term missing primitives:

```text
craft
drop / dropUp / dropDown
suck / suckUp / suckDown
detect / detectUp / detectDown
compare / compareUp / compareDown
markWaypoint
```

Current usable primitives:

```text
selectSlot
getInventory
discardJunk
place / placeUp / placeDown
scanNearby
returnHome
emitStatus
completeObjective
```

Storage bootstrap needs `craft` before it is real with a crafting turtle. Without `craft`, the agent can still use storage blocks already in inventory.

Current executable slice:

```text
turtlequest.bootstrap_home_storage
turtlequest.deposit_inventory
placeStorage
depositInventory
drop / dropUp / dropDown
suck / suckUp / suckDown
craft
detect / detectUp / detectDown
```

`bootstrap_home_storage` can place a chest/barrel already in turtle inventory and records it into route memory as `home_storage`. Crafting missing storage is intentionally a later requirement-clearing behavior.

## Tier One Storage Skills

### `turtlequest.inspect_inventory`

Purpose:

```text
Return item stacks, free slots, selected slot, and coarse fullness threshold.
```

Receipts:

```text
inventoryBefore
freeSlots
occupiedSlots
selectedSlot
itemTags
thresholds
```

### `turtlequest.find_known_storage`

Purpose:

```text
Look up storage waypoints known to this turtle/world.
```

Receipts:

```text
knownStorage
nearestStorage
missingReason
```

### `turtlequest.place_storage`

Purpose:

```text
Place a chest/barrel from inventory and record it as a storage waypoint.
```

Inputs:

```text
storageItem: minecraft:chest | minecraft:barrel
placement: front | up | down
waypointName: home_storage
```

Receipts:

```text
storageWaypointId
storageBlock
position
facing
inventoryDelta
placeReceipt
```

### `turtlequest.craft_storage`

Purpose:

```text
Craft a chest or barrel from inventory using a crafting turtle.
```

Inputs:

```text
storageKind: chest | barrel
maxAttempts
```

Receipts:

```text
craftingAvailable
recipe
ingredientsBefore
craftedItem
inventoryDelta
missingIngredients
```

### `turtlequest.deposit_inventory`

Purpose:

```text
Deposit selected inventory into a known storage waypoint.
```

Inputs:

```text
storageWaypoint
includeTags
excludeSlots
keepFuel
returnToWorksite
```

Receipts:

```text
inventoryBefore
inventoryAfter
depositedItems
storagePosition
returnPosition
failedStacks
```

Current v0 behavior:

```text
Assumes storage is adjacent in the requested direction.
Deposits non-protected stacks.
Protects obvious tools and storage blocks.
Emits inventoryDelta from the CC:T drop operation.
```

### `turtlequest.discard_junk`

Purpose:

```text
Clear one low-value inventory stack through a magic trash can when inventory pressure would otherwise block collection.
```

Default discard priority:

```text
dirt
coarse_dirt
rooted_dirt
gravel
cobblestone
cobbled_deepslate
diorite
granite
andesite
tuff
calcite
sand
red_sand
sandstone
red_sandstone
netherrack
basalt
blackstone
```

Rules:

```text
only host allowlisted junk may be voided
void at most one stack per command
emit the exact item/count removed in inventoryDelta
never discard tools, ores, logs, crafted storage, fuel, food, or unknown items by default
```

Receipts:

```text
discardedItem
discardedCount
slot
inventoryDelta
no_junk_to_discard when nothing matched
```

## Tier Two Upkeep Skills

### `turtlequest.bootstrap_home_storage`

Purpose:

```text
Ensure the turtle has a durable local storage waypoint for long-running workflows.
```

Possible strategy chain:

```text
inspect_inventory
find_known_storage
if storage exists:
  mark/use it
else if chest/barrel exists in inventory:
  place_storage
else if crafting turtle and ingredients exist:
  craft_storage
  place_storage
else:
  request/gather required resource
```

Receipts:

```text
requirementId: home_storage
requirementStatus: satisfied | blocked
storageWaypoint
strategyUsed
missingResources
resumeRecommended
```

### `turtlequest.upkeep_inventory_capacity`

Purpose:

```text
Prevent long workflows from stalling on full inventory.
```

Possible strategy chain:

```text
inspect_inventory
if freeSlots below threshold:
  discard_junk when a valuable pickup is blocked and only allowlisted junk is available
  deposit_inventory
else:
  emitStatus(capacity_ok)
```

Receipts:

```text
freeSlotsBefore
freeSlotsAfter
depositedItems
blockedReason
```

## Agentic Resume Contract

When a requirement blocks a workflow, the final receipt should not pretend the original mission is complete. It should emit a continuation hint:

```json
{
  "workflow": "turtlequest.branch_mine",
  "blockedRequirement": "home_storage",
  "clearingBehavior": "turtlequest.bootstrap_home_storage",
  "requirementStatus": "satisfied",
  "resumeRecommended": true,
  "resumeContext": {
    "storageWaypoint": "home_storage",
    "priorGoal": "branch mine until inventory threshold"
  }
}
```

Agentica can then decide whether to resume the original workflow, clear the next blocker, or stop and report.

## Definition Of Done

Home storage is real when:

```text
the turtle can report no known storage
the agent can choose bootstrap_home_storage
the turtle can craft or place a chest/barrel
the storage position is recorded as a waypoint
deposit_inventory can move items into it
the original workflow can resume with storageWaypoint in context
```

First smoke target:

```text
/tq ask nearest create a home barrel if needed, record it, then report inventory capacity
```

Second smoke target:

```text
/tq ask nearest gather wood, create a home storage barrel, deposit extra logs, and return here
```

Current focused smoke targets:

```text
/tq ask nearest create a home barrel storage and record it
/tq ask nearest deposit inventory into the storage ahead
```
