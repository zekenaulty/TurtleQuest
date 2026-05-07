using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentica;
using Agentica.Artifacts;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Runs;
using Agentica.Tools;
using Agentica.Validation;

var options = PlannerHostOptions.Parse(args);
LoadEnvironmentFile(options.EnvFilePath ?? DefaultAgenticaEnvFile());

var stdin = await Console.In.ReadToEndAsync().ConfigureAwait(false);
if (string.IsNullOrWhiteSpace(stdin))
{
    Console.Error.WriteLine("No planner request JSON was provided on stdin.");
    return 2;
}

JsonNode requestNode;
try
{
    requestNode = JsonNode.Parse(stdin) ?? throw new JsonException("Root JSON value was null.");
}
catch (JsonException exception)
{
    Console.Error.WriteLine($"Planner request JSON was invalid: {exception.Message}");
    return 2;
}

var contextNode = requestNode["context"];
var isRuntimeReplan = contextNode?["failedReceipt"] is not null;
var goal = requestNode["goal"]?.GetValue<string>() ?? string.Empty;
var attempt = requestNode["attempt"]?.GetValue<int>() ?? 1;
var modelId = options.ModelId
    ?? Environment.GetEnvironmentVariable("TURTLEQUEST_LLM_MODEL")
    ?? GeminiModelId.Flash25;

if (!GeminiCredentialsAvailable())
{
    Console.Error.WriteLine("Gemini credentials are not configured. Set GEMINI_API_KEY or GOOGLE_API_KEY in the process environment or TURTLEQUEST_LLM_ENV_FILE.");
    return 2;
}

try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
    var llmClient = new RetryingLlmClient(
        new GeminiLlmClient(GeminiClientOptions.FromEnvironment(modelId)),
        new LlmRetryOptions(
            MaxAttempts: 2,
            CallTimeout: TimeSpan.FromSeconds(options.TimeoutSeconds)));

    var planner = new LlmWorkflowPlanner(
        llmClient,
        new LlmPlannerOptions(
            ModelId: modelId,
            GenerationOptions: new LlmGenerationOptions(
                Temperature: 0,
                MaxOutputTokens: options.MaxOutputTokens,
                Thinking: LlmThinkingOptions.Off())));

    var requestContext = BuildRunContext(requestNode, isRuntimeReplan, attempt, modelId);
    var plannerTrace = PlannerHostTrace.Create(requestContext);
    var toolSession = new TurtleQuestPlannerToolSession
    {
        RequiresReceipts = isRuntimeReplan
    };
    plannerTrace.Write("planner_host.started", new
    {
        Goal = goal,
        IsRuntimeReplan = isRuntimeReplan,
        Attempt = attempt,
        ModelId = modelId,
        BehaviorId = requestContext.GetValueOrDefault("behaviorId")
    });

    var runner = new AgenticaRunner(
        planner,
        TurtleQuestPlannerTools.CreateCatalog(requestContext, plannerTrace, toolSession),
        new TracingEventSink(plannerTrace),
        new PlannerHostOutcomeReporter(),
        new ExecutionPolicy(
            MaxSteps: 8,
            MaxRefinements: 6,
            Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
            PlanningMode: PlanningMode.Stepwise,
            MaxPlanContinuations: 0,
            MaxBlockedRetries: 0,
            MaxBatchSize: 2,
            MaxParallelism: 2,
            AllowReadOnlyParallelBatches: true),
        EvidenceCompletionEvaluator.ForArtifactKind(EmitCompiledPlanTool.ArtifactKind, continueWhenMissing: false));

    var objective = BuildObjective(goal, isRuntimeReplan);
    var envelope = await runner.RunAsync(
        new RunRequest(objective, RequestOrigin.User, requestContext),
        timeout.Token).ConfigureAwait(false);

    var artifact = envelope.Details.Artifacts.LastOrDefault(item =>
        string.Equals(item.Kind, EmitCompiledPlanTool.ArtifactKind, StringComparison.Ordinal));
    if (artifact is null)
    {
        plannerTrace.Write("planner_host.no_compiled_plan", new
        {
            envelope.Outcome.Status,
            envelope.Outcome.StopReason,
            envelope.Outcome.Blockers,
            ValidationIssues = envelope.Details.ValidationIssues.Select(issue => new
            {
                issue.Code,
                issue.Message
            }).ToArray(),
            Receipts = envelope.Receipts.Items.Select(receipt => new
            {
                receipt.ReceiptId,
                receipt.StepId,
                receipt.ToolId,
                receipt.Status,
                receipt.Message,
                receipt.Data
            }).ToArray(),
            Observations = envelope.Details.Observations.Select(observation => new
            {
                observation.ObservationId,
                observation.StepId,
                observation.Kind,
                observation.Summary,
                observation.Data
            }).ToArray()
        });
        Console.Error.WriteLine($"Agentica did not emit a TurtleCompiledPlan artifact. status={envelope.Outcome.Status}; stopReason={envelope.Outcome.StopReason}");
        foreach (var issue in envelope.Details.ValidationIssues)
        {
            Console.Error.WriteLine($"validation: {issue.Code}: {issue.Message}");
        }

        foreach (var blocker in envelope.Outcome.Blockers)
        {
            Console.Error.WriteLine($"blocker: {blocker}");
        }

        return 1;
    }

    var plan = TurtleCompiledPlan.FromPayload(artifact.Payload, isRuntimeReplan);
    plannerTrace.Write("planner_host.final_plan", new
    {
        plan.PlanId,
        plan.PlanKind,
        plan.BehaviorId,
        StepCount = plan.Steps.Count,
        plan.Validation.Valid,
        plan.Validation.Errors,
        plan.Validation.Warnings
    });
    Console.WriteLine(JsonSerializer.Serialize(plan, JsonOptions.WriteIndented));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Agentica TurtleQuest planner failed: {exception.Message}");
    return 1;
}

static Dictionary<string, object?> BuildRunContext(JsonNode requestNode, bool isRuntimeReplan, int attempt, string modelId)
{
    var context = requestNode["context"];
    var supportedActions = ExtractStringArray(context?["supportedPrimitiveActions"]);
    var executionRules = ExtractStringArray(context?["executionRules"]);
    var commandBudget = isRuntimeReplan
        ? context?["run"]?["behavior"]?["commandBudget"]?.GetValue<int?>()
        : context?["preview"]?["commandBudget"]?.GetValue<int?>();
    var behaviorId = isRuntimeReplan
        ? context?["run"]?["behavior"]?["behaviorId"]?.GetValue<string>()
        : context?["preview"]?["behaviorId"]?.GetValue<string>();
    var behaviorArguments = isRuntimeReplan
        ? ToObject(context?["run"]?["behavior"]?["arguments"])
        : ToObject(context?["preview"]?["arguments"]);
    var rawRequest = ToObject(requestNode);

    return new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["contract"] = "TurtleAgenticaPlannerCommandRequest",
        ["mode"] = isRuntimeReplan ? "runtime_replan" : "initial_plan",
        ["attempt"] = attempt,
        ["modelId"] = modelId,
        ["goal"] = requestNode["goal"]?.GetValue<string>(),
        ["behaviorId"] = behaviorId,
        ["behaviorArguments"] = behaviorArguments,
        ["commandBudget"] = commandBudget,
        ["supportedPrimitiveActions"] = supportedActions,
        ["executionRules"] = executionRules,
        ["runtimeFailure"] = isRuntimeReplan ? ToObject(context?["failedReceipt"]) : null,
        ["behaviorSpecificRules"] = BehaviorSpecificRules(behaviorId, isRuntimeReplan),
        ["behaviorToolSurface"] = BehaviorToolSurface(),
        ["environmentProfile"] = ToObject(context?["environmentProfile"]),
        ["routeMemory"] = ToObject(context?["routeMemory"]),
        ["previousRepairAttempts"] = ToObject(requestNode["previousRepairAttempts"]),
        ["rawTurtleQuestPlannerRequest"] = rawRequest,
        ["compiledPlanRequiredShape"] = new Dictionary<string, object?>
        {
            ["planId"] = "string",
            ["planKind"] = isRuntimeReplan ? "agentica_runner_runtime_replan" : "agentica_runner_llm",
            ["behaviorId"] = behaviorId == "turtlequest.open_goal" ? "chosen TurtleQuest behavior id" : behaviorId ?? "unknown",
            ["source"] = "agentica_runner",
            ["arguments"] = "object",
            ["steps"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["action"] = "one supported primitive action",
                    ["arguments"] = "object"
                }
            },
            ["validation"] = new Dictionary<string, object?>
            {
                ["valid"] = true,
                ["commandCount"] = "steps.length",
                ["commandBudget"] = commandBudget ?? 256,
                ["errors"] = Array.Empty<string>(),
                ["warnings"] = Array.Empty<string>()
            }
        }
    };
}

