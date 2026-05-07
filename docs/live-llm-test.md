# Live LLM Test

This is the TurtleQuest-side path for a live LLM-backed planner test. It does not modify the adjacent Agentica repo.

## Primary Planner Host

The primary live path is TurtleQuest's own .NET Agentica planner host:

```text
planner/Agentica.TurtleQuest.AgenticaPlanner
```

It implements the bridge subprocess contract:

```text
stdin:
  TurtleAgenticaPlannerCommandRequest JSON
  or TurtleAgenticaReplanCommandRequest JSON

stdout:
  TurtleCompiledPlan JSON
```

Inside that subprocess, TurtleQuest creates an `AgenticaRunner`, uses Agentica.Clients Gemini through `LlmWorkflowPlanner`, exposes one synthesis tool named `turtlequest.emit_compiled_plan`, and writes the emitted `TurtleCompiledPlan` artifact to stdout.

The older direct Gemini PowerShell shim remains available as a fallback at `scripts/openai-agentica-planner.ps1`, but it is no longer the preferred live path.

## Required Environment

Set secrets and planner routing in the shell that starts the bridge:

```powershell
$env:TURTLEQUEST_LLM_ENV_FILE = "C:\Users\Zythis\source\repos\Agentica\.env"
$env:TURTLEQUEST_LLM_MODEL = "gemini-2.5-flash"

$env:AGENTICA_TURTLEQUEST_PLANNER_COMMAND = "dotnet"
$env:AGENTICA_TURTLEQUEST_PLANNER_ARGS = "run --project `"C:\Users\Zythis\source\repos\Agentica.TurtleQuest\planner\Agentica.TurtleQuest.AgenticaPlanner\Agentica.TurtleQuest.AgenticaPlanner.csproj`" --no-restore --"
$env:AGENTICA_TURTLEQUEST_PLANNER_CWD = "C:\Users\Zythis\source\repos\Agentica.TurtleQuest"
$env:AGENTICA_TURTLEQUEST_PLANNER_TIMEOUT_SECONDS = "240"
```

Template:

```text
scripts/live-llm-env.example.ps1
```

Do not put `GEMINI_API_KEY` or `GOOGLE_API_KEY` in Minecraft commands, world data, or mod config.

## Bridge Smoke

Run:

```powershell
./scripts/smoke-agentica-host-planner.ps1
```

Expected:

```text
PlanKind is model-generated
Valid is true
RunId is present
SimulatedStatus is completed
SimulatedStatus is completed
```

## Game Test

To route in-game prompts through the planner:

```powershell
$env:TURTLEQUEST_USE_PLANNER_FOR_PROMPTS = "true"
$env:TURTLEQUEST_DEFAULT_PLANNER_MODE = "agentica"
$env:TURTLEQUEST_DEFAULT_REPAIR_ATTEMPTS = "1"
```

Optional runtime blocked-run recovery:

```powershell
$env:TURTLEQUEST_AUTO_REPLAN_ON_BLOCKED = "true"
$env:TURTLEQUEST_RUNTIME_REPLAN_MODE = "agentica"
$env:TURTLEQUEST_RUNTIME_REPLAN_ATTEMPTS = "1"
```

Start:

```powershell
./scripts/start-game.ps1
```

In game:

```text
/tq ask nearest Dig a 5x5 pit 1 block deep.
```

## Trace Review

After a run:

```text
GET /runs/{runId}/trace
```

or:

```powershell
Get-Content run/traces/<runId>/events.jsonl
```

Use the trace to inspect the prompt, planner context, generated plan, validation, command receipts, and any replan attempts.

## References

- Agentica runner source: `C:\Users\Zythis\source\repos\Agentica\Agentica\Execution\AgenticaRunner.cs`
- Agentica Gemini client source: `C:\Users\Zythis\source\repos\Agentica\Agentica.Clients\Gemini`
