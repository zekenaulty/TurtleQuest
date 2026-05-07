# Agentica Integration

TurtleQuest should follow the existing Agentica scenario pattern, not create a separate orchestration model.

## Existing Pattern

Agentica CLI scenarios such as MazeQuest and WorkbenchQuest use this shape:

```text
Board
  defines scenario/quest catalog

Session
  owns current authoritative scenario state

Tools
  expose scoped legal actions

Runner
  executes planner-selected tool calls

OutcomeReporter
  turns receipts into completion evidence
```

The LLM planner receives a bounded objective plus public state. It does not receive hidden state and it does not declare success directly. Completion is accepted only through receipt-backed artifacts.

## TurtleQuest Mapping

```text
TurtleQuestBoard
  benchmark and mission definitions

TurtleQuestSession
  turtle id, world id, position, facing, inventory summary, run state

TurtleQuestTools
  planner_context
  behavior.find_tree
  behavior.harvest_tree
  behavior.build_column
  behavior.excavate_rectangular_pit
  validate_plan
  submit_plan
  get_run
  get_receipts
  evaluate_session

TurtleQuestBridge
  remote host executor behind those tools

TurtleQuestOutcomeReporter
  receipts, world diffs, invariants, completion artifacts
```

The bridge already provides the remote executor surface:

```text
POST /planner/context
POST /planner/generate
POST /planner/validate
POST /runs/from-plan
GET  /runs/{runId}
POST /sessions/{sessionId}/evaluate
```

## Bridge-Side Agentica Adapter

TurtleQuest does not modify Agentica. The bridge can call an external Agentica planner command when `mode = agentica`.

Configuration:

```text
AGENTICA_TURTLEQUEST_PLANNER_COMMAND
  required executable path or command name

AGENTICA_TURTLEQUEST_PLANNER_ARGS
  optional argument string

AGENTICA_TURTLEQUEST_PLANNER_CWD
  optional working directory

AGENTICA_TURTLEQUEST_PLANNER_TIMEOUT_SECONDS
  optional timeout, default 120
```

Subprocess contract:

```text
stdin:
  TurtleAgenticaPlannerCommandRequest JSON

stdout:
  TurtleCompiledPlan JSON
  or an object with a plan property containing TurtleCompiledPlan
```

Golden fixtures:

```text
fixtures/agentica-planner/tq-pit-stdin.json
fixtures/agentica-planner/tq-pit-stdout.json
```

Smoke command:

```powershell
./scripts/smoke-agentica-planner.ps1 -UseMock
./scripts/smoke-agentica-planner.ps1 -UseMock -InvalidFirst
```

Against a real configured command:

```powershell
$env:AGENTICA_TURTLEQUEST_PLANNER_COMMAND = "dotnet"
$env:AGENTICA_TURTLEQUEST_PLANNER_ARGS = "run --project C:\Users\Zythis\source\repos\Agentica\Agentica.CLI -- turtlequest-plan"
$env:AGENTICA_TURTLEQUEST_PLANNER_CWD = "C:\Users\Zythis\source\repos\Agentica"
./scripts/smoke-agentica-planner.ps1
```

The command above is illustrative until an Agentica planner command exists with this stdin/stdout contract.

The command receives:

```text
goal
attempt number
planner context
previous validator repair attempts
```

The bridge remains authoritative after the subprocess returns:

```text
deserialize plan
validate supported primitives and budget
if invalid, retry with validator feedback up to repairAttempts
if valid and execute = true, enqueue plan into the turtle run queue
```

This lets Agentica reason and compose without giving it direct Minecraft authority.

## Behavior Tool Surface

TurtleQuest behaviors are not bridge-only shortcuts. They are exposed to Agentica as planner tools:

```text
turtlequest.behavior.find_tree
turtlequest.behavior.harvest_tree
turtlequest.behavior.build_column
turtlequest.behavior.excavate_rectangular_pit
```

