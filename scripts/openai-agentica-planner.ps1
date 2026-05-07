param(
    [string]$Model = $env:TURTLEQUEST_LLM_MODEL,
    [string]$BaseUrl = $env:GEMINI_BASE_URL,
    [string]$EnvFile = $env:TURTLEQUEST_LLM_ENV_FILE,
    [int]$MaxOutputTokens = 8192,
    [switch]$PrintPrompt
)

$ErrorActionPreference = "Stop"

function Import-DotEnvFile($Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path $Path)) {
        return @()
    }

    $loaded = New-Object System.Collections.Generic.List[string]
    foreach ($line in Get-Content $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("#")) {
            continue
        }

        if ($trimmed.StartsWith("export ")) {
            $trimmed = $trimmed.Substring("export ".Length).TrimStart()
        }

        $equals = $trimmed.IndexOf("=")
        if ($equals -le 0) {
            continue
        }

        $key = $trimmed.Substring(0, $equals).Trim()
        $value = $trimmed.Substring($equals + 1).Trim()
        if ($value.Length -ge 2 -and $value[0] -eq '"' -and $value[$value.Length - 1] -eq '"') {
            $value = $value.Substring(1, $value.Length - 2).
                Replace("\n", "`n").
                Replace("\r", "`r").
                Replace("\t", "`t").
                Replace('\"', '"').
                Replace("\\", "\")
        } elseif ($value.Length -ge 2 -and $value[0] -eq "'" -and $value[$value.Length - 1] -eq "'") {
            $value = $value.Substring(1, $value.Length - 2)
        } else {
            $commentIndex = $value.IndexOf(" #")
            if ($commentIndex -ge 0) {
                $value = $value.Substring(0, $commentIndex)
            }
            $value = $value.TrimEnd()
        }

        if (-not [string]::IsNullOrWhiteSpace($key)) {
            [Environment]::SetEnvironmentVariable($key, $value, "Process")
            $loaded.Add($key)
        }
    }

    return $loaded
}

if ([string]::IsNullOrWhiteSpace($EnvFile)) {
    $adjacentAgenticaEnv = "C:\Users\Zythis\source\repos\Agentica\.env"
    if (Test-Path $adjacentAgenticaEnv) {
        $EnvFile = $adjacentAgenticaEnv
    }
}

$loadedKeys = Import-DotEnvFile $EnvFile
if ($PrintPrompt -and $loadedKeys.Count -gt 0) {
    [Console]::Error.WriteLine("Loaded env keys: " + (($loadedKeys | Sort-Object -Unique) -join ", "))
}

if ([string]::IsNullOrWhiteSpace($Model)) {
    $Model = "gemini-2.5-flash"
}

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = "https://generativelanguage.googleapis.com/v1beta"
}

$apiKey = $env:GEMINI_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    $apiKey = $env:GOOGLE_API_KEY
}

if ([string]::IsNullOrWhiteSpace($apiKey)) {
    [Console]::Error.WriteLine("GEMINI_API_KEY or GOOGLE_API_KEY is required for openai-agentica-planner.ps1.")
    exit 2
}

$inputJson = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($inputJson)) {
    [Console]::Error.WriteLine("Planner stdin was empty.")
    exit 2
}

$request = $inputJson | ConvertFrom-Json
$isRuntimeReplan = $null -ne $request.context.failedReceipt
$contextJson = $request | ConvertTo-Json -Depth 80

$instructions = @"
You are the TurtleQuest planner subprocess for Agentica.

Return exactly one TurtleCompiledPlan JSON object. Do not wrap it in Markdown.

Required JSON shape:
{
  "planId": "string",
  "planKind": "string",
  "behaviorId": "string",
  "source": "gemini",
  "arguments": {},
  "steps": [
    { "action": "startBehavior", "arguments": {} }
  ],
  "validation": {
    "valid": true,
    "commandCount": 1,
    "commandBudget": 256,
    "errors": [],
    "warnings": []
  }
}

