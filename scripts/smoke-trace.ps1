param(
    [switch]$UseMock
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$bridgeProject = Join-Path $root "bridge/TurtleQuest.Bridge/TurtleQuest.Bridge.csproj"
$mockPlanner = Join-Path $PSScriptRoot "mock-agentica-planner.ps1"
$traceDir = Join-Path $root "run/traces-smoke"

if (Test-Path $traceDir) {
    Remove-Item -Recurse -Force $traceDir
}

$env:TURTLEQUEST_TRACE_DIR = $traceDir
$env:TURTLEQUEST_TRACE_ENABLED = "true"

if ($UseMock) {
    $env:TURTLEQUEST_AGENTICA_PLANNER_COMMAND = "powershell"
    $env:TURTLEQUEST_AGENTICA_PLANNER_ARGS = "-NoProfile -ExecutionPolicy Bypass -File `"$mockPlanner`""
    $env:TURTLEQUEST_AGENTICA_PLANNER_CWD = $root
}

function Wait-Bridge {
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:57421/health" -Method Get -TimeoutSec 1
            if ($health.status -eq "ok") {
                return
            }
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }

    throw "Bridge did not become healthy."
}

$bridge = Start-Process -WindowStyle Hidden -PassThru -FilePath dotnet -ArgumentList @(
    "run",
    "--no-build",
    "--project",
    $bridgeProject
)

try {
    Wait-Bridge

    $request = @{
        turtleId = "trace-turtle"
        worldId = "trace-world"
        playerId = "trace-player"
        message = "Dig a 5x5 pit 1 block deep."
        position = @{ x = 0; y = 64; z = 0 }
        orientation = "north"
    }

    $body = @{
        mode = if ($UseMock) { "agentica" } else { "deterministic" }
        request = $request
        repairAttempts = 1
        execute = $true
    } | ConvertTo-Json -Depth 32

    $generated = Invoke-RestMethod `
        -Uri "http://127.0.0.1:57421/planner/generate" `
        -Method Post `
        -ContentType "application/json" `
        -Body $body

    $runId = $generated.run.runId
    $null = Invoke-RestMethod -Uri "http://127.0.0.1:57421/runs/$runId/next-command" -Method Get
    $trace = Invoke-RestMethod -Uri "http://127.0.0.1:57421/runs/$runId/trace" -Method Get
    $traceText = [string]$trace

    if ($traceText -notmatch "run.created_from_generated_plan" -or
        $traceText -notmatch "run.next_command.dequeued") {
        throw "Trace did not include expected lifecycle events."
    }

    [pscustomobject]@{
        RunId = $runId
        TraceDir = $traceDir
        TracePath = Join-Path $traceDir "$runId/events.jsonl"
        TraceLines = ($traceText -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
        HasPlannerTrace = Test-Path (Join-Path $traceDir "planner/events.jsonl")
    } | ConvertTo-Json -Depth 8
} finally {
    if ($bridge -and -not $bridge.HasExited) {
        Stop-Process -Id $bridge.Id -Force
    }
}
