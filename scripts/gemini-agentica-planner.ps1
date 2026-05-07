param(
    [string]$Model = $env:TURTLEQUEST_LLM_MODEL,
    [string]$BaseUrl = $env:GEMINI_BASE_URL,
    [string]$EnvFile = $env:TURTLEQUEST_LLM_ENV_FILE,
    [int]$MaxOutputTokens = 8192,
    [switch]$PrintPrompt
)

$script = Join-Path $PSScriptRoot "openai-agentica-planner.ps1"
$argsList = @()
if (-not [string]::IsNullOrWhiteSpace($Model)) {
    $argsList += "-Model"
    $argsList += $Model
}
if (-not [string]::IsNullOrWhiteSpace($BaseUrl)) {
    $argsList += "-BaseUrl"
    $argsList += $BaseUrl
}
if (-not [string]::IsNullOrWhiteSpace($EnvFile)) {
    $argsList += "-EnvFile"
    $argsList += $EnvFile
}
$argsList += "-MaxOutputTokens"
$argsList += $MaxOutputTokens
if ($PrintPrompt) {
    $argsList += "-PrintPrompt"
}

$stdin = [Console]::In.ReadToEnd()
$stdin | & $script @argsList
exit $LASTEXITCODE