The planner host also exposes read-only context tools:

```text
turtlequest.get_context
turtlequest.get_receipts
```

These tools are read-only planner assists. Calling one does not mutate Minecraft. Instead, behavior tools return:

```text
behavior id
normalized behavior arguments
transition contract
recommended flattened primitive steps
completion rule
```

The agent then emits the final `turtlequest.compiled_plan` artifact through:

```text
turtlequest.emit_compiled_plan
```

That artifact is still validated by the bridge before execution. This preserves the split:

```text
Agentica chooses and composes behavior tools.
Behavior tools expose host-owned skills and invariants.
The bridge validates flattened IR.
The mod executes primitives against Minecraft and returns receipts.
```

For example, `turtlequest.behavior.harvest_tree` exposes the durable skill:

```text
scanNearby
moveTowardRelative
digRememberedTarget
fellRememberedTree
getInventory
returnHome
emitStatus
completeObjective
```

The important distinction is that `fellRememberedTree` is itself a host-owned behavior primitive. Agentica chooses it as a capability; the host owns the detailed Minecraft-safe trunk felling procedure and receipt shape.

See `docs/agentic-behavior-tooling.md` for the current behavior-tool contract and trace gap.

See `docs/agentica-ir-generation-flow.md` for the expected Agentica run-step to TurtleQuest IR mapping.

## Configuration Policy

Use bridge environment for planner configuration and model credentials.

Do:

```text
set environment variables before starting the bridge
load local .env-style files from the bridge process if needed later
keep credentials out of git and world saves
```

Do not:

```text
store API keys in Minecraft world data
send API keys through /tq commands
write API keys into mod config that may be copied with the world
```

The mod should configure only non-secret bridge routing, such as bridge URL or turtle behavior toggles. Secrets belong to the bridge process environment because the bridge owns external model/planner calls.

Current bridge configuration:

```text
TURTLEQUEST_BRIDGE_URL
  bridge listen URL, default http://127.0.0.1:57421

TURTLEQUEST_USE_PLANNER_FOR_MESSAGES
  when true, /turtles/{id}/messages routes through /planner/generate before creating a run

TURTLEQUEST_PLANNER_MODE
  deterministic, mock-llm, or agentica; default deterministic

TURTLEQUEST_PLANNER_FALLBACK_MODE
  fallback mode when the configured planner returns invalid IR; default deterministic

TURTLEQUEST_PLANNER_REPAIR_ATTEMPTS
  validator repair attempts for planner mode; default 1
```

The bridge loads `.env` and `.env.local` files from the bridge/repository ancestry if they exist, without overriding environment variables that are already set by the parent process. Use `bridge/Agentica.TurtleQuest.Bridge/.env.example` as the local template and keep real `.env` files out of git.

To make Agentica the default in-game path:

```powershell
Copy-Item ./bridge/Agentica.TurtleQuest.Bridge/.env.example ./bridge/Agentica.TurtleQuest.Bridge/.env
# Edit AGENTICA_TURTLEQUEST_PLANNER_* to point at the real planner command.
./scripts/start-game.ps1
```

`/tq ask nearest <prompt>` will then create runs from `mode = agentica` planner output. The bridge still validates the returned IR and will fall back to the configured fallback mode if Agentica fails validation.

## Transport Position

Keep the Agentica planner bridge as a subprocess stdin/stdout contract for the first live slice.

A WebSocket runner/bus is useful when TurtleQuest needs long-lived bidirectional events:

```text
streaming planner traces
human approval interrupts
live replanning from turtle receipts
multi-turtle coordination
continuous world-state subscriptions
cancel/pause/resume commands with low latency
```

The tradeoff is operational complexity:

```text
connection lifecycle
reconnect and replay semantics
message ordering
auth/session boundaries
backpressure
test fixture complexity
harder smoke tests than single stdin/stdout JSON
```

The practical path is:

