param(
    [string]$Prompt = "Dig a straight tunnel 5 blocks forward and return."
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$bridgeProject = Join-Path $root "bridge/Agentica.TurtleQuest.Bridge/Agentica.TurtleQuest.Bridge.csproj"
$continuationPlanPath = Join-Path $root "fixtures/continuation/emit-status-complete-plan.json"

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

    $command = Invoke-RestMethod `
        -Uri "http://127.0.0.1:57421/runs/$($start.runId)/next-command" `
        -Method Get

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

    $plan = Get-Content $continuationPlanPath -Raw | ConvertFrom-Json
    $continueBody = @{
        runId = $start.runId
        reason = "smoke blocked receipt"
        plan = $plan
    } | ConvertTo-Json -Depth 32

    $continued = Invoke-RestMethod `
        -Uri "http://127.0.0.1:57421/runs/$($start.runId)/continue-from-plan" `
        -Method Post `
        -ContentType "application/json" `
        -Body $continueBody

    $next1 = Invoke-RestMethod -Uri "http://127.0.0.1:57421/runs/$($start.runId)/next-command" -Method Get
    $next2 = Invoke-RestMethod -Uri "http://127.0.0.1:57421/runs/$($start.runId)/next-command" -Method Get

    if ($next1.action -ne "emitStatus" -or $next2.action -ne "completeObjective") {
        throw "Continuation queue did not replace stale pending commands."
    }

    [pscustomobject]@{
        RunId = $start.runId
        FailedAction = $command.action
        BlockedStatus = $blocked.status
        ContinueStatus = $continued.status
        ContinuePending = $continued.pendingCommands
        NextAction1 = $next1.action
        NextAction2 = $next2.action
    } | ConvertTo-Json -Depth 8
} finally {
    if ($bridge -and -not $bridge.HasExited) {
        Stop-Process -Id $bridge.Id -Force
    }
}