static string[] BehaviorSpecificRules(string? behaviorId, bool isRuntimeReplan) =>
    behaviorId switch
    {
        "turtlequest.open_goal" =>
        [
            "Open goal mode: Agentica must infer the user's intent from goal text and choose the appropriate turtlequest.behavior.* tool.",
            "Do not preserve behaviorId=turtlequest.open_goal in the final plan. The final plan behaviorId must be the selected executable behavior.",
            "Extract useful arguments from natural language, including counts such as requested tree count, dimensions, depth, height, radius, and return-home intent.",
            "If the request asks to recover, rescue, descend, or unstick a turtle, prefer turtlequest.behavior.recover_turtle.",
            "If the request asks to gather, collect, fetch, or obtain wood/logs/trees, prefer turtlequest.behavior.harvest_tree and pass targetCount when the user gives a number.",
            "If the request asks for branch mining, side branches, or repeated mining branches, prefer turtlequest.behavior.branch_mine_pattern.",
            "If the request asks to dig, mine, or make a forward tunnel or mineshaft, prefer turtlequest.behavior.tunnel_line and pass length, height, and returnHome."
        ],
        "turtlequest.branch_tunnel" =>
        [
            "For branch_tunnel, use markWaypoint then the host-owned branchTunnel primitive once.",
            "Pass side, length, height, routeId, and returnToOrigin=true.",
            "Do not expand branch_tunnel into raw turn/tunnel/return commands; the host owns returning to the branch origin and restoring facing."
        ],
        "turtlequest.branch_mine_pattern" =>
        [
            "For branch_mine_pattern, use the host-owned branchMinePattern primitive once.",
            "Pass mainLength, branchLength, branchCount, spacing, height, sidePattern, mainRouteId, and returnHome.",
            "Include getInventory before and after so inventory pressure and mined material deltas are visible.",
            "Do not emit deposit_inventory yet; storage deposit remains a later tool unless the user explicitly asks to stop when inventory pressure appears."
        ],
        "turtlequest.tunnel_line" =>
        [
            "For tunnel_line, use the host-owned tunnelLine primitive once with length and height.",
            "Player-traversable tunnel_line height must be at least environmentProfile.minimumPlayerTunnelHeight. Use environmentProfile.defaultTunnelHeight when the user does not specify height.",
            "Use getInventory before and after tunnelLine so inventory pressure can be detected from receipts.",
            "Use emitStatus stages planning, tunneling, inventory_pressure_check, returning, and tunnel_line_returned/tunnel_line_completed.",
            "Use returnHome with mode=breadcrumbs when returnHome is true.",
            "Do not expand tunnel_line into repeated raw dig/move primitives; the host owns the safe repeated tunnel loop and receipt summary."
        ],
        "turtlequest.build_column" =>
        [
            "For build_column, repeat inspectUp, moveUp, then placeDown once per requested height layer.",
            "Do not use placeUp for build_column because placing above the turtle can block the next moveUp.",
            "Use selectSlot before placement when a slot argument is available.",
            "Use exactly height moveUp steps and exactly height placeDown steps."
        ],
        "turtlequest.excavate_rectangular_pit" =>
        [
            "For excavate_rectangular_pit, use a bounded serpentine footprint traversal.",
            "Use digDown for each footprint cell that must be excavated.",
            "Use turns only to move between rows; do not use unsupported loop or macro nodes."
        ],
        "turtlequest.find_tree" =>
        [
            "For find_tree, gather evidence only: startBehavior, scanNearby for minecraft:logs within the requested radius, emitStatus, completeObjective.",
            "Do not move or dig during find_tree. Tree harvesting is a later behavior after pathing to a target is available.",
            "Use scanNearby arguments radius, tag=minecraft:logs, blockContains=_log, and maxMatches."
        ],
        "turtlequest.harvest_tree" =>
            isRuntimeReplan
                ?
                [
                    "Runtime replan for harvest_tree: inspect receipts and the failed action before selecting a continuation.",
                    "If the failed action is moveTowardRelative, fellRememberedTree, moveDown, returnHome, or a receipt indicates obstruction, canopy, elevation, or path loss, first choose turtlequest.behavior.recover_turtle.",
                    "For the first recovery-aware slice, prefer a conservative continuation: recoverToGround, optional returnHome only if breadcrumbs are available, emitStatus stage=harvest_recovered_or_stopped, completeObjective.",
                    "Do not continue scanning or felling from an elevated/bad position until recoverToGround has produced a success receipt.",
                    "Do not use raw movement or raw dig commands as the recovery continuation."
                ]
                :
                [
                    "For harvest_tree, scanNearby for minecraft:logs, use moveTowardRelative with source=lastScanNearest and stopAdjacent=true, then use digRememberedTarget once to cut the base log.",
                    "After the base log cut, call fellRememberedTree with maxHeight=12 so the host executes the vertical trunk felling against authoritative Minecraft state.",
                    "After felling, call getInventory, then returnHome with mode=breadcrumbs before completing.",
                    "If behaviorArguments.targetCount is greater than 1, repeat scanNearby, moveTowardRelative, digRememberedTarget, fellRememberedTree, getInventory, returnHome, and emitStatus once per requested tree, then emit one final completeObjective.",
                    "Do not use raw dig, digUp, or digDown for harvest_tree target cutting.",
                    "Finish with emitStatus stage=full_tree_felled_returned and completeObjective."
                ],
        "turtlequest.recover_turtle" =>
        [
            "For recover_turtle, use recoverToGround with maxDown and digSoftBelow=true, then optionally returnHome if the user asked to return and a breadcrumb path exists.",
            "Do not use raw dig, digDown, moveDown, or moveForward for recovery; the host-owned recoverToGround primitive owns safe descent.",
            "Finish with emitStatus stage=recovered_to_ground and completeObjective."
        ],
        _ => []
    };