```text
1. subprocess planner for request/plan/validate/execute
2. keep HTTP polling for turtle command/receipt execution
3. add a WebSocket bus only after receipt-driven replanning or streaming planner state becomes a real requirement
```

This keeps the planning boundary stable: Agentica returns TurtleQuest IR, the bridge validates, and Minecraft remains authoritative.

## First Live Agentica Slice

The first Agentica-backed run should not use Minecraft construction, storage, recipes, diamonds, or houses.

Goal:

```text
Dig a 5x5 pit 1 block deep.
```

The Agentica planner receives:

```text
goal
turtle position/facing/world id
supported primitive actions
execution rules
cookbook examples
validator feedback from failed attempts
```

The planner must return:

```text
flattened TurtleQuest primitive steps
```

The bridge validates the plan and either:

```text
enqueues it into the turtle run queue
```

or:

```text
returns repairable validator errors
```

## Repair Loop

The first repair loop should be small and explicit:

```text
attempt 1: planner returns flattened IR
validate
if invalid, feed validator errors back once
attempt 2: planner returns repaired flattened IR
validate
if valid, enqueue
if invalid, return blocked result
```

This is enough to test whether the LLM can compose from examples without giving it broad world authority.

## Replan On Failure

There are two distinct replans:

```text
pre-execution repair
  already supported through repairAttempts when generated IR fails validation

runtime replan
  should be added after the first Agentica subprocess smoke passes
```

Runtime replan should trigger only on receipt-backed failure:

```text
turtle command fails
bridge pauses run as blocked
bridge packages current receipts, failed command, turtle state, and original goal
planner returns a new bounded continuation plan
validator checks it
valid continuation appends to the same run
invalid continuation leaves the run blocked with repair errors
```

Do not replan from screenshots or vibes. Runtime replan should start from command receipts and local world state evidence.

Bridge continuation endpoint:

```text
POST /runs/{runId}/continue-from-plan
```

Rules:

```text
run must currently be blocked
continuation plan must use supported primitives
continuation plan does not need startBehavior
continuation plan must end with completeObjective for this first slice
validated continuation steps append to the existing run queue
```

Implementation note:

```text
continuation replaces remaining pending commands from the failed plan
receipts stay on the run as evidence
```

Fixtures:

```text
fixtures/continuation/blocked-dig-receipt.json
fixtures/continuation/emit-status-complete-plan.json
```

Runtime replan endpoint:

```text
POST /runs/{runId}/replan
```

It builds:

```text
TurtleRuntimeReplanContext
  original goal
  full run snapshot
  latest failed receipt
  pending command count
  supported primitives
  execution rules
```

Then it calls the configured Agentica subprocess with:

```text
TurtleAgenticaReplanCommandRequest JSON
```

The returned plan is validated as a continuation, then applied through the same continuation queue replacement.

Smoke commands:

```powershell
./scripts/smoke-runtime-replan.ps1 -UseMock
./scripts/smoke-runtime-replan.ps1 -UseMock -InvalidFirst
```

Mod-side runtime replan:

```text
/tq replan <runId>
```

Automatic executor replan is opt-in:

```powershell
$env:TURTLEQUEST_AUTO_REPLAN_ON_BLOCKED = "true"
$env:TURTLEQUEST_RUNTIME_REPLAN_MODE = "agentica"
$env:TURTLEQUEST_RUNTIME_REPLAN_ATTEMPTS = "1"
```

When enabled, the turtle executor posts `/runs/{runId}/replan` after a failed receipt and resumes polling if the bridge accepts the continuation.

## Cookbook Growth Later

Successful runs can become cookbook candidates, but promotion should be gated.

```text
classify
  identify behavior class and reusable intent

refine
  remove incidental coordinates and player-specific state

decorate
  add assumptions, invariants, expected receipts, failure modes

store
  write as pending cookbook candidate

promote
  require deterministic validation or human approval
```

Do not auto-promote live runs into the trusted cookbook until the validator and evaluator are stronger.
