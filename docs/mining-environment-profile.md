# Mining Environment Profile

TurtleQuest needs baseline Minecraft constraints in the planner context so Agentica is choosing from explicit environment facts instead of hidden assumptions.

## Current Defaults

```text
minimumPlayerTunnelHeight = 2
defaultTunnelHeight = 2
defaultTunnelWidth = 1
defaultRoomWidth = 9
defaultRoomLength = 9
defaultRoomHeight = 9
defaultTorchSpacing = 8
chunkSize = 16
```

The bridge now exposes these as `environmentProfile` in planner context. The Agentica planner host forwards that context through `turtlequest.get_context`.

## Rules

Player-traversable mining routes must be at least two blocks high.

Default utility rooms use a Dire-style nine by nine by nine envelope unless the user asks otherwise.

Nine by nine by nine is treated as chunk-aligned good enough for the early harness. We should still record route and room bounding boxes so later validators can check real placement against chunk boundaries.

Chunk alignment is a planning preference for durable route networks, not a hard requirement for small safe actions.

Light placement is a known planning concern, but it should not be emitted until a light-placement primitive exists.

## Blueprint Vocabulary

Current blueprint defaults are intentionally descriptive:

```text
tunnel_line
  route, 1 wide, 2 high, player walkable

turtlequest.blueprint.dire_room
  room, 9 wide, 9 long, 9 high
  general mining-base room envelope that chunk-aligns good enough

storage_room
  room, 9 wide, 9 long, 9 high
  intended for home chest/barrel array/deposit target

crafting_room
  room, 9 wide, 9 long, 9 high
  intended for crafting table/furnace/turtle staging
```

These are not yet executable room builders. They are the first shared noun surface for Agentica to reason about. Executable room carving should arrive as host-owned behaviors like `clear_box`, `carve_room`, and `connect_route`.

Room candidate planning and scout mapping are described in:

```text
docs/room-candidate-and-scouting-surface.md
```

## Tunnel Bounding Boxes

`tunnelLine` receipts now include a bounding box:

```text
boundingBox=x1,y1,z1->x2,y2,z2
clearance=player_walkable
```

This gives later route storage, world-diff validation, and mining-road planning a compact affected-volume signal.
