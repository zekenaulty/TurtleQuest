param(
    [switch]$NoExecute,
    [switch]$NoSimulate,
    [int]$RepairAttempts = 1,
    [string]$Prompt = "Dig a 5x5 pit 1 block deep.",
    [string]$Model = $env:TURTLEQUEST_LLM_MODEL
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$agenticaEnv = Join-Path (Split-Path -Parent $root) "Agentica/.env"
if ([string]::IsNullOrWhiteSpace($env:TURTLEQUEST_LLM_ENV_FILE) -and (Test-Path $agenticaEnv)) {
    $env:TURTLEQUEST_LLM_ENV_FILE = $agenticaEnv
}

$plannerProject = Join-Path $root "planner/TurtleQuest.AgenticaPlanner/TurtleQuest.AgenticaPlanner.csproj"
$plannerArgs = "run --project `"$plannerProject`" --no-restore --"
if (-not [string]::IsNullOrWhiteSpace($Model)) {
    $plannerArgs = "$plannerArgs --model `"$Model`""
}

$env:TURTLEQUEST_AGENTICA_PLANNER_COMMAND = "dotnet"
$env:TURTLEQUEST_AGENTICA_PLANNER_ARGS = $plannerArgs
$env:TURTLEQUEST_AGENTICA_PLANNER_CWD = $root
$env:TURTLEQUEST_AGENTICA_PLANNER_TIMEOUT_SECONDS = "240"

$smokeArgs = @{
    RepairAttempts = $RepairAttempts
    Prompt = $Prompt
}
if ($NoExecute) {
    $smokeArgs.NoExecute = $true
}
if ($NoSimulate) {
    $smokeArgs.NoSimulate = $true
}

& (Join-Path $PSScriptRoot "smoke-agentica-planner.ps1") @smokeArgs
