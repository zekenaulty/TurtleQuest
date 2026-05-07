param(
    [switch]$InvalidFirst
)

$inputJson = [Console]::In.ReadToEnd()
$request = $inputJson | ConvertFrom-Json
$attempt = [int]$request.attempt
$isRuntimeReplan = $null -ne $request.context.failedReceipt
$behaviorId = if ($isRuntimeReplan) { $request.context.run.behavior.behaviorId } else { $request.context.preview.behaviorId }
$commandBudget = 256

if ($isRuntimeReplan) {
  if ($attempt -eq 1 -and ($InvalidFirst -or $env:TURTLEQUEST_MOCK_PLANNER_INVALID_FIRST -eq '1')) {
    $plan = [ordered]@{
      planId = "plan-mock-agentica-runtime-invalid"
      planKind = "agentica_subprocess_runtime_mock"
      behaviorId = $behaviorId
      source = "mock_agentica_planner"
      arguments = @{ failedAction = $request.context.failedReceipt.action }
      steps = @(
        @{ action = "emitStatus"; arguments = @{ message = "Runtime replan invalid-first fixture." } }
      )
      validation = @{
        valid = $false
        commandCount = 1
        commandBudget = 16
        errors = @()
        warnings = @()
      }
    }

    $plan | ConvertTo-Json -Depth 32
    exit 0
  }

  $plan = [ordered]@{
    planId = "plan-mock-agentica-runtime"
    planKind = "agentica_subprocess_runtime_mock"
    behaviorId = $behaviorId
    source = "mock_agentica_planner"
    arguments = @{ failedAction = $request.context.failedReceipt.action }
    steps = @(
      @{ action = "emitStatus"; arguments = @{ message = "Runtime replan observed a blocked command and stopped safely." } },
      @{ action = "completeObjective"; arguments = @{ artifactKind = "turtlequest.objective_completed" } }
    )
    validation = @{
      valid = $true
      commandCount = 2
      commandBudget = 16
      errors = @()
      warnings = @()
    }
  }

  $plan | ConvertTo-Json -Depth 32
  exit 0
}

if ($attempt -eq 1 -and ($InvalidFirst -or $env:TURTLEQUEST_MOCK_PLANNER_INVALID_FIRST -eq '1')) {
  $plan = [ordered]@{
    planId = "plan-mock-agentica-invalid"
    planKind = "agentica_subprocess_mock"
    behaviorId = $behaviorId
    source = "mock_agentica_planner"
    arguments = @{}
    steps = @(
      @{ action = "startBehavior"; arguments = @{} },
      @{ action = "digDown"; arguments = @{} }
    )
    validation = @{
      valid = $false
      commandCount = 2
      commandBudget = $commandBudget
      errors = @()
      warnings = @()
    }
  }

  $plan | ConvertTo-Json -Depth 32
  exit 0
}

$steps = New-Object System.Collections.Generic.List[object]
$steps.Add(@{ action = "startBehavior"; arguments = @{ behaviorId = $behaviorId } })
for ($row = 0; $row -lt 5; $row++) {
  for ($column = 0; $column -lt 5; $column++) {
    $steps.Add(@{ action = "inspectDown"; arguments = @{} })
    $steps.Add(@{ action = "digDown"; arguments = @{} })
    if ($column -lt 4) {
      $steps.Add(@{ action = "moveForward"; arguments = @{} })
    }
  }

  if ($row -lt 4) {
    $turn = if (($row % 2) -eq 0) { "turnRight" } else { "turnLeft" }
    $steps.Add(@{ action = $turn; arguments = @{} })
    $steps.Add(@{ action = "moveForward"; arguments = @{} })
    $steps.Add(@{ action = $turn; arguments = @{} })
  }
}
$steps.Add(@{ action = "completeObjective"; arguments = @{ artifactKind = "turtlequest.objective_completed" } })

$plan = [ordered]@{
  planId = "plan-mock-agentica"
  planKind = "agentica_subprocess_mock"
  behaviorId = $behaviorId
  source = "mock_agentica_planner"
  arguments = @{}
  steps = $steps
  validation = @{
    valid = $true
    commandCount = $steps.Count
    commandBudget = $commandBudget
    errors = @()
    warnings = @()
  }
}

$plan | ConvertTo-Json -Depth 32
