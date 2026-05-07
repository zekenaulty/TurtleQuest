# Copy the relevant lines into your local shell before starting the bridge.
# Do not commit real keys.

# The primary planner host references Agentica and Agentica.Clients in-process.
# Agentica.Clients.Gemini reads GEMINI_API_KEY first, then GOOGLE_API_KEY.
$env:TURTLEQUEST_LLM_ENV_FILE = "C:\Users\Zythis\source\repos\Agentica\.env"
$env:TURTLEQUEST_LLM_MODEL = "gemini-2.5-flash"

$env:AGENTICA_TURTLEQUEST_PLANNER_COMMAND = "dotnet"
$env:AGENTICA_TURTLEQUEST_PLANNER_ARGS = "run --project `"C:\Users\Zythis\source\repos\Agentica.TurtleQuest\planner\Agentica.TurtleQuest.AgenticaPlanner\Agentica.TurtleQuest.AgenticaPlanner.csproj`" --no-restore --"
$env:AGENTICA_TURTLEQUEST_PLANNER_CWD = "C:\Users\Zythis\source\repos\Agentica.TurtleQuest"
$env:AGENTICA_TURTLEQUEST_PLANNER_TIMEOUT_SECONDS = "240"

$env:TURTLEQUEST_USE_PLANNER_FOR_PROMPTS = "true"
$env:TURTLEQUEST_DEFAULT_PLANNER_MODE = "agentica"
$env:TURTLEQUEST_DEFAULT_REPAIR_ATTEMPTS = "1"

# Optional runtime recovery after a failed turtle command receipt.
$env:TURTLEQUEST_AUTO_REPLAN_ON_BLOCKED = "true"
$env:TURTLEQUEST_RUNTIME_REPLAN_MODE = "agentica"
$env:TURTLEQUEST_RUNTIME_REPLAN_ATTEMPTS = "1"

# Optional trace location. Defaults to run/traces.
# $env:TURTLEQUEST_TRACE_DIR = "C:\Users\Zythis\source\repos\Agentica.TurtleQuest\run\traces"
