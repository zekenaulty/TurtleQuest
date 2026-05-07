param(
    [switch]$InvalidFirst,
    [switch]$NoExecute,
    [switch]$NoSimulate,
    [int]$RepairAttempts = 1,
    [string]$Prompt = "Dig a 5x5 pit 1 block deep.",
    [string]$Model = $env:TURTLEQUEST_LLM_MODEL
)

$ErrorActionPreference = "Stop"

$agenticaEnv = "C:\Users\Zythis\source\repos\Agentica\.env"
if ([string]::IsNullOrWhiteSpace($env:TURTLEQUEST_LLM_ENV_FILE) -and (Test-Path $agenticaEnv)) {
    $env:TURTLEQUEST_LLM_ENV_FILE = $agenticaEnv
}

if ([string]::IsNullOrWhiteSpace($env:GEMINI_API_KEY) -and
    [string]::IsNullOrWhiteSpace($env:GOOGLE_API_KEY) -and
    -not (Test-Path $env:TURTLEQUEST_LLM_ENV_FILE)) {
    throw "GEMINI_API_KEY or GOOGLE_API_KEY is not configured. Set it in the bridge shell environment, or set TURTLEQUEST_LLM_ENV_FILE."
}

$planner = Join-Path $PSScriptRoot "openai-agentica-planner.ps1"
$args = "-NoProfile -ExecutionPolicy Bypass -File `"$planner`""
if (-not [string]::IsNullOrWhiteSpace($Model)) {
    $args = "$args -Model `"$Model`""
}

$env:AGENTICA_TURTLEQUEST_PLANNER_COMMAND = "powershell"
$env:AGENTICA_TURTLEQUEST_PLANNER_ARGS = $args
$env:AGENTICA_TURTLEQUEST_PLANNER_CWD = Split-Path -Parent $PSScriptRoot
$env:AGENTICA_TURTLEQUEST_PLANNER_TIMEOUT_SECONDS = "180"

$smokeArgs = @{
    RepairAttempts = $RepairAttempts
    Prompt = $Prompt
}
if ($InvalidFirst) {
    $smokeArgs.InvalidFirst = $true
}
if ($NoExecute) {
    $smokeArgs.NoExecute = $true
}
if ($NoSimulate) {
    $smokeArgs.NoSimulate = $true
}

& (Join-Path $PSScriptRoot "smoke-agentica-planner.ps1") @smokeArgs
