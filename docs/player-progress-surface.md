# Player Progress Surface

TurtleQuest needs two communication layers:

```text
trace/log surface: complete evidence for debugging and validation
player progress surface: compact user-facing status updates in chat/UI
```

The player progress surface is not model chain-of-thought. It should never expose private hidden reasoning. It should expose deliberate, receipt-backed operational status:

```text
Thinking...
Planning turtle workflow...
Starting turtlequest.harvest_tree.
Scanning nearby blocks.
Moving toward selected target.
Felling remembered tree trunk.
Returning by breadcrumbs.
Blocked during moveTowardRelative: Movement obstructed.
Requesting runtime replan from receipts...
Objective complete.
```

## Sources

Progress messages may come from:

```text
mod executor command lifecycle
bridge run lifecycle
Agentica planner tool events
LLM-visible plan/status artifacts
command receipts
explicit emitStatus steps
```

Only the first slice is implemented in-game: the NeoForge executor sends scoped progress messages to the player who started the run.

## Scoping

Progress chat must be source-player scoped:

```text
player sends /tq ask nearest ...
binding stores player UUID
executor progress is sent only to that player
other players do not receive the run chatter
```

## Flood Control

Progress should be sparse:

```text
force key transitions
throttle repetitive movement/scanning
deduplicate repeated messages
prefer stage messages over raw command spam
```

Current mod settings:

```text
TURTLEQUEST_CHAT_PROGRESS_ENABLED=true
TURTLEQUEST_CHAT_PROGRESS_MIN_INTERVAL_MS=2500
```

## Future UI

WAILA/Jade-style overlays or a custom HUD can mirror the same progress surface:

```text
turtle current behavior
current stage
known home/storage tags
route id
inventory pressure
last receipt summary
blocked reason
```

Metadata tags should remain authoritative in TurtleQuest state. Signs, overlays, and chat are display surfaces, not the source of truth.