static string BuildObjective(string goal, bool isRuntimeReplan)
{
    var prefix = isRuntimeReplan
        ? "Replan the remaining TurtleQuest execution after a failed command."
        : "Use TurtleQuest behavior tools to choose and expand a bounded behavior, then compile the user's goal into a flattened TurtleCompiledPlan.";
    return
        $"""
        {prefix}

        User goal:
        {goal}

        First call turtlequest.get_context to inspect the public TurtleQuest planning surface.
        For runtime replans, also call turtlequest.get_receipts before choosing a continuation.
        Behaviors are the primary skill surface. For known behavior classes, first call the matching turtlequest.behavior.* tool to inspect its allowed host-owned transition and recommended primitive steps.
        In open goal mode, Agentica chooses the behavior tool and its arguments from the user's goal; do not treat the preview behaviorId as authoritative.
        For runtime replans after movement, felling, or return failures, prefer recovery behavior before attempting more mission work from a possibly invalid position.
        After observing the behavior tool result, call turtlequest.emit_compiled_plan.
        Put the complete TurtleCompiledPlan JSON object in that tool input's plan field.
        The compiled plan must contain flattened primitive steps only.
        Do not return behavior tree, loop, repeat, macro, branch, Lua, Java, markdown, or explanation text as the artifact.
        Respect supportedPrimitiveActions, executionRules, behaviorId, behaviorArguments, commandBudget, and any previous repair attempts from request context.
        For initial plans, the first compiled step must be startBehavior and the final compiled step must be completeObjective.
        For runtime replans, omit startBehavior and make the final compiled step completeObjective.
        """;
}

static IReadOnlyList<object> BehaviorToolSurface() =>
[
    new
    {
        toolId = TurtleQuestBehaviorTool.ToolIdFindTree,
        behaviorId = "turtlequest.find_tree",
        purpose = "Locate nearby log-like blocks without moving or digging."
    },
    new
    {
        toolId = TurtleQuestBehaviorTool.ToolIdHarvestTree,
        behaviorId = "turtlequest.harvest_tree",
        purpose = "Scan, approach, fell a remembered tree trunk, collect inventory evidence, and return home."
    },
    new
    {
        toolId = TurtleQuestBehaviorTool.ToolIdTunnelLine,
        behaviorId = "turtlequest.tunnel_line",
        purpose = "Dig a bounded forward tunnel line with host-owned progress, route, inventory-pressure, and optional return receipts.",
        parameters = new
        {
            length = "1..64, default 6",
            height = "1..2, default 2; use 2 for player-traversable tunnels",
            returnHome = "bool, default true"
        }
    },
    new
    {
        toolId = TurtleQuestBehaviorTool.ToolIdBranchTunnel,
        behaviorId = "turtlequest.branch_tunnel",
        purpose = "Dig one side branch, return to the branch origin, and preserve route evidence.",
        parameters = new { side = "left|right", length = "1..64, default 6", height = "1..2, default 2" }
    },
    new
    {
        toolId = TurtleQuestBehaviorTool.ToolIdBranchMinePattern,
        behaviorId = "turtlequest.branch_mine_pattern",
        purpose = "Dig a bounded main tunnel with repeated side branches and route/inventory evidence.",
        parameters = new { mainLength = "1..64, default 9", branchLength = "1..64, default 6", branchCount = "1..8, default 2", spacing = "1..16, default 3" }
    },
    new
    {
        toolId = TurtleQuestBehaviorTool.ToolIdBuildColumn,
        behaviorId = "turtlequest.build_column",
        purpose = "Build a simple vertical column from turtle inventory."
    },
    new
    {
        toolId = TurtleQuestBehaviorTool.ToolIdExcavatePit,
        behaviorId = "turtlequest.excavate_rectangular_pit",
        purpose = "Excavate a shallow rectangular footprint with deterministic dig-down receipts."
    },
    new
    {
        toolId = TurtleQuestBehaviorTool.ToolIdRecoverTurtle,
        behaviorId = "turtlequest.recover_turtle",
        purpose = "Safely descend a stuck elevated turtle to solid support and optionally attempt breadcrumb return."
    }
];

static string? DefaultAgenticaEnvFile()
{
    var configured = Environment.GetEnvironmentVariable("TURTLEQUEST_LLM_ENV_FILE");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured;
    }

    var defaultPath = @"C:\Users\Zythis\source\repos\Agentica\.env";
    return File.Exists(defaultPath) ? defaultPath : null;
}

