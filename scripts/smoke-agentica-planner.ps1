param(
    [switch]$UseMock,
    [switch]$InvalidFirst,
    [switch]$NoExecute,
    [switch]$NoSimulate,
    [int]$RepairAttempts = 1,
    [string]$Prompt = "Dig a 5x5 pit 1 block deep."
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$bridgeProject = Join-Path $root "bridge/TurtleQuest.Bridge/TurtleQuest.Bridge.csproj"
$bridgeDir = Split-Path -Parent $bridgeProject
$mockPlanner = Join-Path $PSScriptRoot "mock-agentica-planner.ps1"

function Import-EnvFile {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return
    }

    Get-Content $Path | ForEach-Object {
        $line = $_.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith("#")) {
            return
        }

        $equals = $line.IndexOf("=")
        if ($equals -le 0) {
            return
        }

        $key = $line.Substring(0, $equals).Trim()
        $value = $line.Substring($equals + 1).Trim()
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        if (-not [string]::IsNullOrWhiteSpace($key) -and
            [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($key))) {
            [Environment]::SetEnvironmentVariable($key, $value, "Process")
        }
    }
}

Import-EnvFile (Join-Path $root ".env")
Import-EnvFile (Join-Path $root ".env.local")
Import-EnvFile (Join-Path $bridgeDir ".env")
Import-EnvFile (Join-Path $bridgeDir ".env.local")

if ($UseMock) {
    $mockArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$mockPlanner`""
    if ($InvalidFirst) {
        $mockArgs = "$mockArgs -InvalidFirst"
    }

    $env:TURTLEQUEST_AGENTICA_PLANNER_COMMAND = "powershell"
    $env:TURTLEQUEST_AGENTICA_PLANNER_ARGS = $mockArgs
    $env:TURTLEQUEST_AGENTICA_PLANNER_CWD = $root
}

if ($InvalidFirst) {
    $env:TURTLEQUEST_MOCK_PLANNER_INVALID_FIRST = "1"
} else {
    Remove-Item Env:\TURTLEQUEST_MOCK_PLANNER_INVALID_FIRST -ErrorAction SilentlyContinue
}

if ([string]::IsNullOrWhiteSpace($env:TURTLEQUEST_AGENTICA_PLANNER_COMMAND)) {
    throw "TURTLEQUEST_AGENTICA_PLANNER_COMMAND is not configured. Pass -UseMock or configure the real Agentica planner command in the bridge environment."
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

    $turtleRequest = @{
        turtleId = "smoke-turtle"
        worldId = "smoke-world"
        playerId = "smoke-player"
        message = $Prompt
        position = @{ x = 0; y = 64; z = 0 }
        orientation = "north"
    }

    $body = @{
        mode = "agentica"
        request = $turtleRequest
        repairAttempts = $RepairAttempts
        execute = -not $NoExecute
    } | ConvertTo-Json -Depth 32

    $generated = Invoke-RestMethod `
        -Uri "http://127.0.0.1:57421/planner/generate" `
        -Method Post `
        -ContentType "application/json" `
        -Body $body

    if (-not $generated.validation.valid) {
        $errors = $generated.validation.errors -join " | "
        throw "Agentica planner smoke failed validation: $errors"
    }

    $simulated = $null
    if (-not $NoExecute -and -not $NoSimulate) {
        $simulated = Invoke-RestMethod `
            -Uri "http://127.0.0.1:57421/runs/$($generated.run.runId)/simulate" `
            -Method Post

        if ($simulated.status -ne "completed") {
            throw "Generated run did not simulate to completion. Status: $($simulated.status)"
        }
    }

    [pscustomobject]@{
        PlannerCommand = $env:TURTLEQUEST_AGENTICA_PLANNER_COMMAND
        PlannerArgs = $env:TURTLEQUEST_AGENTICA_PLANNER_ARGS
        PlanKind = $generated.plan.planKind
        Valid = $generated.validation.valid
        RepairAttempts = $generated.repairAttempts.Count
        RunId = $generated.run.runId
        SimulatedStatus = if ($simulated) { $simulated.status } else { $null }
        SimulatedReceipts = if ($simulated) { $simulated.receipts.Count } else { $null }
    } | ConvertTo-Json -Depth 8
} finally {
    if ($bridge -and -not $bridge.HasExited) {
        Stop-Process -Id $bridge.Id -Force
    }
}
