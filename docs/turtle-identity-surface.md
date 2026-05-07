# Turtle Identity Surface

TurtleQuest needs a stable human-facing identity for each turtle so player chat, bridge traces, receipts, and future route/storage records can be tied back to the correct actor.

## Current Slice

The mod supports:

```text
/tq name nearest <name>
```

This records an in-memory TurtleQuest display name for the nearest CC:T turtle-like block. Progress chat uses the label:

```text
[TurtleQuest] Miner One: Tunneling forward and tracking inventory pressure.
```

The bridge request also includes `turtleName` as metadata. This is intentionally a TurtleQuest label, not yet a CC:T computer label.

## Identity Priority

Future identity resolution should prefer:

1. CC:T computer id and label, if exposed through a stable API.
2. TurtleQuest persistent metadata keyed by world and turtle/computer id.
3. TurtleQuest volatile position key for early smoke tests.

The current fallback key is:

```text
<dimension>:<x>,<y>,<z>
```

That is acceptable for the smoke harness, but it will not survive moving/replacing a turtle as a durable identity.

## Future Commands

Likely command/tool surface:

```text
/tq name nearest <name>
/tq identify nearest
turtlequest.identity.get
turtlequest.identity.set_label
```

If CC:T exposes a safe label setter, TurtleQuest should mirror the name into CC:T so the turtle remains identifiable outside our harness. Until then, the harness should avoid mutating CC:T internals.
