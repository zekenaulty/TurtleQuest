# Architecture

## Boundary

Minecraft state is authoritative. Agentica plans and requests bounded turtle commands. The NeoForge side validates and executes commands, then returns receipts. The bridge never lets a run infer success without a receipt.

```text
Player talks to turtle
        |
        v
NeoForge TurtleQuest mod
        |
        | HTTP/WebSocket, local only
        v
Agentica.TurtleQuest.Bridge
        |
        | temporary local connector
        v
Agentica local runner
```

## Swap Point

The bridge exposes a stable TurtleQuest contract and hides the current Agentica integration behind an adapter:

```text
IAgenticaRunClient
  LocalAgenticaRunClient
  ApiAgenticaRunClient
```

`LocalAgenticaRunClient` can use the current repo, CLI, or local runner. `ApiAgenticaRunClient` will replace it when `Agentica.API` stands up.

## First Runtime Flow

1. Player sends a chat/use message to a specific turtle.
2. The mod posts a `TurtleUserRequest` to the bridge.
3. The bridge creates a scoped run and binds it to a behavior from the TurtleQuest catalog.
4. The turtle executor polls for `NextTurtleCommand` slices for that behavior.
5. The mod executes legal commands only.
6. The mod posts `TurtleCommandReceipt`.
7. The bridge updates run state and eventually emits a `turtlequest.objective_completed` completion artifact.

## Behavior Catalog

TurtleQuest is behavior-first. Primitive commands exist so receipts can be audited, but Agentica should normally select bounded host-owned behaviors instead of rediscovering turtle programs from raw movement atoms.

Initial behavior:

```text
turtlequest.dig_line_return(length, returnHome)
```

The first implementation maps the prompt `Dig a straight tunnel 5 blocks forward and return.` to `turtlequest.dig_line_return` with `length=5`.

The behavior expands into primitive command slices for the executor:

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

This execution shape is currently loaded from `behaviors/turtlequest.dig_line_return.json`. The durable contract is the behavior run plus receipts. Later, a behavior may run as a Java-side state machine, a compiled Lua turtle program, or an Agentica IR program without changing the high-level run contract.

## In-Game Smoke Commands

The first prompt surface intentionally avoids replacing the CC:T turtle UI:

```text
/tq ask nearest <prompt>
/tq status <runId>
/tq simulate <runId>
```

`nearest` currently searches a 16-block radius for a block in the `computercraft` namespace with `turtle` in its id. The binding captures turtle id, position, facing, and dimension before posting the prompt to the bridge.

The mod starts a background executor after the bridge accepts a run. The executor polls `GET /runs/{runId}/next-command`, schedules world reads on the Minecraft server thread, and posts receipts to `POST /runs/{runId}/receipts`.

Implemented real executor actions:

```text
startBehavior
inspect
dig
moveForward
moveBackward
turnRight
returnHome
completeObjective
emitStatus
```

`dig`, `moveForward`, `moveBackward`, and `returnHome` call CC:T's own turtle command implementations against the bound `TurtleBlockEntity` access object. The mod does not move turtle block entities manually.

The first live return path is intentionally narrow for TQ-001: the bridge emits two `turnRight` commands, then one `moveForward` command for each expected straight-line return step. This keeps the turtle facing the direction it is moving. This is not general pathfinding. `returnHome` remains available as a host macro placeholder for later behaviors.

Fuel remains out of scope for v1. If CC:T reports fuel is needed and the turtle is empty, the executor adds one fuel unit before each movement attempt to preserve the solar turtle assumption.

Current execution caveat: `dig` requires an appropriate CC:T tool turtle. A normal turtle can bind and inspect, but dig will return a failed CC:T receipt.

See `behavior-slices.md` for the planned behavior catalog, activity loop, storage, upkeep, and quest-board slice order.