static void LoadEnvironmentFile(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        return;
    }

    foreach (var rawLine in File.ReadLines(path))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var equals = line.IndexOf('=');
        if (equals <= 0)
        {
            continue;
        }

        var key = line[..equals].Trim();
        var value = line[(equals + 1)..].Trim();
        if ((value.StartsWith('"') && value.EndsWith('"')) ||
            (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        if (!string.IsNullOrWhiteSpace(key) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

static bool GeminiCredentialsAvailable()
{
    if (string.Equals(
        Environment.GetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI"),
        "true",
        StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY"));
}

static object? ToObject(JsonNode? node)
{
    if (node is null)
    {
        return null;
    }

    return JsonSerializer.Deserialize<object?>(node.ToJsonString(JsonOptions.Compact), JsonOptions.Compact);
}

static string[] ExtractStringArray(JsonNode? node)
{
    if (node is not JsonArray array)
    {
        return [];
    }

    return array
        .Select(item => item?.GetValue<string>())
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Cast<string>()
        .ToArray();
}

public sealed record PlannerHostOptions(
    string? ModelId,
    string? EnvFilePath,
    int TimeoutSeconds,
    int MaxOutputTokens)
{
    public static PlannerHostOptions Parse(IReadOnlyList<string> args)
    {
        string? modelId = null;
        string? envFilePath = null;
        var timeoutSeconds = 180;
        var maxOutputTokens = 8192;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--model" when index + 1 < args.Count:
                    modelId = args[++index];
                    break;
                case "--env-file" when index + 1 < args.Count:
                    envFilePath = args[++index];
                    break;
                case "--timeout-seconds" when index + 1 < args.Count && int.TryParse(args[++index], out var timeout):
                    timeoutSeconds = Math.Max(10, timeout);
                    break;
                case "--max-output-tokens" when index + 1 < args.Count && int.TryParse(args[++index], out var tokens):
                    maxOutputTokens = Math.Max(512, tokens);
                    break;
            }
        }

        return new PlannerHostOptions(modelId, envFilePath, timeoutSeconds, maxOutputTokens);
    }
}

public sealed class TracingEventSink : IEventSink
{
    private readonly PlannerHostTrace _trace;

    public TracingEventSink(PlannerHostTrace trace)
    {
        _trace = trace;
    }

    public void Emit(ExecutionEvent executionEvent)
    {
        _trace.Write("agentica.event", new
        {
            executionEvent.EventId,
            executionEvent.Type,
            executionEvent.At,
            executionEvent.Data
        });
    }
}

public sealed class PlannerHostOutcomeReporter : IOutcomeReporter
{
    public OutcomeReport BuildReport(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<ValidationIssue> validationIssues,
        IReadOnlyList<string> blockers)
    {
        var artifact = run.Artifacts.LastOrDefault(item =>
            string.Equals(item.Kind, EmitCompiledPlanTool.ArtifactKind, StringComparison.Ordinal));
        var claims = new List<ReportClaim>();
        if (artifact is not null)
        {
            claims.Add(new ReportClaim(
                "Agentica emitted a TurtleQuest compiled plan artifact.",
                [new EvidenceRef("artifact", artifact.ArtifactId)]));
        }

        foreach (var issue in validationIssues)
        {
            claims.Add(new ReportClaim(
                $"Plan validation issue: {issue.Code}.",
                [new EvidenceRef("validationIssue", issue.Code)]));
        }

        if (claims.Count == 0)
        {
            claims.Add(new ReportClaim(
                $"The planner host stopped with status {status}.",
                [new EvidenceRef("stopReason", stopReason.ToString())]));
        }

        return new OutcomeReport(
            AgenticaIds.New("report"),
            artifact is null
                ? $"Agentica TurtleQuest planner stopped with {status}."
                : "Agentica TurtleQuest planner emitted a compiled plan.",
            claims);
    }
}

public static class TurtleQuestPlannerTools
{
    public static ToolCatalog CreateCatalog(
        IReadOnlyDictionary<string, object?> context,
        PlannerHostTrace trace,
        TurtleQuestPlannerToolSession session)
    {
        var behaviorTool = new TurtleQuestBehaviorTool(trace, session);
        var contextTool = new TurtleQuestContextTool(context, trace, session);
        return ToolCatalog.Create(
            TurtleQuestContextTool.Registration(
                TurtleQuestContextTool.ToolIdGetContext,
                "TurtleQuest Get Context",
                "Returns the public TurtleQuest planner context, supported actions, behavior preview, budgets, and runtime failure if present.",
                contextTool),
            TurtleQuestContextTool.Registration(
                TurtleQuestContextTool.ToolIdGetReceipts,
                "TurtleQuest Get Receipts",
                "Returns receipt/run evidence from the current TurtleQuest planning context when available.",
                contextTool),
            TurtleQuestBehaviorTool.Registration(
                TurtleQuestBehaviorTool.ToolIdFindTree,
                "TurtleQuest Behavior Find Tree",
                "Expands the find_tree behavior into receipt-backed scan primitives.",
                behaviorTool),
            TurtleQuestBehaviorTool.Registration(
                TurtleQuestBehaviorTool.ToolIdHarvestTree,
                "TurtleQuest Behavior Harvest Tree",
                "Expands the harvest_tree behavior into scan, approach, host-owned felling, inventory evidence, and breadcrumb return primitives.",
                behaviorTool),
            TurtleQuestBehaviorTool.Registration(
                TurtleQuestBehaviorTool.ToolIdTunnelLine,
                "TurtleQuest Behavior Tunnel Line",
                "Expands the tunnel_line behavior into host-owned tunneling, inventory-pressure evidence, status, and optional breadcrumb return primitives.",
                behaviorTool),
            TurtleQuestBehaviorTool.Registration(
                TurtleQuestBehaviorTool.ToolIdBranchTunnel,
                "TurtleQuest Behavior Branch Tunnel",
                "Expands one side branch into a host-owned branch tunnel primitive with origin return.",
                behaviorTool),
            TurtleQuestBehaviorTool.Registration(
                TurtleQuestBehaviorTool.ToolIdBranchMinePattern,
                "TurtleQuest Behavior Branch Mine Pattern",
                "Expands a bounded branch mine pattern into host-owned main tunnel and side branch execution.",
                behaviorTool),
            TurtleQuestBehaviorTool.Registration(
                TurtleQuestBehaviorTool.ToolIdBuildColumn,
                "TurtleQuest Behavior Build Column",
                "Expands the build_column behavior into slot selection, inventory check, upward movement, and placement primitives.",
                behaviorTool),
            TurtleQuestBehaviorTool.Registration(
                TurtleQuestBehaviorTool.ToolIdExcavatePit,
                "TurtleQuest Behavior Excavate Pit",
                "Expands the shallow rectangular pit behavior into bounded serpentine dig-down primitives.",
                behaviorTool),
            TurtleQuestBehaviorTool.Registration(
                TurtleQuestBehaviorTool.ToolIdRecoverTurtle,
                "TurtleQuest Behavior Recover Turtle",
                "Expands the recovery behavior into a host-owned safe descent primitive and optional breadcrumb return.",
                behaviorTool),
            EmitCompiledPlanTool.Registration(new EmitCompiledPlanTool(trace, session)));
    }
}

public sealed class TurtleQuestPlannerToolSession
{
    public bool ContextObserved { get; set; }

    public bool RequiresReceipts { get; set; }

    public bool ReceiptsObserved { get; set; }

    public bool BehaviorObserved { get; set; }

    public string? LastBehaviorId { get; set; }
}

public sealed class TurtleQuestContextTool : ITool
{
    public const string ToolIdGetContext = "turtlequest.get_context";
    public const string ToolIdGetReceipts = "turtlequest.get_receipts";

    private readonly IReadOnlyDictionary<string, object?> _context;
    private readonly PlannerHostTrace _trace;
    private readonly TurtleQuestPlannerToolSession _session;

    public TurtleQuestContextTool(
        IReadOnlyDictionary<string, object?> context,
        PlannerHostTrace trace,
        TurtleQuestPlannerToolSession session)
    {
        _context = context;
        _trace = trace;
        _session = session;
    }

    public static ToolRegistration Registration(
        string toolId,
        string name,
        string description,
        TurtleQuestContextTool tool) =>
        new(
            new ToolDescriptor(
                toolId,
                name,
                ToolKind.Query,
                ToolEffect.ReadOnly,
                RequiresApproval: false,
                InputSchema: ToolInputSchema.Create(),
                Description: description),
            tool);

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = invocation.ToolId switch
        {
            ToolIdGetContext => ContextProjection(),
            ToolIdGetReceipts => ReceiptProjection(),
            _ => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error"] = $"Unsupported TurtleQuest context tool {invocation.ToolId}."
            }
        };

        var status = data.ContainsKey("error") ? ReceiptStatus.Refused : ReceiptStatus.Succeeded;
        if (status == ReceiptStatus.Succeeded && invocation.ToolId == ToolIdGetContext)
        {
            _session.ContextObserved = true;
        }
        else if (status == ReceiptStatus.Succeeded && invocation.ToolId == ToolIdGetReceipts)
        {
            _session.ReceiptsObserved = true;
        }
        var message = invocation.ToolId == ToolIdGetReceipts
            ? "TurtleQuest receipt evidence observed."
            : "TurtleQuest planner context observed.";
        var receipt = new Receipt(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            status,
            message,
            DateTimeOffset.UtcNow,
            data);
        var observation = new Observation(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.StateQuery,
            message,
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        _trace.Write("agentica.tool_result", new
        {
            invocation.StepId,
            invocation.ToolId,
            Input = invocation.Input,
            ReceiptId = receipt.ReceiptId,
            ReceiptStatus = receipt.Status.ToString(),
            ObservationId = observation.ObservationId,
            observation.Summary,
            Data = data
        });
        return Task.FromResult(new ToolResult(receipt, observation));
    }

    private Dictionary<string, object?> ContextProjection()
    {
        var projection = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = Get("mode"),
            ["goal"] = Get("goal"),
            ["behaviorId"] = Get("behaviorId"),
            ["behaviorArguments"] = Get("behaviorArguments"),
            ["commandBudget"] = Get("commandBudget"),
            ["environmentProfile"] = Get("environmentProfile"),
            ["routeMemory"] = Get("routeMemory"),
            ["supportedPrimitiveActions"] = Get("supportedPrimitiveActions"),
            ["executionRules"] = Get("executionRules"),
            ["behaviorToolSurface"] = Get("behaviorToolSurface"),
            ["behaviorSpecificRules"] = Get("behaviorSpecificRules"),
            ["runtimeFailure"] = Get("runtimeFailure")
        };

        return projection;
    }

    private Dictionary<string, object?> ReceiptProjection()
    {
        var rawRequest = Get("rawTurtleQuestPlannerRequest");
        var receipts = ExtractPath(rawRequest, "context", "run", "receipts")
            ?? ExtractPath(rawRequest, "context", "run", "Receipts")
            ?? Array.Empty<object>();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = Get("mode"),
            ["runtimeFailure"] = Get("runtimeFailure"),
            ["receipts"] = receipts,
            ["pendingCommandCount"] = ExtractPath(rawRequest, "context", "pendingCommandCount")
                ?? ExtractPath(rawRequest, "context", "PendingCommandCount")
        };
    }

    private object? Get(string key) =>
        _context.TryGetValue(key, out var value) ? value : null;

    private static object? ExtractPath(object? value, params string[] path)
    {
        var node = JsonSerializer.SerializeToNode(value, JsonOptions.Compact);
        foreach (var segment in path)
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(segment, out node))
            {
                return null;
            }
        }

        return node is null
            ? null
            : JsonSerializer.Deserialize<object?>(node.ToJsonString(JsonOptions.Compact), JsonOptions.Compact);
    }
}