Rules:
- Produce flattened primitive steps only.
- Use only supportedPrimitiveActions from the input context.
- For initial plans, include startBehavior as the first step and completeObjective as the final step.
- For runtime replan continuations, do not include startBehavior; produce only continuation steps and finish with completeObjective.
- Do not use place, moveUp, moveDown, scanNearby, getInventory, selectSlot, or any unsupported action unless the input says it is supported.
- Keep the plan bounded and within the command budget implied by the context.
- If previousRepairAttempts are present, fix the validation errors they describe.
- If safe execution is impossible, return a plan with no steps, validation.valid=false, and clear validation.errors.
- Minecraft state is authoritative; receipts and validation decide success.
"@

$userPrompt = if ($isRuntimeReplan) {
    @"
Compile a runtime continuation plan from this blocked TurtleQuest run.

Input JSON:
$contextJson
"@
} else {
    @"
Compile an initial TurtleQuest plan from this planner context.

Input JSON:
$contextJson
"@
}

if ($PrintPrompt) {
    [Console]::Error.WriteLine($instructions)
    [Console]::Error.WriteLine($userPrompt)
}

$body = @{
    system_instruction = @{
        parts = @(
            @{ text = $instructions }
        )
    }
    contents = @(
        @{
            role = "user"
            parts = @(
                @{ text = $userPrompt }
            )
        }
    )
    generationConfig = @{
        temperature = 0
        maxOutputTokens = $MaxOutputTokens
        responseMimeType = "application/json"
    }
} | ConvertTo-Json -Depth 100

$encodedModel = [Uri]::EscapeDataString($Model)
$uri = $BaseUrl.TrimEnd("/") + "/models/$encodedModel`:generateContent?key=" + [Uri]::EscapeDataString($apiKey)

try {
    $response = Invoke-RestMethod `
        -Uri $uri `
        -Method Post `
        -Headers @{
            "Content-Type" = "application/json"
        } `
        -Body $body `
        -TimeoutSec 180
} catch {
    [Console]::Error.WriteLine("Gemini generateContent API call failed: " + $_.Exception.Message)
    if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
        [Console]::Error.WriteLine($_.ErrorDetails.Message)
    }
    if ($_.Exception.Response -and $_.Exception.Response.GetResponseStream()) {
        try {
            $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
            $responseText = $reader.ReadToEnd()
            if (-not [string]::IsNullOrWhiteSpace($responseText)) {
                [Console]::Error.WriteLine($responseText)
            }
        } catch {
            # Best effort provider diagnostics only.
        }
    }
    exit 3
}

function Get-OutputText($node) {
    if ($null -eq $node) {
        return $null
    }

    if ($node.PSObject.Properties.Name -contains "text" -and
        -not [string]::IsNullOrWhiteSpace($node.text)) {
        return [string]$node.text
    }

    if ($node.PSObject.Properties.Name -contains "parts") {
        foreach ($part in @($node.parts)) {
            $text = Get-OutputText $part
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                return $text
            }
        }
    }

    if ($node.PSObject.Properties.Name -contains "content") {
        $text = Get-OutputText $node.content
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            return $text
        }
    }

    if ($node.PSObject.Properties.Name -contains "candidates") {
        foreach ($candidate in @($node.candidates)) {
            $text = Get-OutputText $candidate
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                return $text
            }
        }
    }

    return $null
}

$outputText = Get-OutputText $response
if ([string]::IsNullOrWhiteSpace($outputText)) {
    [Console]::Error.WriteLine("Gemini response did not contain output text.")
    [Console]::Error.WriteLine(($response | ConvertTo-Json -Depth 20))
    exit 4
}

try {
    $plan = $outputText | ConvertFrom-Json
    $plan | ConvertTo-Json -Depth 100
} catch {
    [Console]::Error.WriteLine("Model output was not valid TurtleCompiledPlan JSON: " + $_.Exception.Message)
    [Console]::Error.WriteLine($outputText)
    exit 5
}
