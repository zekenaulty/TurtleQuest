param(
    [switch]$InvalidFirst,
    [switch]$NoExecute,
    [switch]$NoSimulate,
    [int]$RepairAttempts = 1,
    [string]$Prompt = "Dig a 5x5 pit 1 block deep.",
    [string]$Model = $env:TURTLEQUEST_LLM_MODEL
)

$args = @{
    RepairAttempts = $RepairAttempts
    Prompt = $Prompt
}
if ($InvalidFirst) {
    $args.InvalidFirst = $true
}
if ($NoExecute) {
    $args.NoExecute = $true
}
if ($NoSimulate) {
    $args.NoSimulate = $true
}
if (-not [string]::IsNullOrWhiteSpace($Model)) {
    $args.Model = $Model
}

& (Join-Path $PSScriptRoot "smoke-openai-agentica-planner.ps1") @args
exit $LASTEXITCODE
