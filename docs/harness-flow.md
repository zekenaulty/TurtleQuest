# Harness Flow

TurtleQuest is moving toward the same shape as the Agentica quest benchmarks:

```text
Board -> Quest -> Session -> Plan -> Commands -> Receipts -> Snapshot Diff -> Outcome
```

## Board

A board is a static set of benchmark-like quests.

Current board:

```text
boards/turtlequest.smoke.json
```

Current bridge endpoints:

```text
GET /boards
GET /boards/{boardId}
```

## Quest

A quest defines:

```text
questId
title
prompt
expected behavior id
arguments
success criteria
```

The prompt is still used as the bridge input because this keeps the same player-message path alive. The structured fields are there for validation, reporting, and later Agentica.CLI integration.

## Session

A session binds a quest to a specific turtle request and run.

Current bridge endpoints:

```text
POST /sessions
GET  /sessions/{sessionId}
POST /sessions/{sessionId}/evaluate
```

Session creation starts a normal TurtleQuest run from the quest prompt. That means sessions use the same command queue and receipts as `/turtles/{id}/messages`.

Example request:

```json
{
  "boardId": "turtlequest.smoke",
  "questId": "TQ-001",
  "request": {
    "turtleId": "turtle@0,64,0",
    "worldId": "minecraft:overworld",
    "playerId": "manual",
    "message": "",
    "position": { "x": 0, "y": 64, "z": 0 },
    "orientation": "north"
  }
}
```

The bridge replaces the empty message with the quest prompt.

## Snapshot Diff

The first snapshot/diff layer is now present.

Current bridge endpoints:

```text
POST /snapshots
GET  /snapshots/{snapshotId}
POST /snapshots/diff
```

Current in-game commands:

```text
/tq snapshot nearest <x> <y> <z>
/tq diff <beforeSnapshotId> <afterSnapshotId>
```

The snapshot volume is centered around the nearest turtle on X/Z and extends downward from the turtle's current Y. For example, `7 3 7` captures the turtle layer plus two layers below.

Smoke flow:

```text
/tq snapshot nearest <x> <y> <z>
/tq ask nearest Dig a 5x5 pit 1 block deep.
/tq snapshot nearest <x> <y> <z>
/tq diff <beforeSnapshotId> <afterSnapshotId>
```

The snapshot and diff layer belongs in the host/test harness because Minecraft is authoritative. Agentica should consume the diff result, not invent it.

## Outcome

The first deterministic outcome evaluator is now present.

Current endpoint:

```text
POST /sessions/{sessionId}/evaluate
```

Optional request body:

```json
{
  "beforeSnapshotId": "tqsnap-before",
  "afterSnapshotId": "tqsnap-after"
}
```

The evaluator combines:

```text
quest success criteria
run completion artifact
receipt trail
world diff
```

Current deterministic checks:

```text
completion success
completion artifact kind
minimum successful receipt counts
final position equals start
expected changed footprint blocks changed to air
```

## Validation Tiers

Snapshots and diffs are not the first source of truth. They are an evidence stream.

```text
Tier 1: deterministic command receipts
  position, facing, command result, block samples, inventory delta later

Tier 2: task-specific invariants
  rectangular prism cleared, turtle returned, expected receipt counts

Tier 3: world fragment diff
  changed coordinates, changed-to-air count, unexpected mutations later

Tier 4: semantic judge later
  given goal, plan, receipts, diff, and projections, decide ambiguous intent satisfaction
```

The goal is not one magical validator. It is a chain of locally verifiable transitions that compose into larger behavior evidence.
