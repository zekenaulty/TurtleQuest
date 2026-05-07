$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$traceRoot = if ($env:TURTLEQUEST_TRACE_DIR) {
    Join-Path $env:TURTLEQUEST_TRACE_DIR "planner-host"
} else {
    Join-Path $root "run/traces/planner-host"
}

if (-not (Test-Path $traceRoot)) {
    throw "Planner-host trace directory does not exist: $traceRoot"
}

$latest = Get-ChildItem $traceRoot -Directory |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $latest) {
    throw "No planner-host trace directories found under $traceRoot"
}

$events = Join-Path $latest.FullName "events.jsonl"
if (-not (Test-Path $events)) {
    throw "Latest planner-host trace has no events.jsonl: $($latest.FullName)"
}

Write-Output $latest.FullName
Get-Content $events
