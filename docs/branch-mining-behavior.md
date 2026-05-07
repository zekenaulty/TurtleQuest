# Branch Mining Behavior Slice

Branch mining is the first route-aware mining macro. It gives Agentica a bounded mining tool that can create a main tunnel, carve repeated side branches, return to the starting point, and emit route evidence for later reuse.

## Implemented Surface

Behavior ids:

```text
turtlequest.branch_tunnel
turtlequest.branch_mine_pattern
```

Executable host primitives:

```text
markWaypoint
branchTunnel
branchMinePattern
```

Planner tools:

```text
turtlequest.behavior.branch_tunnel
turtlequest.behavior.branch_mine_pattern
```

The Agentica planner host exposes these as behavior tools. Agentica should choose them from natural-language goals such as:

```text
dig a main tunnel 9 blocks, then make two 6 block side branches and return here
make a branch mine with 4 side branches
dig a left branch 8 blocks and return to the branch origin
```

## IR Shape

Single branch:

```json
[
  { "action": "startBehavior", "arguments": {} },
  { "action": "markWaypoint", "arguments": { "name": "branch_origin" } },
  { "action": "emitStatus", "arguments": { "stage": "branch_started" } },
  { "action": "branchTunnel", "arguments": { "side": "left", "length": 6, "height": 2, "returnToOrigin": true } },
  { "action": "emitStatus", "arguments": { "stage": "branch_returned_to_origin" } },
  { "action": "completeObjective", "arguments": {} }
]
```

Branch pattern:

```json
[
  { "action": "startBehavior", "arguments": {} },
  { "action": "markWaypoint", "arguments": { "name": "branch_mine_start" } },
  { "action": "getInventory", "arguments": {} },
  { "action": "emitStatus", "arguments": { "stage": "branch_mine_started" } },
  { "action": "branchMinePattern", "arguments": { "mainLength": 9, "branchLength": 6, "branchCount": 2, "spacing": 3, "height": 2, "sidePattern": "alternating", "returnHome": true } },
  { "action": "getInventory", "arguments": {} },
  { "action": "emitStatus", "arguments": { "stage": "branch_mine_completed" } },
  { "action": "completeObjective", "arguments": {} }
]
```

## Host Responsibility

The host owns the repeated Minecraft mechanics:

```text
face branch direction
clear player-walkable tunnel cells
return to branch origin
restore main-route facing
record blocks removed and inventory pressure
stop on blocked movement or hazard evidence
```

Agentica owns:

```text
choosing branch_mine_pattern vs tunnel_line
choosing dimensions and counts
deciding whether to continue, recover, or stop from receipts
choosing future storage/deposit tools when inventory pressure appears
```

## Route Evidence

Successful live receipts feed bridge route memory:

```text
GET /routes
```

Route memory currently records:

```text
waypoints from markWaypoint receipts
route segments from tunnelLine, branchTunnel, and branchMinePattern receipts
route ids, parent route ids, start/end positions, bounding boxes, clearance, blocks removed
```

The route memory is included in planner context and visible through `turtlequest.get_context`, so later Agentica runs can reason over known routes.

## Smoke Commands

In-game after restarting the bridge/mod:

```text
/tq ask nearest dig a main tunnel 9 blocks, then make two 6 block side branches and return here
/tq ask nearest make a 6 block left branch and return to the branch origin
```

Local bridge smoke:

```text
POST /planner/generate execute=true
POST /runs/{runId}/simulate
GET /routes
```

Expected local smoke result:

```text
behavior = turtlequest.branch_mine_pattern
status = completed
receipts = 8
waypoints >= 1
routeSegments >= 1
```

## Limits

This is not a complete mining economy yet.

Not done:

```text
deposit to home storage
craft/place barrel
ore-specific mining policy
torch placement
fluid/lava handling beyond stop evidence
route persistence across bridge restarts
return_to_waypoint
descending stair mineshaft
```

Next durable value comes from making route memory persistent and adding storage/deposit requirements, not from making the branch pattern larger.