public sealed class TurtleQuestBehaviorTool : ITool
{
    public const string ToolIdFindTree = "turtlequest.behavior.find_tree";
    public const string ToolIdHarvestTree = "turtlequest.behavior.harvest_tree";
    public const string ToolIdTunnelLine = "turtlequest.behavior.tunnel_line";
    public const string ToolIdBranchTunnel = "turtlequest.behavior.branch_tunnel";
    public const string ToolIdBranchMinePattern = "turtlequest.behavior.branch_mine_pattern";
    public const string ToolIdBuildColumn = "turtlequest.behavior.build_column";
    public const string ToolIdExcavatePit = "turtlequest.behavior.excavate_rectangular_pit";
    public const string ToolIdRecoverTurtle = "turtlequest.behavior.recover_turtle";

    private readonly PlannerHostTrace _trace;
    private readonly TurtleQuestPlannerToolSession _session;

    public TurtleQuestBehaviorTool(PlannerHostTrace trace, TurtleQuestPlannerToolSession session)
    {
        _trace = trace;
        _session = session;
    }

    public static ToolRegistration Registration(
        string toolId,
        string name,
        string description,
        TurtleQuestBehaviorTool tool) =>
        new(
            new ToolDescriptor(
                toolId,
                name,
                ToolKind.PlannerAssist,
                ToolEffect.ReadOnly,
                RequiresApproval: false,
                InputSchema: ToolInputSchema.Create(
                    new ToolInputField(
                        "arguments",
                        ToolInputValueType.Object,
                        Required: false,
                        Description: "Behavior arguments inferred from the goal, such as targetCount/count, radius, pathBudget, width, length, depth, height, slot, and returnHome.")),
                Description: description),
            tool);

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.ContextObserved)
        {
            return Task.FromResult(Refused(
                invocation,
                "context_required",
                "Call turtlequest.get_context before selecting a TurtleQuest behavior tool."));
        }

        if (_session.RequiresReceipts && !_session.ReceiptsObserved)
        {
            return Task.FromResult(Refused(
                invocation,
                "receipts_required",
                "Call turtlequest.get_receipts before selecting a runtime replan behavior tool."));
        }

        var behavior = BehaviorForTool(invocation.ToolId);
        if (behavior is null)
        {
            return Task.FromResult(Refused(invocation, "unsupported_behavior_tool", $"Unsupported TurtleQuest behavior tool {invocation.ToolId}."));
        }

        var arguments = ReadArguments(invocation);
        var steps = StepsFor(behavior, arguments);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["behaviorId"] = behavior,
            ["arguments"] = arguments,
            ["recommendedPrimitiveSteps"] = steps.Select(step => new Dictionary<string, object?>
            {
                ["action"] = step.Action,
                ["arguments"] = step.Arguments
            }).ToArray(),
            ["transitionContract"] = TransitionContractFor(behavior),
            ["completionRule"] = "Emit a TurtleCompiledPlan artifact using turtlequest.emit_compiled_plan after adapting these recommended primitive steps to the current request context."
        };

        var receipt = new Receipt(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            ReceiptStatus.Succeeded,
            $"TurtleQuest behavior tool expanded {behavior}.",
            DateTimeOffset.UtcNow,
            data);
        var observation = new Observation(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.ToolResult,
            $"Behavior {behavior} expanded into {steps.Count} primitive step(s).",
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        _session.BehaviorObserved = true;
        _session.LastBehaviorId = behavior;
        _trace.Write("agentica.tool_result", new
        {
            invocation.StepId,
            invocation.ToolId,
            Input = invocation.Input,
            ReceiptId = receipt.ReceiptId,
            ReceiptStatus = receipt.Status.ToString(),
            ObservationId = observation.ObservationId,
            observation.Summary,
            BehaviorId = behavior,
            RecommendedStepCount = steps.Count,
            Data = data
        });
        return Task.FromResult(new ToolResult(receipt, observation));
    }

    private static ToolResult Refused(ToolInvocation invocation, string reason, string message)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reason"] = reason
        };
        var receipt = new Receipt(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            ReceiptStatus.Refused,
            message,
            DateTimeOffset.UtcNow,
            data);
        var observation = new Observation(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.ToolResult,
            message,
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return new ToolResult(receipt, observation);
    }

    private static string? BehaviorForTool(string toolId) =>
        toolId switch
        {
            ToolIdFindTree => "turtlequest.find_tree",
            ToolIdHarvestTree => "turtlequest.harvest_tree",
            ToolIdTunnelLine => "turtlequest.tunnel_line",
            ToolIdBranchTunnel => "turtlequest.branch_tunnel",
            ToolIdBranchMinePattern => "turtlequest.branch_mine_pattern",
            ToolIdBuildColumn => "turtlequest.build_column",
            ToolIdExcavatePit => "turtlequest.excavate_rectangular_pit",
            ToolIdRecoverTurtle => "turtlequest.recover_turtle",
            _ => null
        };

    private static IReadOnlyDictionary<string, object?> ReadArguments(ToolInvocation invocation)
    {
        if (!invocation.Input.TryGetValue("arguments", out var value) || value is null)
        {
            return new Dictionary<string, object?>();
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                element.GetRawText(),
                JsonOptions.Compact) ?? new Dictionary<string, object?>();
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            return readOnly;
        }

        if (value is Dictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        var json = JsonSerializer.Serialize(value, JsonOptions.Compact);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions.Compact)
            ?? new Dictionary<string, object?>();
    }

    private static IReadOnlyList<TurtleCompiledPlanStep> StepsFor(
        string behaviorId,
        IReadOnlyDictionary<string, object?> arguments) =>
        behaviorId switch
        {
            "turtlequest.find_tree" => FindTreeSteps(arguments),
            "turtlequest.harvest_tree" => HarvestTreeSteps(arguments),
            "turtlequest.tunnel_line" => TunnelLineSteps(arguments),
            "turtlequest.branch_tunnel" => BranchTunnelSteps(arguments),
            "turtlequest.branch_mine_pattern" => BranchMinePatternSteps(arguments),
            "turtlequest.build_column" => BuildColumnSteps(arguments),
            "turtlequest.excavate_rectangular_pit" => ExcavatePitSteps(arguments),
            "turtlequest.recover_turtle" => RecoverTurtleSteps(arguments),
            _ => []
        };

    private static IReadOnlyList<TurtleCompiledPlanStep> FindTreeSteps(IReadOnlyDictionary<string, object?> arguments)
    {
        var radius = ClampArgument(arguments, "radius", 12, 1, 16);
        return
        [
            Step("startBehavior", ("behaviorId", "turtlequest.find_tree"), ("arguments", arguments)),
            Step("scanNearby", ("radius", radius), ("tag", "minecraft:logs"), ("blockContains", "_log"), ("maxMatches", 16)),
            Step("emitStatus", ("stage", "scan_complete"), ("behaviorId", "turtlequest.find_tree"), ("target", "minecraft:logs")),
            Step("completeObjective", ("artifactKind", "turtlequest.objective_completed"))
        ];
    }

    private static IReadOnlyList<TurtleCompiledPlanStep> HarvestTreeSteps(IReadOnlyDictionary<string, object?> arguments)
    {
        var radius = ClampArgument(arguments, "radius", 12, 1, 16);
        var pathBudget = ClampArgument(arguments, "pathBudget", radius, 1, 32);
        var targetCount = ClampArgument(arguments, "targetCount", 0, 1, 8);
        if (targetCount == 0)
        {
            targetCount = ClampArgument(arguments, "count", 0, 1, 8);
        }

        if (targetCount == 0)
        {
            targetCount = ClampArgument(arguments, "trees", 1, 1, 8);
        }
        var steps = new List<TurtleCompiledPlanStep>
        {
            Step("startBehavior", ("behaviorId", "turtlequest.harvest_tree"), ("arguments", arguments))
        };

        for (var i = 0; i < targetCount; i++)
        {
            steps.Add(Step("scanNearby", ("radius", radius), ("tag", "minecraft:logs"), ("blockContains", "_log"), ("maxMatches", 16)));
            steps.Add(Step("moveTowardRelative", ("source", "lastScanNearest"), ("budget", pathBudget), ("stopAdjacent", true)));
            steps.Add(Step("digRememberedTarget", ("source", "lastScanNearest"), ("expectedTag", "minecraft:logs")));
            steps.Add(Step("fellRememberedTree", ("maxHeight", 12), ("expectedTag", "minecraft:logs")));
            steps.Add(Step("getInventory"));
            steps.Add(Step("returnHome", ("mode", "breadcrumbs")));
            steps.Add(Step(
                "emitStatus",
                ("stage", i + 1 == targetCount ? "full_tree_felled_returned" : "tree_felled_returned"),
                ("behaviorId", "turtlequest.harvest_tree"),
                ("target", "minecraft:logs"),
                ("completedTrees", i + 1),
                ("targetTrees", targetCount)));
        }

        steps.Add(Step(
            "completeObjective",
            ("artifactKind", "turtlequest.objective_completed"),
            ("stage", "full_tree_felled_returned"),
            ("targetTrees", targetCount)));
        return steps;
    }

    private static IReadOnlyList<TurtleCompiledPlanStep> TunnelLineSteps(IReadOnlyDictionary<string, object?> arguments)
    {
        var length = ClampArgument(arguments, "length", 6, 1, 64);
        var height = ClampArgument(arguments, "height", 2, 1, 2);
        var returnHome = BoolArgument(arguments, "returnHome", true);
        var routeId = StringArgument(arguments, "routeId", $"route-{Guid.NewGuid():N}");
        var steps = new List<TurtleCompiledPlanStep>
        {
            Step("startBehavior", ("behaviorId", "turtlequest.tunnel_line"), ("arguments", arguments)),
            Step("emitStatus", ("stage", "planning"), ("behaviorId", "turtlequest.tunnel_line")),
            Step("getInventory"),
            Step("emitStatus", ("stage", "tunneling"), ("behaviorId", "turtlequest.tunnel_line"), ("length", length), ("height", height)),
            Step("tunnelLine", ("length", length), ("height", height), ("routeId", routeId)),
            Step("emitStatus", ("stage", "inventory_pressure_check"), ("behaviorId", "turtlequest.tunnel_line")),
            Step("getInventory")
        };

        if (returnHome)
        {
            steps.Add(Step("emitStatus", ("stage", "returning"), ("behaviorId", "turtlequest.tunnel_line")));
            steps.Add(Step("returnHome", ("mode", "breadcrumbs")));
        }

        steps.Add(Step("emitStatus", ("stage", returnHome ? "tunnel_line_returned" : "tunnel_line_completed"), ("behaviorId", "turtlequest.tunnel_line")));
        steps.Add(Step(
            "completeObjective",
            ("artifactKind", "turtlequest.objective_completed"),
            ("stage", returnHome ? "tunnel_line_returned" : "tunnel_line_completed")));
        return steps;
    }

    private static IReadOnlyList<TurtleCompiledPlanStep> BranchTunnelSteps(IReadOnlyDictionary<string, object?> arguments)
    {
        var side = StringArgument(arguments, "side", "left");
        var length = ClampArgument(arguments, "length", 6, 1, 64);
        var height = ClampArgument(arguments, "height", 2, 1, 2);
        var routeId = StringArgument(arguments, "routeId", $"route-branch-{Guid.NewGuid():N}");
        return
        [
            Step("startBehavior", ("behaviorId", "turtlequest.branch_tunnel"), ("arguments", arguments)),
            Step("markWaypoint", ("name", "branch_origin")),
            Step("emitStatus", ("stage", "branch_started"), ("behaviorId", "turtlequest.branch_tunnel"), ("side", side), ("length", length)),
            Step("branchTunnel", ("side", side), ("length", length), ("height", height), ("routeId", routeId), ("returnToOrigin", true)),
            Step("emitStatus", ("stage", "branch_returned_to_origin"), ("behaviorId", "turtlequest.branch_tunnel")),
            Step("completeObjective", ("artifactKind", "turtlequest.objective_completed"), ("stage", "branch_returned_to_origin"))
        ];
    }

    private static IReadOnlyList<TurtleCompiledPlanStep> BranchMinePatternSteps(IReadOnlyDictionary<string, object?> arguments)
    {
        var mainLength = ClampArgument(arguments, "mainLength", 9, 1, 64);
        var branchLength = ClampArgument(arguments, "branchLength", 6, 1, 64);
        var branchCount = ClampArgument(arguments, "branchCount", 2, 1, 8);
        var spacing = ClampArgument(arguments, "spacing", 3, 1, 16);
        var height = ClampArgument(arguments, "height", 2, 1, 2);
        var returnHome = BoolArgument(arguments, "returnHome", true);
        var mainRouteId = StringArgument(arguments, "mainRouteId", $"route-main-{Guid.NewGuid():N}");
        return
        [
            Step("startBehavior", ("behaviorId", "turtlequest.branch_mine_pattern"), ("arguments", arguments)),
            Step("markWaypoint", ("name", "branch_mine_start")),
            Step("getInventory"),
            Step("emitStatus", ("stage", "branch_mine_started"), ("behaviorId", "turtlequest.branch_mine_pattern"), ("mainLength", mainLength), ("branchLength", branchLength), ("branchCount", branchCount)),
            Step("branchMinePattern", ("mainLength", mainLength), ("branchLength", branchLength), ("branchCount", branchCount), ("spacing", spacing), ("height", height), ("sidePattern", StringArgument(arguments, "sidePattern", "alternating")), ("mainRouteId", mainRouteId), ("returnHome", returnHome)),
            Step("getInventory"),
            Step("emitStatus", ("stage", "branch_mine_completed"), ("behaviorId", "turtlequest.branch_mine_pattern")),
            Step("completeObjective", ("artifactKind", "turtlequest.objective_completed"), ("stage", "branch_mine_completed"))
        ];
    }

    private static IReadOnlyList<TurtleCompiledPlanStep> BuildColumnSteps(IReadOnlyDictionary<string, object?> arguments)
    {
        var height = ClampArgument(arguments, "height", 5, 1, 16);
        var slot = ClampArgument(arguments, "slot", 1, 1, 16);
        var steps = new List<TurtleCompiledPlanStep>
        {
            Step("startBehavior", ("behaviorId", "turtlequest.build_column"), ("arguments", arguments)),
            Step("selectSlot", ("slot", slot)),
            Step("getInventory")
        };
        for (var i = 0; i < height; i++)
        {
            steps.Add(Step("inspectUp"));
            steps.Add(Step("moveUp"));
            steps.Add(Step("placeDown"));
            steps.Add(Step("emitStatus", ("stage", "work_progress"), ("behaviorId", "turtlequest.build_column"), ("completedLayers", i + 1), ("totalLayers", height)));
        }

        steps.Add(Step("completeObjective", ("artifactKind", "turtlequest.objective_completed")));
        return steps;
    }

    private static IReadOnlyList<TurtleCompiledPlanStep> ExcavatePitSteps(IReadOnlyDictionary<string, object?> arguments)
    {
        var width = ClampArgument(arguments, "width", 5, 1, 7);
        var length = ClampArgument(arguments, "length", 5, 1, 7);
        var steps = new List<TurtleCompiledPlanStep>
        {
            Step("startBehavior", ("behaviorId", "turtlequest.excavate_rectangular_pit"), ("arguments", arguments))
        };

        for (var row = 0; row < length; row++)
        {
            for (var column = 0; column < width; column++)
            {
                steps.Add(Step("inspectDown"));
                steps.Add(Step("digDown"));
                if (column < width - 1)
                {
                    steps.Add(Step("moveForward"));
                }
            }

            if (row < length - 1)
            {
                var turn = row % 2 == 0 ? "turnRight" : "turnLeft";
                steps.Add(Step(turn));
                steps.Add(Step("moveForward"));
                steps.Add(Step(turn));
            }
        }

        steps.Add(Step("completeObjective", ("artifactKind", "turtlequest.objective_completed")));
        return steps;
    }

    private static IReadOnlyList<TurtleCompiledPlanStep> RecoverTurtleSteps(IReadOnlyDictionary<string, object?> arguments)
    {
        var maxDown = ClampArgument(arguments, "maxDown", 32, 1, 64);
        var returnHome = BoolArgument(arguments, "returnHome", false);
        var steps = new List<TurtleCompiledPlanStep>
        {
            Step("startBehavior", ("behaviorId", "turtlequest.recover_turtle"), ("arguments", arguments)),
            Step("recoverToGround", ("maxDown", maxDown), ("digSoftBelow", true))
        };

        if (returnHome)
        {
            steps.Add(Step("returnHome", ("mode", "breadcrumbs"), ("optional", true)));
        }

        steps.Add(Step("emitStatus", ("stage", "recovered_to_ground"), ("behaviorId", "turtlequest.recover_turtle")));
        steps.Add(Step("completeObjective", ("artifactKind", "turtlequest.objective_completed"), ("stage", "recovered_to_ground")));
        return steps;
    }

    private static IReadOnlyList<string> TransitionContractFor(string behaviorId) =>
        behaviorId switch
        {
            "turtlequest.find_tree" =>
            [
                "Observation-only behavior.",
                "Requires at least one scanNearby receipt.",
                "Must not move or dig."
            ],
            "turtlequest.harvest_tree" =>
            [
                "Resource acquisition behavior.",
                "Requires scanNearby, approach, base log cut, fellRememberedTree, inventory evidence, and breadcrumb return.",
                "Host owns target-bound felling; the plan should not replace it with raw dig offsets."
            ],
            "turtlequest.tunnel_line" =>
            [
                "Mining route behavior.",
                "Requires one tunnelLine receipt with length, height, routeId, stepsCompleted, blocksRemoved, and inventoryPressure.",
                "If returnHome is true, requires returnHome after tunnelLine.",
                "Inventory pressure is evidence for later discardJunk, storage, or home-chest behaviors."
            ],
            "turtlequest.branch_tunnel" =>
            [
                "Branch route behavior.",
                "Requires markWaypoint and one branchTunnel receipt.",
                "Host must return to branch origin and restore original facing."
            ],
            "turtlequest.branch_mine_pattern" =>
            [
                "Branch mining behavior.",
                "Requires one branchMinePattern receipt with mainLength, branchCount, branchesCompleted, route ids, inventoryPressure, and return state.",
                "Storage/deposit is not required in this first branch mining slice; inventory pressure should be emitted as evidence."
            ],
            "turtlequest.build_column" =>
            [
                "Construction behavior.",
                "Requires exactly height moveUp and height placeDown receipts.",
                "Uses moveUp then placeDown so the turtle does not block itself."
            ],
            "turtlequest.excavate_rectangular_pit" =>
            [
                "Excavation behavior.",
                "Requires bounded serpentine traversal and digDown over the requested footprint.",
                "This slice supports depth 1 only."
            ],
            "turtlequest.recover_turtle" =>
            [
                "Recovery behavior.",
                "Requires recoverToGround receipt proving final support below is solid or the descent budget was exhausted.",
                "May attempt returnHome only after recoverToGround and only when requested."
            ],
            _ => []
        };

    private static int ClampArgument(
        IReadOnlyDictionary<string, object?> arguments,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return fallback;
        }

        var parsed = value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            double doubleValue => checked((int)doubleValue),
            decimal decimalValue => checked((int)decimalValue),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue) => intValue,
            JsonElement element when element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var intValue) => intValue,
            string stringValue when int.TryParse(stringValue, out var intValue) => intValue,
            IConvertible convertible => Convert.ToInt32(convertible),
            _ => fallback
        };

        return Math.Clamp(parsed, minimum, maximum);
    }

    private static bool BoolArgument(
        IReadOnlyDictionary<string, object?> arguments,
        string name,
        bool fallback)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            bool boolValue => boolValue,
            JsonElement element when element.ValueKind == JsonValueKind.True => true,
            JsonElement element when element.ValueKind == JsonValueKind.False => false,
            JsonElement element when element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed) => parsed,
            string stringValue when bool.TryParse(stringValue, out var parsed) => parsed,
            _ => fallback
        };
    }

    private static string StringArgument(
        IReadOnlyDictionary<string, object?> arguments,
        string name,
        string fallback)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            string stringValue when !string.IsNullOrWhiteSpace(stringValue) => stringValue,
            JsonElement element when element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()) => element.GetString()!,
            _ => fallback
        };
    }

    private static TurtleCompiledPlanStep Step(string action, params (string Key, object? Value)[] arguments) =>
        new(
            action,
            arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

public sealed class EmitCompiledPlanTool : ITool
{
    public const string ToolId = "turtlequest.emit_compiled_plan";
    public const string ArtifactKind = "turtlequest.compiled_plan";

    private readonly PlannerHostTrace _trace;
    private readonly TurtleQuestPlannerToolSession _session;

    public EmitCompiledPlanTool(PlannerHostTrace trace, TurtleQuestPlannerToolSession session)
    {
        _trace = trace;
        _session = session;
    }

    public static ToolRegistration Registration(EmitCompiledPlanTool tool) =>
        new(
            new ToolDescriptor(
                ToolId,
                "Emit TurtleQuest Compiled Plan",
                ToolKind.Synthesis,
                ToolEffect.ReadOnly,
                RequiresApproval: false,
                InputSchema: ToolInputSchema.Create(
                    new ToolInputField(
                        "plan",
                        ToolInputValueType.Object,
                        Required: true,
                        Description:
                        """
                        A complete TurtleCompiledPlan object:
                        { planId, planKind, behaviorId, source, arguments, steps: [{ action, arguments }], validation: { valid, commandCount, commandBudget, errors, warnings } }.
                        steps must be flattened primitive turtle actions only.
                        """)),
                Description: "Emits the final flattened TurtleQuest IR plan artifact for bridge validation and execution."),
            tool);

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (!_session.ContextObserved)
        {
            return Task.FromResult(Failed(invocation, "Call turtlequest.get_context before emitting a TurtleQuest compiled plan."));
        }

        if (_session.RequiresReceipts && !_session.ReceiptsObserved)
        {
            return Task.FromResult(Failed(invocation, "Call turtlequest.get_receipts before emitting a runtime continuation plan."));
        }

        if (!_session.BehaviorObserved)
        {
            return Task.FromResult(Failed(invocation, "Call a turtlequest.behavior.* tool before emitting a TurtleQuest compiled plan."));
        }

        if (!invocation.Input.TryGetValue("plan", out var value) || value is null)
        {
            return Task.FromResult(Failed(invocation, "Tool input did not include required plan object."));
        }

        var payload = NormalizePlanPayload(value);
        var receipt = new Receipt(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            ReceiptStatus.Succeeded,
            "TurtleQuest compiled plan artifact emitted.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>
            {
                ["artifactKind"] = ArtifactKind
            });
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            ArtifactKind,
            payload,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        _trace.Write("agentica.tool_result", new
        {
            invocation.StepId,
            invocation.ToolId,
            Input = invocation.Input,
            ReceiptId = receipt.ReceiptId,
            ReceiptStatus = receipt.Status.ToString(),
            ArtifactId = artifact.ArtifactId,
            ArtifactKind,
            PlanId = payload.TryGetValue("planId", out var planId) ? planId : null,
            BehaviorId = payload.TryGetValue("behaviorId", out var behaviorId) ? behaviorId : null
        });
        return Task.FromResult(new ToolResult(receipt, Artifact: artifact));
    }

    private static ToolResult Failed(ToolInvocation invocation, string message)
    {
        var receipt = new Receipt(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            ReceiptStatus.Failed,
            message,
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());
        return new ToolResult(receipt);
    }

    private static IReadOnlyDictionary<string, object?> NormalizePlanPayload(object value)
    {
        if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        if (value is Dictionary<string, object?> mutableDictionary)
        {
            return mutableDictionary;
        }

        if (value is JsonElement element)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                element.GetRawText(),
                JsonOptions.Compact) ?? new Dictionary<string, object?>();
        }

        var json = JsonSerializer.Serialize(value, JsonOptions.Compact);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions.Compact)
            ?? new Dictionary<string, object?>();
    }
}

