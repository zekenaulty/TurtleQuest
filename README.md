# Agentica.TurtleQuest

Agentica.TurtleQuest is a NeoForge sandbox for testing Agentica with an embodied, scoped Minecraft turtle actor.

The first version uses CC: Tweaked as the trusted turtle implementation and keeps Agentica behind a local bridge. The bridge is temporary by design: once `Agentica.API` is ready, the implementation behind the same boundary can be swapped without changing the Minecraft mod contract.

## Initial Target

- Minecraft: `1.21.1`
- NeoForge: `21.1.228`
- CC: Tweaked: `1.118.0`
- Runtime mod jar: `cc-tweaked-1.21.1-forge-1.118.0.jar`

## Layout

```text
bridge/      Local Agentica run bridge.
contracts/   Shared request, command, and receipt schemas.
docs/        Architecture and benchmark notes.
mod/         NeoForge mod scaffold.
scripts/     Setup and launch helpers.
third_party/ Pinned external mod metadata.
run/         Local runtime files, ignored by git.
```

## Bootstrap

```powershell
./scripts/sync-mods.ps1
./scripts/bootstrap-gradle-wrapper.ps1
dotnet run --project ./bridge/Agentica.TurtleQuest.Bridge
```

Launch bridge and the NeoForge client together:

```powershell
./scripts/start-game.ps1
```

The first benchmark is `TQ-001`: the player asks a turtle to dig a straight tunnel five blocks forward and return to its starting position.

## Current Smoke Slice

The bridge listens on `http://127.0.0.1:57421` by default. Override with `TURTLEQUEST_BRIDGE_URL`.

The NeoForge mod registers:

```text
/tq kit
/tq ask nearest <prompt>
/tq status <runId>
/tq simulate <runId>
/tq replan <runId>
```

`nearest` searches for a nearby CC:T turtle-like block and binds the bridge run to that turtle position. The current real executor uses CC:T turtle commands for `dig`, `moveForward`, and a narrow TQ-001 `returnHome`. `/tq simulate <runId>` remains available for bridge-side completion simulation.

The mod grants a small TurtleQuest dev kit on first login for the current dev session. `/tq kit` grants it again.

See `docs/smoke-test.md`.

Behavior catalog, work-loop, upkeep, storage, and quest-board slice notes are captured in `docs/behavior-slices.md`.

The first catalog-backed behavior definition lives at `behaviors/turtlequest.dig_line_return.json`.

Planner boundary notes are captured in `docs/planner-boundary.md`.

Board, session, and future snapshot/diff harness flow is captured in `docs/harness-flow.md`.

Mission primitive notes for diamonds, towers, and houses are captured in `docs/mission-primitives.md`.

Tiered primitive, shape-skill, and behavior command planning is captured in `docs/behavior-command-catalog.md`.

Agentica scenario integration notes are captured in `docs/agentica-integration.md`.

Bridge trace artifacts are captured in `docs/trace-artifacts.md`.

Live LLM smoke instructions are captured in `docs/live-llm-test.md`.

The implementation roadmap and definitions of done are captured in `docs/execution-roadmap.md`.

Agentica planner adapter smoke:

```powershell
./scripts/smoke-agentica-planner.ps1 -UseMock
./scripts/smoke-agentica-planner.ps1 -UseMock -InvalidFirst
./scripts/smoke-continuation.ps1
./scripts/smoke-runtime-replan.ps1 -UseMock
./scripts/smoke-trace.ps1
./scripts/smoke-trace-replan.ps1
./scripts/smoke-agentica-host-planner.ps1
./scripts/smoke-gemini-agentica-planner.ps1
```

`smoke-agentica-host-planner.ps1` is the primary live LLM path. It runs TurtleQuest's planner host, which references the adjacent Agentica and Agentica.Clients projects in-process. `smoke-gemini-agentica-planner.ps1` remains as a direct-provider fallback.

Runtime replan is opt-in for the game executor:

```powershell
$env:TURTLEQUEST_AUTO_REPLAN_ON_BLOCKED = "true"
$env:TURTLEQUEST_RUNTIME_REPLAN_MODE = "agentica"
$env:TURTLEQUEST_RUNTIME_REPLAN_ATTEMPTS = "1"
```

To route in-game `/tq ask nearest <prompt>` through the Agentica planner bridge, copy and edit the bridge environment template:

```powershell
Copy-Item ./bridge/Agentica.TurtleQuest.Bridge/.env.example ./bridge/Agentica.TurtleQuest.Bridge/.env
```

Set `TURTLEQUEST_PLANNER_MODE=agentica` and point `AGENTICA_TURTLEQUEST_PLANNER_*` at the real planner command before starting the bridge or `./scripts/start-game.ps1`.

`./scripts/start-game.ps1` now defaults those values to the local Agentica planner host:

```text
planner/Agentica.TurtleQuest.AgenticaPlanner
```

The current live test command is:

```text
/tq ask nearest Dig a 5x5 pit 1 block deep.
```

First construction smoke command:

```text
/tq ask nearest Build a column 5 blocks tall.
```

First resource scouting smoke command:

```text
/tq ask nearest Find a nearby tree.
```

This scans for log-like blocks and reports bounded evidence before we add pathing and harvesting.

First resource approach smoke command:

```text
/tq ask nearest Harvest a nearby tree.
```

This scans for log-like blocks, approaches the nearest remembered candidate, and stops adjacent before cutting.

After the run starts, inspect it in game and then from PowerShell:

```text
/tq status <runId>
```

```powershell
./scripts/show-latest-trace.ps1
```
