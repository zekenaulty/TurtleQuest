param()

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$traceDir = Join-Path $root "run/traces-smoke-replan"

if (Test-Path $traceDir) {
    Remove-Item -Recurse -Force $traceDir
}

$env:TURTLEQUEST_TRACE_DIR = $traceDir
$env:TURTLEQUEST_TRACE_ENABLED = "true"

$result = & (Join-Path $PSScriptRoot "smoke-runtime-replan.ps1") -UseMock | ConvertFrom-Json
$tracePath = Join-Path $traceDir "$($result.RunId)/events.jsonl"
if (!(Test-Path $tracePath)) {
    throw "Trace file was not written: $tracePath"
}

$traceText = Get-Content $tracePath -Raw
if ($traceText -notmatch "run.created_from_prompt_catalog" -and
    $traceText -notmatch "run.created_from_prompt_planner") {
    throw "Trace missing expected run creation event."
}

foreach ($expected in @(
    "run.next_command.dequeued",
    "run.receipt_recorded",
    "run.replan_context_built",
    "run.replan_applied"
)) {
    if ($traceText -notmatch [regex]::Escape($expected)) {
        throw "Trace missing expected event: $expected"
    }
}

[pscustomobject]@{
    RunId = $result.RunId
    TracePath = $tracePath
    TraceLines = ($traceText -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
    CheckedEvents = 5
} | ConvertTo-Json -Depth 8
