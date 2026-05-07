param(
    [switch]$UseMock,
    [switch]$InvalidFirst,
    [int]$RepairAttempts = 1,
    [string]$Prompt = "Dig a straight tunnel 5 blocks forward and return."
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$bridgeProject = Join-Path $root "bridge/Agentica.TurtleQuest.Bridge/Agentica.TurtleQuest.Bridge.csproj"
$mockPlanner = Join-Path $PSScriptRoot "mock-agentica-planner.ps1"

if ($UseMock) {
    $mockArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$mockPlanner`""
    if ($InvalidFirst) {
        $mockArgs = "$mockArgs -InvalidFirst"
    }

    $env:AGENTICA_TURTLEQUEST_PLANNER_COMMAND = "powershell"
    $env:AGENTICA_TURTLEQUEST_PLANNER_ARGS = $mockArgs
    $env:AGENTICA_TURTLEQUEST_PLANNER_CWD = $root
}

if ([string]::IsNullOrWhiteSpace($env:AGENTICA_TURTLEQUEST_PLANNER_COMMAND)) {
    throw "AGENTICA_TURTLEQUEST_PLANNER_COMMAND is not configured. Pass -UseMock or configure the real Agentica planner command in the bridge environment."
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
        turtleId = "fixture-turtle"
        worldId = "fixture-world"
        playerId = "fixture-player"
        message = $Prompt
        position = @{ x = 0; y = 64; z = 0 }
        orientation = "north"
    }

    $start = Invoke-RestMethod `
        -Uri "http://127.0.0.1:57421/turtles/fixture-turtle/messages" `
        -Method Post `
        -ContentType "application/json" `
        -Body ($request | ConvertTo-Json -Depth 16)

    $command = Invoke-RestMethod -Uri "http://127.0.0.1:57421/runs/$($start.runId)/next-command" -Method Get
    $failedReceipt = @{
        runId = $start.runId
        turtleId = "fixture-turtle"
        commandId = $command.commandId
        action = $command.action
        success = $false
        position = @{ x = 0; y = 64; z = 0 }
        orientation = "north"
        observedAt = (Get-Date).ToUniversalTime().ToString("o")
        blockAhead = "minecraft:bedrock"
        hazards = @()
        inventoryDelta = @{}
        message = "Smoke fixture forced command failure."
    }

    $blocked = Invoke-RestMethod `
        -Uri "http://127.0.0.1:57421/runs/$($start.runId)/receipts" `
        -Method Post `
        -ContentType "application/json" `
        -Body ($failedReceipt | ConvertTo-Json -Depth 16)

    if ($blocked.status -ne "blocked") {
        throw "Expected run to become blocked, actual status: $($blocked.status)"
    }

    $replanBody = @{
        mode = "agentica"
        repairAttempts = $RepairAttempts
    } | ConvertTo-Json -Depth 16

    $replanned = Invoke-RestMethod `
        -Uri "http://127.0.0.1:57421/runs/$($start.runId)/replan" `
        -Method Post `
        -ContentType "application/json" `
        -Body $replanBody

    if (-not $replanned.validation.valid -or -not $replanned.applied) {
        throw "Runtime replan did not produce an applied valid continuation."
    }

    $next1 = Invoke-RestMethod -Uri "http://127.0.0.1:57421/runs/$($start.runId)/next-command" -Method Get
    $next2 = Invoke-RestMethod -Uri "http://127.0.0.1:57421/runs/$($start.runId)/next-command" -Method Get

    [pscustomobject]@{
        RunId = $start.runId
        FailedAction = $command.action
        ReplanPlanKind = $replanned.plan.planKind
        RepairAttempts = $replanned.repairAttempts.Count
        Applied = $replanned.applied
        PendingCommands = $replanned.continuation.pendingCommands
        NextAction1 = $next1.action
        NextAction2 = $next2.action
    } | ConvertTo-Json -Depth 8
} finally {
    if ($bridge -and -not $bridge.HasExited) {
        Stop-Process -Id $bridge.Id -Force
    }
}
