# Tunnel Line Behavior

`turtlequest.tunnel_line` is the first mining-route primitive. It gives Agentica a behavior-level tool for a bounded forward tunnel while the Minecraft host owns the repeated dig/move execution.

## Intent

Use for prompts like:

```text
dig a 6 block long 2 high tunnel forward and return here
make a short mineshaft and come back
```

## Agentica Tool

```text
turtlequest.behavior.tunnel_line
```

Arguments:

```json
{
  "length": 6,
  "height": 2,
  "returnHome": true
}
```

## Flattened IR Shape

```text
startBehavior(turtlequest.tunnel_line)
emitStatus(planning)
getInventory()
emitStatus(tunneling)
tunnelLine(length, height, routeId)
emitStatus(inventory_pressure_check)
getInventory()
emitStatus(returning)
returnHome(mode=breadcrumbs)
emitStatus(tunnel_line_returned)
completeObjective()
```

## Receipt Contract

The host-owned `tunnelLine` primitive returns:

```text
routeId
requestedLength
height
stepsCompleted
blocksRemoved
start
current
facing
inventoryOccupiedSlots
inventoryFreeSlots
inventoryPressure
storageRequirement
inventoryDelta
```

`inventoryPressure` is a planning signal. It should not automatically discard items, but it gives Agentica evidence to choose `discardJunk`, storage, home-chest setup, or clean stop on later slices.

The receipt also includes:

```text
boundingBox
clearance
```

For default tunnels, `clearance=player_walkable` means the host attempted a one-block-wide, two-block-high route volume.

## Scope

V0 supports height 1 or 2. Larger tunnel profiles, stairs, branch mines, torch placement, deposit routing, and route-map persistence are separate mining behavior slices.
