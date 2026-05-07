# Smoke Test

## Bridge-Only

Start the bridge:

```powershell
dotnet run --project ./bridge/Agentica.TurtleQuest.Bridge
```

Create a run:

```powershell
$body = @{
  turtleId = "nearest"
  worldId = "minecraft:overworld"
  playerId = "smoke-player"
  message = "Dig a straight tunnel 5 blocks forward and return."
  position = @{ x = 0; y = 64; z = 0 }
  orientation = "north"
} | ConvertTo-Json -Depth 5

$run = Invoke-RestMethod `
  -Uri "http://127.0.0.1:57421/turtles/nearest/messages" `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Simulate completion:

```powershell
Invoke-RestMethod `
  -Uri "http://127.0.0.1:57421/runs/$($run.runId)/simulate" `
  -Method Post
```

Expected result:

- `behaviorId` is `turtlequest.dig_line_return`
- `status` is `completed`
- `completion.artifactKind` is `turtlequest.objective_completed`
- `completion.success` is `true`

## In Game

Start the bridge, then start the NeoForge client:

```powershell
dotnet run --project ./bridge/Agentica.TurtleQuest.Bridge
cd ./mod/agentica-turtlequest-neoforge
./gradlew.bat runClient
```

Or use the combined launcher from the repo root:

```powershell
./scripts/start-game.ps1
```

In Minecraft:

```text
/tq kit
/tq ask nearest Dig a straight tunnel 5 blocks forward and return.
/tq simulate <runId>
/tq status <runId>
```

`nearest` searches for a CC:T turtle-like block within 16 blocks. The mod binds the accepted run to that turtle and starts a background executor.

The mod grants a small dev kit on first login for the current dev session. `/tq kit` grants it again. The kit includes CC:T turtle items if their registry ids are present, plus a few vanilla supplies for quick setup.

Current real executor behavior:

- `startBehavior` posts a real receipt.
- `inspect` reads the block ahead on the server thread and posts a real receipt.
- `dig` runs CC:T's turtle tool command.
- `moveForward` runs CC:T's turtle move command.
- `turnRight` runs CC:T's turtle turn command.
- TQ-001 now returns by turning around and moving forward back to start.
- `returnHome` remains available as a host macro placeholder.
- `completeObjective` succeeds only when the turtle returned to its start position after moving.

Use a tool turtle facing a solid, mineable five-block line for the first real execution smoke. A normal turtle can bind and inspect, but `dig` should fail with a CC:T command receipt because it has no digging tool.

Use `/tq simulate <runId>` only when you want bridge-side simulated completion without moving a real turtle.

Compiled-plan smoke after TQ-001:

```text
/tq ask nearest Dig a 5x5 pit 1 block deep.
```

Expected result:

- bridge compiles `turtlequest.excavate_rectangular_pit`
- executor receives `inspectDown`, `digDown`, turns, and movement commands
- final receipt completes the pit objective after receipt-backed `digDown` steps

This is intentionally shallow. Deeper pits and return-home pit plans are the first LLM-backed planning boundary.

Optional snapshot/diff smoke:

```text
/tq snapshot nearest 7 3 7
/tq ask nearest Dig a 5x5 pit 1 block deep.
/tq snapshot nearest 7 3 7
/tq diff <beforeSnapshotId> <afterSnapshotId>
```

Expected diff: block changes in the captured 5x5 footprint below the turtle path.

If the run was started through a board session, evaluate it:

```text
POST /sessions/{sessionId}/evaluate
```

with the before/after snapshot ids. The evaluator checks receipts and world diff evidence.
