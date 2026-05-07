# Trace Artifacts

TurtleQuest writes scoped bridge traces so failed runs can be inspected without reconstructing state from chat logs.

## Location

Default:

```text
run/traces/<runId>/events.jsonl
run/traces/planner/events.jsonl
```

Override:

```powershell
$env:TURTLEQUEST_TRACE_DIR = "C:\path\to\traces"
```

Disable:

```powershell
$env:TURTLEQUEST_TRACE_ENABLED = "false"
```

## Shape

Each line is one JSON event:

```json
{
  "observedAt": "2026-05-06T00:00:00.0000000+00:00",
  "scopeId": "tq-example",
  "eventType": "run.receipt_recorded",
  "payload": {}
}
```

## Run Events

Current event types include:

```text
run.created_from_prompt_catalog
run.created_from_prompt_planner
run.created_from_plan
run.created_from_generated_plan
run.next_command.dequeued
run.next_command.none
run.receipt_recorded
run.continuation_applied
run.continuation_rejected
run.replan_context_built
run.replan_invalid
run.replan_applied
run.simulated
```

The payloads intentionally include snapshots after state transitions where useful. That makes a trace self-contained enough to answer:

```text
what was asked
what behavior/plan was selected
what command was issued
what receipt came back
why the run blocked
what replan context was sent
what continuation was accepted
what final state was observed
```

## Reading

Via bridge:

```text
GET /runs/{runId}/trace
```

Via filesystem:

```powershell
Get-Content run/traces/<runId>/events.jsonl
```

## Scope

Trace artifacts are evidence streams, not authority. Receipts and world snapshots still determine validation. Traces make the sequence easy for us to inspect, compare, and debug.
