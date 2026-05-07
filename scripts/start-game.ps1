$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$bridgeProject = Join-Path $root "bridge/Agentica.TurtleQuest.Bridge/Agentica.TurtleQuest.Bridge.csproj"
$plannerProject = Join-Path $root "planner/Agentica.TurtleQuest.AgenticaPlanner/Agentica.TurtleQuest.AgenticaPlanner.csproj"
$modDir = Join-Path $root "mod/agentica-turtlequest-neoforge"
$logDir = Join-Path $root "run/logs"
$bridgeOut = Join-Path $logDir "bridge.out.log"
$bridgeErr = Join-Path $logDir "bridge.err.log"

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

& (Join-Path $PSScriptRoot "sync-mods.ps1")

function Set-DefaultEnv {
    param(
        [string]$Name,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($Name))) {
        [Environment]::SetEnvironmentVariable($Name, $Value, "Process")
    }
}

$agenticaEnv = "C:\Users\Zythis\source\repos\Agentica\.env"
if (Test-Path $agenticaEnv) {
    Set-DefaultEnv "TURTLEQUEST_LLM_ENV_FILE" $agenticaEnv
}

Set-DefaultEnv "TURTLEQUEST_USE_PLANNER_FOR_MESSAGES" "true"
Set-DefaultEnv "TURTLEQUEST_PLANNER_MODE" "agentica"
Set-DefaultEnv "TURTLEQUEST_PLANNER_FALLBACK_MODE" "deterministic"
Set-DefaultEnv "TURTLEQUEST_PLANNER_REPAIR_ATTEMPTS" "1"
Set-DefaultEnv "AGENTICA_TURTLEQUEST_PLANNER_COMMAND" "dotnet"
Set-DefaultEnv "AGENTICA_TURTLEQUEST_PLANNER_ARGS" "run --project `"$plannerProject`" --no-restore --"
Set-DefaultEnv "AGENTICA_TURTLEQUEST_PLANNER_CWD" $root
Set-DefaultEnv "AGENTICA_TURTLEQUEST_PLANNER_TIMEOUT_SECONDS" "240"
Set-DefaultEnv "TURTLEQUEST_PLANNER_REQUEST_TIMEOUT_SECONDS" "240"
Set-DefaultEnv "TURTLEQUEST_BRIDGE_REQUEST_TIMEOUT_SECONDS" "15"
Set-DefaultEnv "TURTLEQUEST_AUTO_REPLAN_ON_BLOCKED" "true"
Set-DefaultEnv "TURTLEQUEST_RUNTIME_REPLAN_MODE" "agentica"
Set-DefaultEnv "TURTLEQUEST_RUNTIME_REPLAN_ATTEMPTS" "1"

Write-Host "Starting TurtleQuest bridge at http://127.0.0.1:57421"
Write-Host "Planner mode: $env:TURTLEQUEST_PLANNER_MODE via $env:AGENTICA_TURTLEQUEST_PLANNER_COMMAND"
$bridge = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList @("run", "--project", $bridgeProject) `
    -PassThru `
    -WindowStyle Hidden `
    -RedirectStandardOutput $bridgeOut `
    -RedirectStandardError $bridgeErr

try {
    $healthy = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:57421/health" -Method Get -TimeoutSec 1
            if ($health.status -eq "ok") {
                $healthy = $true
                break
            }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $healthy) {
        throw "Bridge did not become healthy. See $bridgeOut and $bridgeErr."
    }

    Write-Host "Launching NeoForge client."
    Write-Host "In game: /tq kit"
    Write-Host "In game: /tq ask nearest Dig a 5x5 pit 1 block deep."
    Write-Host "After completion: /tq status <runId>"
    Push-Location $modDir
    try {
        & .\gradlew.bat runClient
    } finally {
        Pop-Location
    }
} finally {
    if ($bridge -and -not $bridge.HasExited) {
        Stop-Process -Id $bridge.Id -Force
    }
}