public sealed record TurtleCompiledPlan(
    string PlanId,
    string PlanKind,
    string BehaviorId,
    string Source,
    IReadOnlyDictionary<string, object?> Arguments,
    IReadOnlyList<TurtleCompiledPlanStep> Steps,
    TurtleCompiledPlanValidation Validation)
{
    public static TurtleCompiledPlan FromPayload(IReadOnlyDictionary<string, object?> payload, bool isRuntimeReplan)
    {
        var node = JsonSerializer.SerializeToNode(payload, JsonOptions.Compact)
            ?? throw new InvalidOperationException("Compiled plan payload was empty.");
        var steps = ReadSteps(node["steps"]);
        var commandBudget = node["validation"]?["commandBudget"]?.GetValue<int?>()
            ?? node["commandBudget"]?.GetValue<int?>()
            ?? 256;
        var validation = new TurtleCompiledPlanValidation(
            Valid: node["validation"]?["valid"]?.GetValue<bool?>() ?? true,
            CommandCount: steps.Count,
            CommandBudget: commandBudget,
            Errors: ReadStringList(node["validation"]?["errors"]),
            Warnings: ReadStringList(node["validation"]?["warnings"]));

        return new TurtleCompiledPlan(
            PlanId: ReadString(node["planId"], $"plan-agentica-{Guid.NewGuid():N}"),
            PlanKind: ReadString(node["planKind"], isRuntimeReplan ? "agentica_runner_runtime_replan" : "agentica_runner_llm"),
            BehaviorId: ReadString(node["behaviorId"], "unknown"),
            Source: "agentica_runner",
            Arguments: ReadObjectDictionary(node["arguments"]),
            Steps: steps,
            Validation: validation);
    }

    private static string ReadString(JsonNode? node, string fallback) =>
        node is null ? fallback : node.GetValue<string>() ?? fallback;

    private static IReadOnlyDictionary<string, object?> ReadObjectDictionary(JsonNode? node)
    {
        if (node is null)
        {
            return new Dictionary<string, object?>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(
            node.ToJsonString(JsonOptions.Compact),
            JsonOptions.Compact) ?? new Dictionary<string, object?>();
    }

    private static IReadOnlyList<TurtleCompiledPlanStep> ReadSteps(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .OfType<JsonObject>()
            .Select(item => new TurtleCompiledPlanStep(
                ReadString(item["action"], string.Empty),
                ReadObjectDictionary(item["arguments"])))
            .Where(item => !string.IsNullOrWhiteSpace(item.Action))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadStringList(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(item => item?.GetValue<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }
}

public sealed record TurtleCompiledPlanStep(
    string Action,
    IReadOnlyDictionary<string, object?> Arguments);

public sealed record TurtleCompiledPlanValidation(
    bool Valid,
    int CommandCount,
    int CommandBudget,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed class PlannerHostTrace
{
    private static readonly object Gate = new();

    private readonly string _scopeId;
    private readonly string _path;

    private PlannerHostTrace(string scopeId, string path)
    {
        _scopeId = scopeId;
        _path = path;
    }

    public static PlannerHostTrace Create(IReadOnlyDictionary<string, object?> context)
    {
        var traceId = $"agentica-planner-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}"[..51];
        var behaviorId = Convert.ToString(context.GetValueOrDefault("behaviorId")) ?? "unknown";
        var safeBehavior = Safe(behaviorId.Replace("turtlequest.", "", StringComparison.Ordinal));
        var scope = $"{traceId}-{safeBehavior}";
        var root = Environment.GetEnvironmentVariable("TURTLEQUEST_TRACE_DIR") ??
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "run", "traces"));
        var directory = Path.Combine(root, "planner-host", scope);
        Directory.CreateDirectory(directory);
        return new PlannerHostTrace(scope, Path.Combine(directory, "events.jsonl"));
    }

    public void Write(string eventType, object payload)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("TURTLEQUEST_TRACE_ENABLED"), "false", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var line = JsonSerializer.Serialize(new
            {
                observedAt = DateTimeOffset.UtcNow,
                scopeId = _scopeId,
                eventType,
                payload
            }, JsonOptions.Compact);

            lock (Gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Planner tracing must not change planning behavior.
        }
    }

    private static string Safe(string value)
    {
        var chars = value
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            .ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}

public static class JsonOptions
{
    public static JsonSerializerOptions Compact { get; } = Create(writeIndented: false);

    public static JsonSerializerOptions WriteIndented { get; } = Create(writeIndented: true);

    private static JsonSerializerOptions Create(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
