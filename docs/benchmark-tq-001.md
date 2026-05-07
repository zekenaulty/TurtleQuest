# TQ-001: Dig Straight Tunnel And Return

## Prompt

```text
Dig a straight tunnel 5 blocks forward and return.
```

## Preconditions

- Fuel is disabled.
- Turtle has a known start position and orientation.
- Mobs and lava are out of scope for the first pass.
- Blocks ahead are mineable or receipts identify failure.

## Success Criteria

- Turtle records start position and orientation.
- Host maps the prompt to `turtlequest.dig_line_return` with `length=5`.
- Turtle executor records a `startBehavior` receipt.
- Turtle digs or moves forward exactly five steps.
- Turtle returns to the recorded start position.
- Final report includes behavior id, path, blocks mined, inventory delta, failed commands, and `success: true`.
- Completion is represented by `turtlequest.objective_completed`, not report prose.

## Command Budget

Initial cap: `64` commands.

## Smoke Stage

Before CC:T execution is wired, the bridge supports a simulation endpoint:

```text
POST /runs/{runId}/simulate
```

This proves prompt binding, behavior selection, receipt shaping, and completion evidence without claiming real Minecraft execution.
