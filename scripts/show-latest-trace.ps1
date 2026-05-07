param(
    [string]$RunId,
    [int]$Tail = 40
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$traceRoot = Join-Path $root "run/traces"

if (-not (Test-Path $traceRoot)) {
    throw "No trace directory found at $traceRoot."
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $latest = Get-ChildItem $traceRoot -Directory |
        Where-Object { $_.Name -like "tq-*" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $latest) {
        throw "No TurtleQuest run trace directories found under $traceRoot."
    }

    $RunId = $latest.Name
}

$runEvents = Join-Path $traceRoot "$RunId/events.jsonl"
$plannerEvents = Join-Path $traceRoot "planner/events.jsonl"

[pscustomobject]@{
    RunId = $RunId
    RunEvents = $runEvents
    PlannerEvents = $plannerEvents
} | ConvertTo-Json -Depth 4

if (Test-Path $plannerEvents) {
    ""
    "== planner tail =="
    Get-Content $plannerEvents -Tail ([Math]::Max(1, [Math]::Min($Tail, 80)))
}

if (Test-Path $runEvents) {
    ""
    "== run tail =="
    Get-Content $runEvents -Tail ([Math]::Max(1, [Math]::Min($Tail, 80)))
} else {
    ""
    "Run trace file not found: $runEvents"
}
