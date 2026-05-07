using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

TurtleQuestBridgeConfiguration.LoadEnvironmentFiles();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(TurtleQuestBridgeConfiguration.BridgeUrl);
var app = builder.Build();

var runs = new ConcurrentDictionary<string, TurtleRunState>();
var sessions = new ConcurrentDictionary<string, TurtleQuestSession>();
var snapshots = new ConcurrentDictionary<string, WorldFragmentSnapshot>();
var routeMemory = TurtleRouteMemory.Load(Path.Combine("run", "data", "routes.json"));
var behaviorCatalog = TurtleBehaviorCatalog.Load();
var boardCatalog = TurtleQuestBoardCatalog.Load();
var cookbook = TurtleQuestCookbook.Load();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TurtleQuest.Bridge" }));

app.MapGet("/behaviors", () => Results.Ok(behaviorCatalog.Snapshot()));

app.MapGet("/cookbook", () => Results.Ok(cookbook.Snapshot()));

app.MapGet("/boards", () => Results.Ok(boardCatalog.Snapshot()));

app.MapGet("/routes", () => Results.Ok(routeMemory.Snapshot()));

app.MapGet("/boards/{boardId}", (string boardId) =>
{
    return boardCatalog.TryGetBoard(boardId, out var board)
        ? Results.Ok(board)
        : Results.NotFound(new { error = "Board not found." });
});

app.MapPost("/snapshots", (WorldFragmentSnapshot snapshot) =>
{
    snapshots[snapshot.SnapshotId] = snapshot;
    return Results.Accepted($"/snapshots/{snapshot.SnapshotId}", new
    {
        snapshot.SnapshotId,
        snapshot.WorldId,
        BlockCount = snapshot.Blocks.Count
    });
});

app.MapGet("/snapshots/{snapshotId}", (string snapshotId) =>
{
    return snapshots.TryGetValue(snapshotId, out var snapshot)
        ? Results.Ok(snapshot)
        : Results.NotFound(new { error = "Snapshot not found." });
});

app.MapPost("/snapshots/diff", (WorldFragmentDiffRequest request) =>
{
    if (!snapshots.TryGetValue(request.BeforeSnapshotId, out var before))
    {
        return Results.NotFound(new { error = "Before snapshot not found." });
    }

    if (!snapshots.TryGetValue(request.AfterSnapshotId, out var after))
    {
        return Results.NotFound(new { error = "After snapshot not found." });
    }

    return Results.Ok(WorldFragmentDiff.Create(before, after));
});

app.MapPost("/planner/preview", (TurtleUserRequest request) =>
{
    var behavior = TurtleBehaviorMatcher.Match(request.Message);
    var executable = behaviorCatalog.Contains(behavior.BehaviorId) ||
        TurtlePlanCompiler.IsKnownCompiledBehavior(behavior.BehaviorId);
    var warnings = new List<string>();
    if (!executable)
    {
        warnings.Add($"No catalog-backed executor exists for {behavior.BehaviorId}.");
    }

    if (behavior.Arguments.TryGetValue("missing", out var missing) && missing is not null)
    {
        warnings.Add($"Missing required parameter(s): {missing}.");
    }

    return Results.Ok(new TurtlePlanPreview(
        behavior.BehaviorId,
        executable ? "catalog" : "not_executable",
        behavior.Arguments,
        executable,
        warnings));
});

app.MapPost("/planner/compile", (TurtleUserRequest request) =>
{
    var behavior = TurtleBehaviorMatcher.Match(request.Message);
    var plan = TurtlePlanCompiler.Compile(request, behavior, behaviorCatalog);
    return Results.Ok(plan);
});

app.MapPost("/planner/context", (TurtleUserRequest request) =>
{
    var behavior = TurtleBehaviorMatcher.Match(request.Message);
    var executable = behaviorCatalog.Contains(behavior.BehaviorId) ||
        TurtlePlanCompiler.IsKnownCompiledBehavior(behavior.BehaviorId);
    var warnings = new List<string>();
    if (!executable)
    {
        warnings.Add($"No deterministic executor exists for {behavior.BehaviorId}; compile flattened IR or ask a clarifying question.");
    }

    if (behavior.Arguments.TryGetValue("missing", out var missing) && missing is not null)
    {
        warnings.Add($"Missing required parameter(s): {missing}.");
    }

    return Results.Ok(new TurtlePlannerContext(
        request,
        new TurtlePlanPreview(
            behavior.BehaviorId,
            executable ? "known_or_compilable" : "requires_planning",
            behavior.Arguments,
            executable,
            warnings),
        TurtlePlanCompiler.SupportedPrimitiveActions,
        cookbook.Snapshot(),
        TurtleEnvironmentProfile.Default,
        routeMemory.Snapshot(),
        TurtlePlanCompiler.ExecutionRules));
});

app.MapPost("/planner/generate", async (TurtlePlannerGenerateRequest request) =>
{
    var generated = await TurtlePlanner.GenerateAsync(request, behaviorCatalog, cookbook, routeMemory.Snapshot());
    TurtleQuestTrace.WritePlanner("planner.generate", new
    {
        request.Mode,
        request.Execute,
        RequestedRepairAttempts = request.RepairAttempts,
        request.Request.TurtleId,
        request.Request.WorldId,
        Goal = request.Request.Message,
        generated.Plan.PlanId,
        generated.Plan.PlanKind,
        generated.Plan.BehaviorId,
        generated.Validation.Valid,
        generated.Validation.Errors,
        PlannerRepairAttempts = generated.RepairAttempts
    });

    if (request.Execute && generated.Validation.Valid)
    {
        var runId = $"tq-{Guid.NewGuid():N}";
        var state = TurtleRunState.CreateFromPlan(runId, request.Request, generated.Plan, generated.Validation);
        runs[runId] = state;
        TurtleQuestTrace.WriteRun(runId, "run.created_from_generated_plan", new
        {
            request.Request,
            generated.Plan,
            generated.Validation
        });
        var handle = new AgenticaRunHandle(
            runId,
            request.Request.TurtleId,
            state.Status,
            state.Behavior.BehaviorRunId,
            state.Behavior.BehaviorId);
        generated = generated with { Run = handle };
    }

    return Results.Ok(generated);
});

app.MapPost("/planner/validate", (TurtlePlanValidationRequest request) =>
{
    var validation = TurtlePlanCompiler.ValidatePlan(request.Steps, request.CommandBudget);
    return Results.Ok(new TurtlePlanValidationResult(
        validation,
        TurtlePlanCompiler.SupportedPrimitiveActions));
});

app.MapPost("/runs/from-plan", (CreateRunFromPlanRequest request) =>
{
    var commandBudget = request.Plan.Validation.CommandBudget > 0
        ? request.Plan.Validation.CommandBudget
        : request.Plan.Steps.Count;
    var validation = TurtlePlanCompiler.ValidateCompiledPlan(request.Plan, commandBudget);
    if (!validation.Valid)
    {
        return Results.BadRequest(new TurtlePlanValidationResult(
            validation,
            TurtlePlanCompiler.SupportedPrimitiveActions));
    }

    var runId = $"tq-{Guid.NewGuid():N}";
    var state = TurtleRunState.CreateFromPlan(runId, request.Request, request.Plan, validation);
    runs[runId] = state;
    TurtleQuestTrace.WriteRun(runId, "run.created_from_plan", new
    {
        request.Request,
        request.Plan,
        Validation = validation
    });

    return Results.Accepted(
        $"/runs/{runId}",
        new AgenticaRunHandle(
            runId,
            request.Request.TurtleId,
            state.Status,
            state.Behavior.BehaviorRunId,
            state.Behavior.BehaviorId));
});

app.MapPost("/runs/{runId}/continue-from-plan", (string runId, ContinueRunFromPlanRequest request) =>
{
    if (!runs.TryGetValue(runId, out var run))
    {
        return Results.NotFound(new { error = "Run not found." });
    }

    if (!string.Equals(runId, request.RunId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "Route run id must match request run id." });
    }

    var commandBudget = request.Plan.Validation.CommandBudget > 0
        ? request.Plan.Validation.CommandBudget
        : request.Plan.Steps.Count;
    var validation = TurtlePlanCompiler.ValidateContinuationPlan(request.Plan.Steps, commandBudget);
    if (!validation.Valid)
    {
        return Results.BadRequest(new TurtlePlanValidationResult(
            validation,
            TurtlePlanCompiler.SupportedPrimitiveActions));
    }

    if (!run.TryAppendContinuation(request.Plan, validation, out var error))
    {
        TurtleQuestTrace.WriteRun(runId, "run.continuation_rejected", new
        {
            Error = error,
            request.Reason,
            request.Plan,
            Validation = validation,
            Snapshot = run.Snapshot()
        });
        return Results.BadRequest(new { error });
    }

    TurtleQuestTrace.WriteRun(runId, "run.continuation_applied", new
    {
        request.Reason,
        request.Plan,
        Validation = validation,
        Snapshot = run.Snapshot()
    });

    return Results.Accepted(
        $"/runs/{runId}",
        new ContinueRunFromPlanResult(
            runId,
            run.Status,
            run.PendingCommands,
            validation,
            request.Plan.PlanId));
});

app.MapPost("/runs/{runId}/replan", async (string runId, TurtleRuntimeReplanRequest request) =>
{
    if (!runs.TryGetValue(runId, out var run))
    {
        return Results.NotFound(new { error = "Run not found." });
    }

    if (!run.TryBuildRuntimeReplanContext(out var context, out var contextError))
    {
        TurtleQuestTrace.WriteRun(runId, "run.replan_context_rejected", new
        {
            Error = contextError,
            Request = request,
            Snapshot = run.Snapshot()
        });
        return Results.BadRequest(new { error = contextError });
    }

    TurtleQuestTrace.WriteRun(runId, "run.replan_context_built", new
    {
        Request = request,
        Context = context
    });

    var mode = string.IsNullOrWhiteSpace(request.Mode)
        ? "agentica"
        : request.Mode.Trim().ToLowerInvariant();
    var generated = await TurtlePlanner.GenerateRuntimeContinuationAsync(
        mode,
        context,
        Math.Max(0, request.RepairAttempts));

    if (!generated.Validation.Valid)
    {
        TurtleQuestTrace.WriteRun(runId, "run.replan_invalid", generated);
        return Results.BadRequest(generated);
    }

    if (!run.TryAppendContinuation(generated.Plan, generated.Validation, out var appendError))
    {
        TurtleQuestTrace.WriteRun(runId, "run.replan_append_rejected", new
        {
            Error = appendError,
            Generated = generated,
            Snapshot = run.Snapshot()
        });
        return Results.BadRequest(new { error = appendError, generated });
    }

    var continuation = new ContinueRunFromPlanResult(
        runId,
        run.Status,
        run.PendingCommands,
        generated.Validation,
        generated.Plan.PlanId);

    TurtleQuestTrace.WriteRun(runId, "run.replan_applied", generated with
    {
        Applied = true,
        Continuation = continuation
    });

    return Results.Accepted($"/runs/{runId}", generated with
    {
        Applied = true,
        Continuation = continuation
    });
});

app.MapPost("/turtles/{turtleId}/messages", async (string turtleId, TurtleUserRequest request) =>
{
    if (!string.Equals(turtleId, request.TurtleId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "Route turtle id must match request turtle id." });
    }

    if (TurtleQuestBridgeConfiguration.UsePlannerForPromptMessages)
    {
        var plannedRunId = $"tq-{Guid.NewGuid():N}";
        var planningState = TurtleRunState.CreatePlanning(plannedRunId, request);
        runs[plannedRunId] = planningState;
        TurtleQuestTrace.WriteRun(plannedRunId, "run.planning_started", new
        {
            request,
            Snapshot = planningState.Snapshot(),
            PlannerMode = TurtleQuestBridgeConfiguration.DefaultPlannerMode,
            FallbackMode = TurtleQuestBridgeConfiguration.FallbackPlannerMode
        });

        _ = Task.Run(async () =>
        {
            try
            {
                TurtleQuestTrace.WriteRun(plannedRunId, "run.planner_background_started", new
                {
                    request,
                    TurtleQuestBridgeConfiguration.DefaultPlannerMode
                });

        var plannerRequest = new TurtlePlannerGenerateRequest(
            TurtleQuestBridgeConfiguration.DefaultPlannerMode,
            request,
            Execute: false,
            RepairAttempts: TurtleQuestBridgeConfiguration.DefaultRepairAttempts);
        var generated = await TurtlePlanner.GenerateAsync(plannerRequest, behaviorCatalog, cookbook, routeMemory.Snapshot());

        if (!generated.Validation.Valid &&
            !string.Equals(TurtleQuestBridgeConfiguration.DefaultPlannerMode, TurtleQuestBridgeConfiguration.FallbackPlannerMode, StringComparison.OrdinalIgnoreCase))
        {
            var fallbackRequest = plannerRequest with { Mode = TurtleQuestBridgeConfiguration.FallbackPlannerMode };
            generated = await TurtlePlanner.GenerateAsync(fallbackRequest, behaviorCatalog, cookbook, routeMemory.Snapshot());
        }

        if (!generated.Validation.Valid)
        {
                    planningState.FailPlanning(
                        "Planner did not produce a valid TurtleQuest plan.",
                        generated.Validation.Errors,
                        generated.Validation.Warnings);
                    TurtleQuestTrace.WriteRun(plannedRunId, "run.planning_failed", new
            {
                generated.Mode,
                generated.Validation.Errors,
                        generated.Validation.Warnings,
                        Snapshot = planningState.Snapshot()
            });
                    return;
        }

                planningState.ApplyPlan(generated.Plan, generated.Validation);
        TurtleQuestTrace.WriteRun(plannedRunId, "run.created_from_prompt_planner", new
        {
            request,
            generated.Plan,
            generated.Validation,
                    generated.RepairAttempts,
                    Snapshot = planningState.Snapshot()
        });
            }
            catch (Exception exception)
            {
                planningState.FailPlanning(
                    "Planner background task failed.",
                    [exception.Message],
                    []);
                TurtleQuestTrace.WriteRun(plannedRunId, "run.planning_exception", new
                {
                    Exception = exception.ToString(),
                    Snapshot = planningState.Snapshot()
                });
            }
        });

        return Results.Accepted(
            $"/runs/{plannedRunId}",
            new AgenticaRunHandle(
                plannedRunId,
                turtleId,
                planningState.Status,
                planningState.Behavior.BehaviorRunId,
                planningState.Behavior.BehaviorId));
    }

    var runId = $"tq-{Guid.NewGuid():N}";
    var state = TurtleRunState.Create(runId, request, behaviorCatalog);
    runs[runId] = state;
    TurtleQuestTrace.WriteRun(runId, "run.created_from_prompt_catalog", new
    {
        request,
        Snapshot = state.Snapshot()
    });

    return Results.Accepted(
        $"/runs/{runId}",
        new AgenticaRunHandle(
            runId,
            turtleId,
            state.Status,
            state.Behavior.BehaviorRunId,
            state.Behavior.BehaviorId));
});

app.MapPost("/sessions", (CreateTurtleQuestSessionRequest request) =>
{
    if (!boardCatalog.TryGetQuest(request.BoardId, request.QuestId, out var board, out var quest))
    {
        return Results.NotFound(new { error = "Quest not found." });
    }

    var runId = $"tq-{Guid.NewGuid():N}";
    var turtleRequest = request.Request with { Message = quest.Prompt };
    var run = TurtleRunState.Create(runId, turtleRequest, behaviorCatalog);
    runs[runId] = run;
    TurtleQuestTrace.WriteRun(runId, "run.created_from_session", new
    {
        request.BoardId,
        request.QuestId,
        TurtleRequest = turtleRequest,
        Snapshot = run.Snapshot()
    });

    var session = new TurtleQuestSession(
        $"tqs-{Guid.NewGuid():N}",
        board.BoardId,
        quest.QuestId,
        runId,
        turtleRequest.TurtleId,
        DateTimeOffset.UtcNow,
        "running",
        quest.Title,
        quest.Prompt);
    sessions[session.SessionId] = session;

    return Results.Accepted($"/sessions/{session.SessionId}", session);
});

app.MapGet("/sessions/{sessionId}", (string sessionId) =>
{
    if (!sessions.TryGetValue(sessionId, out var session))
    {
        return Results.NotFound(new { error = "Session not found." });
    }

    TurtleRunSnapshot? run = null;
    if (runs.TryGetValue(session.RunId, out var runState))
    {
        run = runState.Snapshot();
        session = session with { Status = run.Status };
        sessions[sessionId] = session;
    }

    return Results.Ok(new TurtleQuestSessionSnapshot(session, run));
});

app.MapPost("/sessions/{sessionId}/evaluate", (string sessionId, TurtleQuestEvaluationRequest request) =>
{
    if (!sessions.TryGetValue(sessionId, out var session))
    {
        return Results.NotFound(new { error = "Session not found." });
    }

    if (!boardCatalog.TryGetQuest(session.BoardId, session.QuestId, out _, out var quest))
    {
        return Results.NotFound(new { error = "Quest not found." });
    }

    if (!runs.TryGetValue(session.RunId, out var runState))
    {
        return Results.NotFound(new { error = "Run not found." });
    }

    WorldFragmentDiff? diff = null;
    if (!string.IsNullOrWhiteSpace(request.BeforeSnapshotId) && !string.IsNullOrWhiteSpace(request.AfterSnapshotId))
    {
        if (!snapshots.TryGetValue(request.BeforeSnapshotId, out var before))
        {
            return Results.NotFound(new { error = "Before snapshot not found." });
        }

        if (!snapshots.TryGetValue(request.AfterSnapshotId, out var after))
        {
            return Results.NotFound(new { error = "After snapshot not found." });
        }

        diff = WorldFragmentDiff.Create(before, after);
    }

    return Results.Ok(TurtleQuestOutcomeEvaluator.Evaluate(session, quest, runState.Snapshot(), diff));
});

app.MapGet("/runs/{runId}", (string runId) =>
{
    return runs.TryGetValue(runId, out var run)
        ? Results.Ok(run.Snapshot())
        : Results.NotFound(new { error = "Run not found." });
});

app.MapGet("/runs/{runId}/trace", (string runId) =>
{
    return TurtleQuestTrace.TryReadRunTrace(runId, out var trace)
        ? Results.Text(trace, "application/x-ndjson")
        : Results.NotFound(new { error = "Trace not found." });
});

app.MapGet("/runs/{runId}/next-command", (string runId) =>
{
    if (!runs.TryGetValue(runId, out var run))
    {
        return Results.NotFound(new { error = "Run not found." });
    }

    if (string.Equals(run.Status, "planning", StringComparison.Ordinal))
    {
        TurtleQuestTrace.WriteRun(runId, "run.next_command.waiting_for_plan", new
        {
            Command = (object?)null,
            Snapshot = run.Snapshot()
        });
        return Results.Accepted($"/runs/{runId}", new { runId, status = run.Status });
    }

    var command = run.NextCommand();
    TurtleQuestTrace.WriteRun(runId, command is null ? "run.next_command.none" : "run.next_command.dequeued", new
    {
        Command = command,
        Snapshot = run.Snapshot()
    });
    return command is null ? Results.NoContent() : Results.Ok(command);
});

app.MapPost("/runs/{runId}/receipts", (string runId, TurtleCommandReceipt receipt) =>
{
    if (!runs.TryGetValue(runId, out var run))
    {
        return Results.NotFound(new { error = "Run not found." });
    }

    if (!string.Equals(runId, receipt.RunId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "Route run id must match receipt run id." });
    }

    run.Record(receipt);
    routeMemory.Observe(run, receipt);
    TurtleQuestTrace.WriteRun(runId, "run.receipt_recorded", new
    {
        Receipt = receipt,
        Routes = routeMemory.Snapshot(),
        Snapshot = run.Snapshot()
    });
    return Results.Accepted($"/runs/{runId}", new { runId, receiptCount = run.Receipts.Count, status = run.Status });
});

app.MapPost("/runs/{runId}/simulate", (string runId) =>
{
    if (!runs.TryGetValue(runId, out var run))
    {
        return Results.NotFound(new { error = "Run not found." });
    }

    var receiptCountBeforeSimulation = run.Receipts.Count;
    run.SimulateBehavior();
    foreach (var receipt in run.Receipts.Skip(receiptCountBeforeSimulation))
    {
        routeMemory.Observe(run, receipt);
    }

    TurtleQuestTrace.WriteRun(runId, "run.simulated", new
    {
        Routes = routeMemory.Snapshot(),
        Snapshot = run.Snapshot()
    });
    return Results.Ok(run.Snapshot());
});

app.Run();

public sealed record AgenticaRunHandle(
    string RunId,
    string TurtleId,
    string Status,
    string BehaviorRunId,
    string BehaviorId);

public static class TurtleQuestBridgeConfiguration
{
    private const string DefaultBridgeUrl = "http://127.0.0.1:57421";
    private const string DefaultMode = "deterministic";
    private const string DefaultFallbackMode = "deterministic";

    public static string BridgeUrl =>
        Environment.GetEnvironmentVariable("TURTLEQUEST_BRIDGE_URL") ?? DefaultBridgeUrl;

    public static bool UsePlannerForPromptMessages =>
        ReadBool("TURTLEQUEST_USE_PLANNER_FOR_MESSAGES", defaultValue: true);

    public static string DefaultPlannerMode =>
        ReadPlannerMode("TURTLEQUEST_PLANNER_MODE", DefaultMode);

    public static string FallbackPlannerMode =>
        ReadPlannerMode("TURTLEQUEST_PLANNER_FALLBACK_MODE", DefaultFallbackMode);

    public static int DefaultRepairAttempts
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("TURTLEQUEST_PLANNER_REPAIR_ATTEMPTS");
            return int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : 1;
        }
    }

    public static void LoadEnvironmentFiles()
    {
        foreach (var file in CandidateEnvironmentFiles())
        {
            if (File.Exists(file))
            {
                LoadEnvironmentFile(file);
            }
        }
    }

    private static IEnumerable<string> CandidateEnvironmentFiles()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            yield return Path.Combine(current.FullName, ".env");
            yield return Path.Combine(current.FullName, ".env.local");

            if (File.Exists(Path.Combine(current.FullName, "TurtleQuest.slnx")) ||
                Directory.Exists(Path.Combine(current.FullName, "behaviors")))
            {
                yield break;
            }

            current = current.Parent;
        }
    }

    private static void LoadEnvironmentFile(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
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
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (string.IsNullOrWhiteSpace(key) || Environment.GetEnvironmentVariable(key) is not null)
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string ReadPlannerMode(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : value.Trim().ToLowerInvariant();
    }

    private static bool ReadBool(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => defaultValue
        };
    }
}

public sealed record TurtleUserRequest(
    string TurtleId,
    string WorldId,
    string? PlayerId,
    string Message,
    Position Position,
    string Orientation);

public sealed record CreateTurtleQuestSessionRequest(
    string BoardId,
    string QuestId,
    TurtleUserRequest Request);

public sealed record CreateRunFromPlanRequest(
    TurtleUserRequest Request,
    TurtleCompiledPlan Plan);

public sealed record ContinueRunFromPlanRequest(
    string RunId,
    string Reason,
    TurtleCompiledPlan Plan);

public sealed record ContinueRunFromPlanResult(
    string RunId,
    string Status,
    int PendingCommands,
    TurtleCompiledPlanValidation Validation,
    string PlanId);

public sealed record TurtleRuntimeReplanRequest(
    string Mode = "agentica",
    int RepairAttempts = 1);

public sealed record WorldFragmentSnapshot(
    string SnapshotId,
    string WorldId,
    string? TurtleId,
    Position Origin,
    FragmentSize Size,
    DateTimeOffset CapturedAt,
    Position? TurtlePosition,
    string? TurtleOrientation,
    IReadOnlyList<WorldBlockSample> Blocks);

public sealed record FragmentSize(int X, int Y, int Z);

public sealed record WorldBlockSample(int X, int Y, int Z, string Block);

public sealed record WorldFragmentDiffRequest(string BeforeSnapshotId, string AfterSnapshotId);

public sealed record TurtleQuestEvaluationRequest(string? BeforeSnapshotId, string? AfterSnapshotId);

public sealed record WorldFragmentDiff(
    string BeforeSnapshotId,
    string AfterSnapshotId,
    int ComparedBlocks,
    int ChangedBlocks,
    int AddedBlocks,
    int RemovedBlocks,
    IReadOnlyList<WorldBlockChange> Changes)
{
    public static WorldFragmentDiff Create(WorldFragmentSnapshot before, WorldFragmentSnapshot after)
    {
        var beforeBlocks = before.Blocks.ToDictionary(Key, StringComparer.Ordinal);
        var afterBlocks = after.Blocks.ToDictionary(Key, StringComparer.Ordinal);
        var keys = beforeBlocks.Keys.Concat(afterBlocks.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changes = new List<WorldBlockChange>();
        var added = 0;
        var removed = 0;

        foreach (var key in keys)
        {
            beforeBlocks.TryGetValue(key, out var beforeBlock);
            afterBlocks.TryGetValue(key, out var afterBlock);

            if (beforeBlock is null && afterBlock is not null)
            {
                added++;
                changes.Add(new WorldBlockChange(afterBlock.X, afterBlock.Y, afterBlock.Z, null, afterBlock.Block));
                continue;
            }

            if (beforeBlock is not null && afterBlock is null)
            {
                removed++;
                changes.Add(new WorldBlockChange(beforeBlock.X, beforeBlock.Y, beforeBlock.Z, beforeBlock.Block, null));
                continue;
            }

            if (beforeBlock is not null &&
                afterBlock is not null &&
                !string.Equals(beforeBlock.Block, afterBlock.Block, StringComparison.Ordinal))
            {
                changes.Add(new WorldBlockChange(afterBlock.X, afterBlock.Y, afterBlock.Z, beforeBlock.Block, afterBlock.Block));
            }
        }

        return new WorldFragmentDiff(
            before.SnapshotId,
            after.SnapshotId,
            keys.Length,
            changes.Count,
            added,
            removed,
            changes);
    }

    private static string Key(WorldBlockSample sample) => $"{sample.X},{sample.Y},{sample.Z}";
}

public sealed record WorldBlockChange(int X, int Y, int Z, string? Before, string? After);

public sealed record NextTurtleCommand(
    string RunId,
    string CommandId,
    string Action,
    IReadOnlyDictionary<string, object?> Arguments);

public sealed record TurtleCommandReceipt(
    string RunId,
    string TurtleId,
    string CommandId,
    string Action,
    bool Success,
    Position Position,
    string Orientation,
    DateTimeOffset ObservedAt,
    string? BlockAhead,
    IReadOnlyList<string> Hazards,
    IReadOnlyDictionary<string, int> InventoryDelta,
    string? Message);

public sealed record TurtleBehavior(
    string BehaviorRunId,
    string BehaviorId,
    IReadOnlyDictionary<string, object?> Arguments,
    int CommandBudget);

public sealed record TurtleWaypointRecord(
    string WaypointId,
    string TurtleId,
    string WorldId,
    Position Position,
    string Orientation,
    DateTimeOffset ObservedAt,
    string SourceRunId);

public sealed record TurtleRouteSegmentRecord(
    string RouteId,
    string TurtleId,
    string WorldId,
    Position Start,
    Position End,
    string Orientation,
    string Kind,
    string? ParentRouteId,
    string? BoundingBox,
    int? StepsCompleted,
    int? BlocksRemoved,
    string? Clearance,
    DateTimeOffset ObservedAt,
    string SourceRunId);

public sealed record TurtleRouteMemorySnapshot(
    IReadOnlyList<TurtleWaypointRecord> Waypoints,
    IReadOnlyList<TurtleRouteSegmentRecord> Routes);

public sealed class TurtleRouteMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, TurtleWaypointRecord> _waypoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TurtleRouteSegmentRecord> _routes = new(StringComparer.Ordinal);
    private readonly string _path;

    private TurtleRouteMemory(string path)
    {
        _path = path;
    }

    public static TurtleRouteMemory Load(string path)
    {
        var memory = new TurtleRouteMemory(path);
        if (!File.Exists(path))
        {
            return memory;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<TurtleRouteMemorySnapshot>(
                File.ReadAllText(path),
                JsonOptions);
            if (persisted is null)
            {
                return memory;
            }

            foreach (var waypoint in persisted.Waypoints)
            {
                memory._waypoints[waypoint.WaypointId] = waypoint;
            }

            foreach (var route in persisted.Routes)
            {
                memory._routes[route.RouteId] = route;
            }
        }
        catch (Exception exception)
        {
            TurtleQuestTrace.WritePlanner("route_memory.load_failed", new
            {
                Path = path,
                exception.Message
            });
        }

        return memory;
    }

    public TurtleRouteMemorySnapshot Snapshot() => new(
        _waypoints.Values.OrderBy(item => item.WaypointId, StringComparer.Ordinal).ToArray(),
        _routes.Values.OrderBy(item => item.RouteId, StringComparer.Ordinal).ToArray());

    public void Observe(TurtleRunState run, TurtleCommandReceipt receipt)
    {
        if (!receipt.Success)
        {
            return;
        }

        if (receipt.Action is "markWaypoint" or "placeStorage")
        {
            var waypointId = ExtractToken(receipt.Message, "waypointId") ??
                ExtractToken(receipt.Message, "name") ??
                $"waypoint-{Guid.NewGuid():N}";
            var position = ExtractPosition(receipt.Message, "storagePosition") ?? receipt.Position;
            _waypoints[waypointId] = new TurtleWaypointRecord(
                waypointId,
                receipt.TurtleId,
                run.Request.WorldId,
                position,
                receipt.Orientation,
                receipt.ObservedAt,
                receipt.RunId);
            Save();
            return;
        }

        if (receipt.Action is "tunnelLine" or "branchTunnel" or "branchMinePattern")
        {
            var routeId = ExtractToken(receipt.Message, "routeId") ??
                ExtractToken(receipt.Message, "mainRouteId") ??
                $"route-{Guid.NewGuid():N}";
            var kind = receipt.Action switch
            {
                "branchTunnel" => "branch",
                "branchMinePattern" => "branch_mine",
                _ => "tunnel"
            };
            _routes[routeId] = new TurtleRouteSegmentRecord(
                routeId,
                receipt.TurtleId,
                run.Request.WorldId,
                ExtractPosition(receipt.Message, "start") ?? run.Request.Position,
                receipt.Position,
                receipt.Orientation,
                kind,
                ExtractToken(receipt.Message, "parentRouteId"),
                ExtractToken(receipt.Message, "boundingBox"),
                ExtractInt(receipt.Message, "stepsCompleted"),
                ExtractInt(receipt.Message, "blocksRemoved"),
                ExtractToken(receipt.Message, "clearance"),
                receipt.ObservedAt,
                receipt.RunId);
            Save();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(Snapshot(), JsonOptions));
        }
        catch (Exception exception)
        {
            TurtleQuestTrace.WritePlanner("route_memory.save_failed", new
            {
                Path = _path,
                exception.Message
            });
        }
    }

    private static string? ExtractToken(string? message, string key)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var match = Regex.Match(message, $@"(?:^|[;\s]){Regex.Escape(key)}=([^;\s.]+)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static int? ExtractInt(string? message, string key) =>
        int.TryParse(ExtractToken(message, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static Position? ExtractPosition(string? message, string key)
    {
        var token = ExtractToken(message, key);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)
                ? new Position(x, y, z)
                : null;
    }
}

public sealed record TurtlePlanPreview(
    string BehaviorId,
    string PlanKind,
    IReadOnlyDictionary<string, object?> Arguments,
    bool Executable,
    IReadOnlyList<string> Warnings);

public sealed record TurtlePlannerContext(
    TurtleUserRequest Request,
    TurtlePlanPreview Preview,
    IReadOnlyList<string> SupportedPrimitiveActions,
    IReadOnlyList<object> CookbookExamples,
    TurtleEnvironmentProfile EnvironmentProfile,
    object RouteMemory,
    IReadOnlyList<string> ExecutionRules);

public sealed record TurtleEnvironmentProfile(
    int MinimumPlayerTunnelHeight,
    int DefaultTunnelHeight,
    int DefaultTunnelWidth,
    int DefaultRoomWidth,
    int DefaultRoomLength,
    int DefaultRoomHeight,
    int DefaultTorchSpacing,
    int ChunkSize,
    IReadOnlyList<object> BlueprintDefaults,
    IReadOnlyList<string> PlanningRules)
{
    public static TurtleEnvironmentProfile Default { get; } = new(
        MinimumPlayerTunnelHeight: 2,
        DefaultTunnelHeight: 2,
        DefaultTunnelWidth: 1,
        DefaultRoomWidth: 9,
        DefaultRoomLength: 9,
        DefaultRoomHeight: 9,
        DefaultTorchSpacing: 8,
        ChunkSize: 16,
        BlueprintDefaults:
        [
            new
            {
                id = "turtlequest.blueprint.tunnel_line",
                kind = "route",
                defaultSize = new { width = 1, height = 2 },
                clearance = "player_walkable"
            },
            new
            {
                id = "turtlequest.blueprint.dire_room",
                kind = "room",
                defaultSize = new { width = 9, length = 9, height = 9 },
                purpose = "general mining-base room envelope that chunk-aligns well enough"
            },
            new
            {
                id = "turtlequest.blueprint.storage_room",
                kind = "room",
                defaultSize = new { width = 9, length = 9, height = 9 },
                purpose = "home chest, barrel array, and deposit target"
            },
            new
            {
                id = "turtlequest.blueprint.crafting_room",
                kind = "room",
                defaultSize = new { width = 9, length = 9, height = 9 },
                purpose = "crafting table, furnace line, and turtle staging"
            }
        ],
        PlanningRules:
        [
            "Player-traversable tunnels must be at least two blocks high.",
            "Default mining tunnel profile is one block wide and two blocks high.",
            "Default mining-base utility rooms use a Dire-style nine by nine by nine envelope unless the user asks otherwise.",
            "A nine by nine by nine room is treated as chunk-aligned good enough for early TurtleQuest route planning.",
            "Align long-lived routes and rooms to chunk boundaries when practical, but do not sacrifice local safety or return-home reliability.",
            "Torch/light placement is a future behavior; expose spacing intent now but do not emit unsupported light placement primitives."
        ]);
}

public sealed record TurtlePlannerGenerateRequest(
    string Mode,
    TurtleUserRequest Request,
    bool Execute = false,
    int RepairAttempts = 1);

public sealed record TurtlePlannerGenerateResult(
    string Mode,
    TurtlePlannerContext Context,
    TurtleCompiledPlan Plan,
    TurtleCompiledPlanValidation Validation,
    IReadOnlyList<TurtlePlannerRepairAttempt> RepairAttempts,
    bool Executable,
    AgenticaRunHandle? Run);

public sealed record TurtlePlannerRepairAttempt(
    int Attempt,
    string Reason,
    TurtleCompiledPlanValidation Validation);

public sealed record TurtleAgenticaPlannerCommandRequest(
    string Goal,
    int Attempt,
    TurtlePlannerContext Context,
    IReadOnlyList<TurtlePlannerRepairAttempt> PreviousRepairAttempts);

public sealed record TurtleRuntimeReplanContext(
    string RunId,
    string Goal,
    TurtleRunSnapshot Run,
    TurtleCommandReceipt FailedReceipt,
    int PendingCommands,
    IReadOnlyList<string> SupportedPrimitiveActions,
    IReadOnlyList<string> ExecutionRules);

public sealed record TurtleAgenticaReplanCommandRequest(
    string Goal,
    int Attempt,
    TurtleRuntimeReplanContext Context,
    IReadOnlyList<TurtlePlannerRepairAttempt> PreviousRepairAttempts);

public sealed record TurtleRuntimeReplanResult(
    string Mode,
    TurtleRuntimeReplanContext Context,
    TurtleCompiledPlan Plan,
    TurtleCompiledPlanValidation Validation,
    IReadOnlyList<TurtlePlannerRepairAttempt> RepairAttempts,
    bool Applied,
    ContinueRunFromPlanResult? Continuation);

public sealed record TurtleCompiledPlan(
    string PlanId,
    string PlanKind,
    string BehaviorId,
    string Source,
    IReadOnlyDictionary<string, object?> Arguments,
    IReadOnlyList<TurtleCompiledPlanStep> Steps,
    TurtleCompiledPlanValidation Validation);

public sealed record TurtleCompiledPlanStep(
    string Action,
    IReadOnlyDictionary<string, object?> Arguments);

public sealed record TurtleCompiledPlanValidation(
    bool Valid,
    int CommandCount,
    int CommandBudget,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record TurtlePlanValidationRequest(
    int CommandBudget,
    IReadOnlyList<TurtleCompiledPlanStep> Steps);

public sealed record TurtlePlanValidationResult(
    TurtleCompiledPlanValidation Validation,
    IReadOnlyList<string> SupportedPrimitiveActions);

public sealed record TurtleRunSnapshot(
    string RunId,
    string TurtleId,
    string Status,
    TurtleUserRequest Request,
    TurtleBehavior Behavior,
    int PendingCommands,
    int RuntimeContinuationCount,
    IReadOnlyList<TurtleCommandReceipt> Receipts,
    TurtleCompletion? Completion);

public sealed record TurtleCompletion(
    bool Success,
    string ArtifactKind,
    string Message,
    IReadOnlyDictionary<string, object?> Evidence);

public sealed record TurtleQuestBoard(
    string BoardId,
    string Title,
    IReadOnlyList<TurtleQuestDefinition> Quests);

public sealed record TurtleQuestDefinition(
    string QuestId,
    string Title,
    string Prompt,
    string BehaviorId,
    IReadOnlyDictionary<string, object?> Arguments,
    IReadOnlyDictionary<string, object?> SuccessCriteria);

public sealed record TurtleQuestSession(
    string SessionId,
    string BoardId,
    string QuestId,
    string RunId,
    string TurtleId,
    DateTimeOffset CreatedAt,
    string Status,
    string Title,
    string Prompt);

public sealed record TurtleQuestSessionSnapshot(
    TurtleQuestSession Session,
    TurtleRunSnapshot? Run);

public sealed record TurtleQuestEvaluation(
    string SessionId,
    string QuestId,
    string Status,
    bool Passed,
    IReadOnlyList<ValidationCheck> Checks,
    TurtleQuestEvidencePackage Evidence);

public sealed record ValidationCheck(string Name, bool Passed, string Message);

public sealed record TurtleQuestEvidencePackage(
    string Goal,
    string BehaviorId,
    int ReceiptCount,
    IReadOnlyDictionary<string, int> ReceiptCounts,
    bool CompletionSuccess,
    WorldFragmentDiff? WorldDiff);

public sealed record Position(int X, int Y, int Z)
{
    public Position Step(string orientation, int distance = 1) =>
        orientation.ToLowerInvariant() switch
        {
            "north" => this with { Z = Z - distance },
            "south" => this with { Z = Z + distance },
            "west" => this with { X = X - distance },
            "east" => this with { X = X + distance },
            _ => this
        };
}

public sealed class TurtleRunState
{
    private const int MaxRuntimeContinuations = 1;
    private readonly Queue<NextTurtleCommand> _commands;
    private int _runtimeContinuationCount;

    private TurtleRunState(
        string runId,
        TurtleUserRequest request,
        TurtleBehavior behavior,
        Queue<NextTurtleCommand> commands)
    {
        RunId = runId;
        Request = request;
        Behavior = behavior;
        _commands = commands;
    }

    public string RunId { get; }

    public TurtleUserRequest Request { get; }

    public TurtleBehavior Behavior { get; private set; }

    public List<TurtleCommandReceipt> Receipts { get; } = [];

    public TurtleCompletion? Completion { get; private set; }

    public string Status { get; private set; } = "queued";

    public int PendingCommands => _commands.Count;

    public int RuntimeContinuationCount => _runtimeContinuationCount;

    public static TurtleRunState Create(string runId, TurtleUserRequest request, TurtleBehaviorCatalog behaviorCatalog)
    {
        var behavior = TurtleBehaviorMatcher.Match(request.Message);
        var commands = BuildBehaviorCommands(runId, request, behavior, behaviorCatalog);
        return new TurtleRunState(runId, request, behavior, commands);
    }

    public static TurtleRunState CreatePlanning(string runId, TurtleUserRequest request)
    {
        var behavior = TurtleBehaviorMatcher.Match(request.Message);
        return new TurtleRunState(runId, request, behavior, new Queue<NextTurtleCommand>())
        {
            Status = "planning"
        };
    }

    public static TurtleRunState CreateFromPlan(
        string runId,
        TurtleUserRequest request,
        TurtleCompiledPlan plan,
        TurtleCompiledPlanValidation validation)
    {
        var behavior = new TurtleBehavior(
            $"behavior-{Guid.NewGuid():N}",
            plan.BehaviorId,
            plan.Arguments,
            validation.CommandBudget);
        var commands = new Queue<NextTurtleCommand>();
        foreach (var step in plan.Steps)
        {
            commands.Enqueue(Command(runId, step.Action, step.Arguments));
        }

        return new TurtleRunState(runId, request, behavior, commands);
    }

    public void ApplyPlan(TurtleCompiledPlan plan, TurtleCompiledPlanValidation validation)
    {
        Behavior = new TurtleBehavior(
            $"behavior-{Guid.NewGuid():N}",
            plan.BehaviorId,
            plan.Arguments,
            validation.CommandBudget);

        _commands.Clear();
        foreach (var step in plan.Steps)
        {
            _commands.Enqueue(Command(RunId, step.Action, step.Arguments));
        }

        Status = _commands.Count == 0 ? "awaiting_completion" : "queued";
    }

    public void FailPlanning(
        string message,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings)
    {
        Completion = new TurtleCompletion(
            Success: false,
            ArtifactKind: "turtlequest.planning_failed",
            Message: message,
            Evidence: new Dictionary<string, object?>
            {
                ["errors"] = errors,
                ["warnings"] = warnings
            });
        Status = "failed";
    }

    public NextTurtleCommand? NextCommand()
    {
        if (Completion is not null)
        {
            Status = "completed";
            return null;
        }

        if (_commands.TryDequeue(out var command))
        {
            Status = "running";
            return command;
        }

        Status = Receipts.Count == 0 ? "queued" : "awaiting_completion";
        return null;
    }

    public bool TryAppendContinuation(
        TurtleCompiledPlan plan,
        TurtleCompiledPlanValidation validation,
        out string error)
    {
        error = "";
        if (!string.Equals(Status, "blocked", StringComparison.Ordinal))
        {
            error = $"Run is not blocked. Current status is {Status}.";
            return false;
        }

        if (!validation.Valid)
        {
            error = "Continuation plan is not valid.";
            return false;
        }

        if (_runtimeContinuationCount >= MaxRuntimeContinuations)
        {
            error = $"Runtime continuation limit reached for this run ({MaxRuntimeContinuations}).";
            return false;
        }

        _commands.Clear();
        foreach (var step in plan.Steps)
        {
            _commands.Enqueue(Command(RunId, step.Action, step.Arguments));
        }

        _runtimeContinuationCount++;
        Status = _commands.Count == 0 ? "blocked" : "running";
        return true;
    }

    public bool TryBuildRuntimeReplanContext(out TurtleRuntimeReplanContext context, out string error)
    {
        context = default!;
        error = "";
        if (!string.Equals(Status, "blocked", StringComparison.Ordinal))
        {
            error = $"Run is not blocked. Current status is {Status}.";
            return false;
        }

        var failedReceipt = Receipts.LastOrDefault(receipt => !receipt.Success);
        if (failedReceipt is null)
        {
            error = "Run is blocked but has no failed receipt.";
            return false;
        }

        context = new TurtleRuntimeReplanContext(
            RunId,
            Request.Message,
            Snapshot(),
            failedReceipt,
            PendingCommands,
            TurtlePlanCompiler.SupportedPrimitiveActions,
            TurtlePlanCompiler.ExecutionRules);
        return true;
    }

    public void Record(TurtleCommandReceipt receipt)
    {
        Receipts.Add(receipt);

        if (!receipt.Success)
        {
            Status = "blocked";
            return;
        }

        if (receipt.Action == "completeObjective" && receipt.Success)
        {
            Complete(success: !Receipts.Any(item => !item.Success), "Completion accepted from turtle executor receipt.");
            return;
        }

        Status = _commands.Count == 0 ? "awaiting_completion" : "running";
    }

    public void SimulateBehavior()
    {
        if (Completion is not null)
        {
            return;
        }

        var position = Request.Position;
        var orientation = Request.Orientation;
        var mined = 0;
        var placed = 0;
        var path = new List<Position> { position };

        while (_commands.TryDequeue(out var command))
        {
            switch (command.Action)
            {
                case "startBehavior":
                    Receipts.Add(Receipt(command, true, position, orientation, "Behavior started."));
                    break;
                case "dig":
                    mined++;
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated block mined.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:cobblestone"] = 1
                    }));
                    break;
                case "digDown":
                    mined++;
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated block below mined.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:dirt"] = 1
                    }));
                    break;
                case "digRememberedTarget":
                    mined++;
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated remembered log target cut.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:oak_log"] = 1
                    }));
                    break;
                case "moveForward":
                    position = position.Step(orientation);
                    path.Add(position);
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated turtle moved forward."));
                    break;
                case "face":
                    if (command.Arguments.TryGetValue("direction", out var directionValue) &&
                        directionValue is not null)
                    {
                        orientation = Convert.ToString(directionValue, CultureInfo.InvariantCulture) ?? orientation;
                    }
                    Receipts.Add(Receipt(command, true, position, orientation, $"Simulated turtle faced {orientation}."));
                    break;
                case "moveTowardRelative":
                    var dx = ArgumentInt(command.Arguments, "dx", 3);
                    var dz = ArgumentInt(command.Arguments, "dz", 0);
                    var budget = ArgumentInt(command.Arguments, "budget", ArgumentInt(command.Arguments, "pathBudget", 12));
                    var stopAdjacent = ArgumentBool(command.Arguments, "stopAdjacent", true);
                    for (var i = 0; i < budget && (Math.Abs(dx) + Math.Abs(dz) > (stopAdjacent ? 1 : 0)); i++)
                    {
                        if (Math.Abs(dx) >= Math.Abs(dz) && dx != 0)
                        {
                            orientation = dx > 0 ? "east" : "west";
                            position = position.Step(orientation);
                            dx += dx > 0 ? -1 : 1;
                        }
                        else if (dz != 0)
                        {
                            orientation = dz > 0 ? "south" : "north";
                            position = position.Step(orientation);
                            dz += dz > 0 ? -1 : 1;
                        }
                        path.Add(position);
                    }
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated bounded moveTowardRelative."));
                    break;
                case "moveBackward":
                    position = position.Step(TurnRight(TurnRight(orientation)));
                    path.Add(position);
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated turtle moved backward."));
                    break;
                case "moveUp":
                    position = position with { Y = position.Y + 1 };
                    path.Add(position);
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated turtle moved up."));
                    break;
                case "moveDown":
                    position = position with { Y = position.Y - 1 };
                    path.Add(position);
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated turtle moved down."));
                    break;
                case "turnLeft":
                    orientation = TurnLeft(orientation);
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated turtle turned left."));
                    break;
                case "turnRight":
                    orientation = TurnRight(orientation);
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated turtle turned right."));
                    break;
                case "completeObjective":
                    Receipts.Add(Receipt(command, true, position, orientation, "Objective completion checked."));
                    break;
                case "place":
                case "placeUp":
                case "placeDown":
                    placed++;
                    Receipts.Add(Receipt(command, true, position, orientation, $"Simulated {command.Action}.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:cobblestone"] = -1
                    }));
                    break;
                case "selectSlot":
                case "getInventory":
                    Receipts.Add(Receipt(command, true, position, orientation, $"Simulated {command.Action}."));
                    break;
                case "discardJunk":
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated magic trash can discarded junk.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:dirt"] = -64
                    }));
                    break;
                case "markWaypoint":
                    var waypointName = Convert.ToString(command.Arguments.TryGetValue("name", out var wp) ? wp : "waypoint", CultureInfo.InvariantCulture) ?? "waypoint";
                    Receipts.Add(Receipt(command, true, position, orientation, $"Marked waypointId={waypointName}; name={waypointName}."));
                    break;
                case "returnToPosition":
                    position = new Position(
                        ArgumentInt(command.Arguments, "x", position.X),
                        ArgumentInt(command.Arguments, "y", position.Y),
                        ArgumentInt(command.Arguments, "z", position.Z));
                    Receipts.Add(Receipt(command, true, position, orientation, $"Simulated returnToPosition target={position.X},{position.Y},{position.Z}."));
                    break;
                case "placeStorage":
                    var storageName = Convert.ToString(command.Arguments.TryGetValue("waypointName", out var storageWaypoint) ? storageWaypoint : "home_storage", CultureInfo.InvariantCulture) ?? "home_storage";
                    var storagePosition = position.Step(orientation);
                    placed++;
                    Receipts.Add(Receipt(command, true, position, orientation, $"Placed storage waypointId={storageName}; name={storageName}; storageKind={command.Arguments.GetValueOrDefault("storageKind", "barrel")}; storagePosition={storagePosition.X},{storagePosition.Y},{storagePosition.Z}; block=minecraft:barrel.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:barrel"] = -1
                    }));
                    break;
                case "depositInventory":
                    Receipts.Add(Receipt(command, true, position, orientation, "Deposited inventory slots=3; direction=forward.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:cobblestone"] = -64,
                        ["minecraft:dirt"] = -32
                    }));
                    break;
                case "drop":
                case "dropUp":
                case "dropDown":
                    Receipts.Add(Receipt(command, true, position, orientation, $"Simulated {command.Action}.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:cobblestone"] = -1 * ArgumentInt(command.Arguments, "count", 64)
                    }));
                    break;
                case "suck":
                case "suckUp":
                case "suckDown":
                    Receipts.Add(Receipt(command, true, position, orientation, $"Simulated {command.Action}.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:cobblestone"] = ArgumentInt(command.Arguments, "count", 64)
                    }));
                    break;
                case "craft":
                case "detect":
                case "detectUp":
                case "detectDown":
                    Receipts.Add(Receipt(command, true, position, orientation, $"Simulated {command.Action}."));
                    break;
                case "tunnelLine":
                    var tunnelLength = ArgumentInt(command.Arguments, "length", 6);
                    var tunnelHeight = ArgumentInt(command.Arguments, "height", 2);
                    for (var tunnelStep = 0; tunnelStep < tunnelLength; tunnelStep++)
                    {
                        position = orientation switch
                        {
                            "north" => position with { Z = position.Z - 1 },
                            "south" => position with { Z = position.Z + 1 },
                            "east" => position with { X = position.X + 1 },
                            "west" => position with { X = position.X - 1 },
                            _ => position
                        };
                    }

                    mined += tunnelLength * tunnelHeight;
                    Receipts.Add(Receipt(command, true, position, orientation, $"Simulated tunnelLine length={tunnelLength}; height={tunnelHeight}; stepsCompleted={tunnelLength}; blocksRemoved={tunnelLength * tunnelHeight}; inventoryPressure=low.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:cobblestone"] = tunnelLength * tunnelHeight
                    }));
                    break;
                case "branchTunnel":
                    var branchLength = ArgumentInt(command.Arguments, "length", 6);
                    var branchSide = Convert.ToString(command.Arguments.TryGetValue("side", out var sideValue) ? sideValue : "left", CultureInfo.InvariantCulture) ?? "left";
                    var branchStart = position;
                    var branchOrientation = branchSide == "right" ? TurnRight(orientation) : TurnLeft(orientation);
                    for (var branchStep = 0; branchStep < branchLength; branchStep++)
                    {
                        position = position.Step(branchOrientation);
                    }
                    position = branchStart;
                    mined += branchLength * 2;
                    Receipts.Add(Receipt(command, true, position, orientation, $"Simulated branchTunnel routeId={command.Arguments.GetValueOrDefault("routeId", "route-branch")}; side={branchSide}; start={branchStart.X},{branchStart.Y},{branchStart.Z}; stepsCompleted={branchLength}; blocksRemoved={branchLength * 2}; clearance=player_walkable.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:cobblestone"] = branchLength * 2
                    }));
                    break;
                case "branchMinePattern":
                    var mainLength = ArgumentInt(command.Arguments, "mainLength", 9);
                    var branchCount = ArgumentInt(command.Arguments, "branchCount", 2);
                    var branchMineLength = ArgumentInt(command.Arguments, "branchLength", 6);
                    position = position.Step(orientation) with
                    {
                        X = orientation is "east" ? position.X + mainLength : orientation is "west" ? position.X - mainLength : position.X,
                        Z = orientation is "south" ? position.Z + mainLength : orientation is "north" ? position.Z - mainLength : position.Z
                    };
                    mined += (mainLength * 2) + (branchCount * branchMineLength * 2);
                    Receipts.Add(Receipt(command, true, Request.Position, orientation, $"Simulated branchMinePattern mainRouteId={command.Arguments.GetValueOrDefault("mainRouteId", "route-main")}; mainLength={mainLength}; branchCount={branchCount}; branchesCompleted={branchCount}; blocksRemoved={mined}; clearance=player_walkable.", inventoryDelta: new Dictionary<string, int>
                    {
                        ["minecraft:cobblestone"] = mined
                    }));
                    break;
                case "scanNearby":
                    Receipts.Add(Receipt(command, true, position, orientation, "Simulated scanNearby radius=12; query=minecraft:logs; matches=1; nearest=minecraft:oak_log@3,0,4."));
                    break;
                default:
                    Receipts.Add(Receipt(command, true, position, orientation, $"Simulated {command.Action}."));
                    break;
            }
        }

        Complete(success: true, $"Simulated {Behavior.BehaviorId} behavior completed.", new Dictionary<string, object?>
        {
            ["startPosition"] = Request.Position,
            ["finalPosition"] = position,
            ["path"] = path,
            ["blocksMined"] = mined,
            ["blocksPlaced"] = placed,
            ["receiptCount"] = Receipts.Count
        });
    }

    private static int ArgumentInt(IReadOnlyDictionary<string, object?> arguments, string name, int fallback)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            double doubleValue => checked((int)doubleValue),
            decimal decimalValue => checked((int)decimalValue),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue) => intValue,
            JsonElement element when element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var intValue) => intValue,
            string stringValue when int.TryParse(stringValue, out var intValue) => intValue,
            IConvertible convertible => Convert.ToInt32(convertible, CultureInfo.InvariantCulture),
            _ => fallback
        };
    }

    private static bool ArgumentBool(IReadOnlyDictionary<string, object?> arguments, string name, bool fallback)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            bool boolValue => boolValue,
            JsonElement element when element.ValueKind is JsonValueKind.True => true,
            JsonElement element when element.ValueKind is JsonValueKind.False => false,
            JsonElement element when element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var boolValue) => boolValue,
            string stringValue when bool.TryParse(stringValue, out var boolValue) => boolValue,
            _ => fallback
        };
    }

    public TurtleRunSnapshot Snapshot() =>
        new(
            RunId,
            Request.TurtleId,
            Status,
            Request,
            Behavior,
            PendingCommands,
            RuntimeContinuationCount,
            Receipts.ToArray(),
            Completion);

    private static Queue<NextTurtleCommand> BuildBehaviorCommands(
        string runId,
        TurtleUserRequest request,
        TurtleBehavior behavior,
        TurtleBehaviorCatalog behaviorCatalog)
    {
        var commands = new Queue<NextTurtleCommand>();

        if (behavior.BehaviorId == TurtleBehaviorIds.BuildColumn)
        {
            var columnPlan = TurtlePlanCompiler.Compile(request, behavior, behaviorCatalog);
            if (columnPlan.Validation.Valid)
            {
                foreach (var step in columnPlan.Steps)
                {
                    commands.Enqueue(Command(runId, step.Action, step.Arguments));
                }

                return commands;
            }
        }

        if (behaviorCatalog.TryExpand(runId, behavior, Command, out var catalogCommands))
        {
            foreach (var command in catalogCommands)
            {
                commands.Enqueue(command);
            }

            return commands;
        }

        var compiledPlan = TurtlePlanCompiler.Compile(request, behavior, behaviorCatalog);
        if (compiledPlan.Validation.Valid && compiledPlan.PlanKind == "compiled_behavior")
        {
            foreach (var step in compiledPlan.Steps)
            {
                commands.Enqueue(Command(runId, step.Action, step.Arguments));
            }

            return commands;
        }

        commands.Enqueue(Command(runId, "startBehavior", new Dictionary<string, object?>
        {
            ["behaviorRunId"] = behavior.BehaviorRunId,
            ["behaviorId"] = behavior.BehaviorId,
            ["arguments"] = behavior.Arguments
        }));

        if (behavior.BehaviorId == TurtleBehaviorIds.DigLineReturn &&
            behavior.Arguments.TryGetValue("length", out var lengthValue) &&
            Convert.ToInt32(lengthValue) is var length)
        {
            for (var i = 0; i < length; i++)
            {
                commands.Enqueue(Command(runId, "inspect"));
                commands.Enqueue(Command(runId, "dig"));
                commands.Enqueue(Command(runId, "moveForward"));
            }

            commands.Enqueue(Command(runId, "turnRight"));
            commands.Enqueue(Command(runId, "turnRight"));

            for (var i = 0; i < length; i++)
            {
                commands.Enqueue(Command(runId, "moveForward"));
            }
        }
        else
        {
            commands.Enqueue(Command(runId, "emitStatus", new Dictionary<string, object?>
            {
                ["message"] = compiledPlan.Validation.Errors.Count > 0
                    ? string.Join(" ", compiledPlan.Validation.Errors)
                    : $"No executor behavior is available for {behavior.BehaviorId}."
            }));
        }

        commands.Enqueue(Command(runId, "completeObjective", new Dictionary<string, object?>
        {
            ["artifactKind"] = "turtlequest.objective_completed"
        }));
        return commands;
    }

    private static string TurnRight(string orientation) =>
        orientation.ToLowerInvariant() switch
        {
            "north" => "east",
            "east" => "south",
            "south" => "west",
            "west" => "north",
            _ => orientation
        };

    private static string TurnLeft(string orientation) =>
        orientation.ToLowerInvariant() switch
        {
            "north" => "west",
            "west" => "south",
            "south" => "east",
            "east" => "north",
            _ => orientation
        };

    private void Complete(
        bool success,
        string message,
        IReadOnlyDictionary<string, object?>? evidence = null)
    {
        Status = success ? "completed" : "failed";
        Completion = new TurtleCompletion(
            success,
            "turtlequest.objective_completed",
            message,
            evidence ?? CompletionEvidence());
    }

    private IReadOnlyDictionary<string, object?> CompletionEvidence() =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["behaviorRunId"] = Behavior.BehaviorRunId,
            ["behaviorId"] = Behavior.BehaviorId,
            ["receiptCount"] = Receipts.Count,
            ["successfulReceipts"] = Receipts.Count(item => item.Success),
            ["failedReceipts"] = Receipts.Count(item => !item.Success)
        };

    private TurtleCommandReceipt Receipt(
        NextTurtleCommand command,
        bool success,
        Position position,
        string orientation,
        string message,
        IReadOnlyDictionary<string, int>? inventoryDelta = null) =>
        new(
            RunId,
            Request.TurtleId,
            command.CommandId,
            command.Action,
            success,
            position,
            orientation,
            DateTimeOffset.UtcNow,
            BlockAhead: null,
            Hazards: [],
            InventoryDelta: inventoryDelta ?? new Dictionary<string, int>(),
            Message: message);

    private static NextTurtleCommand Command(
        string runId,
        string action,
        IReadOnlyDictionary<string, object?>? arguments = null) =>
        new(runId, $"cmd-{Guid.NewGuid():N}", action, arguments ?? new Dictionary<string, object?>());
}

public static class TurtleQuestOutcomeEvaluator
{
    public static TurtleQuestEvaluation Evaluate(
        TurtleQuestSession session,
        TurtleQuestDefinition quest,
        TurtleRunSnapshot run,
        WorldFragmentDiff? diff)
    {
        var receiptCounts = run.Receipts
            .Where(receipt => receipt.Success)
            .GroupBy(receipt => receipt.Action, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var checks = new List<ValidationCheck>();
        var criteria = quest.SuccessCriteria;

        checks.Add(new ValidationCheck(
            "completion.success",
            run.Completion?.Success == true,
            run.Completion?.Success == true ? "Run completion artifact succeeded." : "Run has no successful completion artifact."));

        if (criteria.TryGetValue("completionArtifact", out var artifact) && artifact is string expectedArtifact)
        {
            var actualArtifact = run.Completion?.ArtifactKind;
            checks.Add(new ValidationCheck(
                "completion.artifactKind",
                string.Equals(expectedArtifact, actualArtifact, StringComparison.Ordinal),
                $"Expected {expectedArtifact}, actual {actualArtifact ?? "<none>"}." ));
        }

        AddMinimumReceiptCheck(checks, criteria, receiptCounts, "minimumSuccessfulDigReceipts", "dig");
        AddMinimumReceiptCheck(checks, criteria, receiptCounts, "minimumSuccessfulDigDownReceipts", "digDown");
        AddMinimumReceiptCheck(checks, criteria, receiptCounts, "minimumSuccessfulInspectReceipts", "inspect");

        if (criteria.TryGetValue("finalPosition", out var finalPosition) &&
            finalPosition is string finalPositionString &&
            string.Equals(finalPositionString, "start", StringComparison.Ordinal))
        {
            var lastReceipt = run.Receipts.LastOrDefault();
            var passed = lastReceipt?.Position == run.Request.Position;
            checks.Add(new ValidationCheck(
                "finalPosition.start",
                passed,
                passed ? "Final receipt position matches start position." : "Final receipt position does not match start position."));
        }

        if (criteria.ContainsKey("expectedChangedFootprint"))
        {
            var expectedChanges = ExpectedChangedFootprintBlocks(criteria["expectedChangedFootprint"]);
            if (diff is null)
            {
                checks.Add(new ValidationCheck(
                    "worldDiff.expectedChangedFootprint",
                    false,
                    "No world diff was provided."));
            }
            else
            {
                var changedToAir = diff.Changes.Count(change =>
                    !string.Equals(change.Before, "minecraft:air", StringComparison.Ordinal) &&
                    string.Equals(change.After, "minecraft:air", StringComparison.Ordinal));
                checks.Add(new ValidationCheck(
                    "worldDiff.changedToAir",
                    changedToAir >= expectedChanges,
                    $"Expected at least {expectedChanges} changed-to-air blocks, actual {changedToAir}."));
            }
        }

        var evidence = new TurtleQuestEvidencePackage(
            quest.Prompt,
            run.Behavior.BehaviorId,
            run.Receipts.Count,
            receiptCounts,
            run.Completion?.Success == true,
            diff);

        return new TurtleQuestEvaluation(
            session.SessionId,
            session.QuestId,
            checks.All(check => check.Passed) ? "passed" : "failed",
            checks.All(check => check.Passed),
            checks,
            evidence);
    }

    private static void AddMinimumReceiptCheck(
        List<ValidationCheck> checks,
        IReadOnlyDictionary<string, object?> criteria,
        IReadOnlyDictionary<string, int> receiptCounts,
        string criterionKey,
        string receiptAction)
    {
        if (!criteria.TryGetValue(criterionKey, out var expectedValue) || expectedValue is null)
        {
            return;
        }

        var expected = Convert.ToInt32(expectedValue);
        receiptCounts.TryGetValue(receiptAction, out var actual);
        checks.Add(new ValidationCheck(
            $"receipts.{receiptAction}.minimum",
            actual >= expected,
            $"Expected at least {expected} successful {receiptAction} receipt(s), actual {actual}."));
    }

    private static int ExpectedChangedFootprintBlocks(object? value)
    {
        if (value is not IReadOnlyDictionary<string, object?> footprint)
        {
            return 0;
        }

        var width = footprint.TryGetValue("width", out var widthValue) ? Convert.ToInt32(widthValue) : 0;
        var length = footprint.TryGetValue("length", out var lengthValue) ? Convert.ToInt32(lengthValue) : 0;
        var depth = footprint.TryGetValue("depth", out var depthValue) ? Convert.ToInt32(depthValue) : 1;
        return width * length * depth;
    }
}

public static class TurtleQuestTrace
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly bool Enabled = !string.Equals(
        Environment.GetEnvironmentVariable("TURTLEQUEST_TRACE_ENABLED"),
        "false",
        StringComparison.OrdinalIgnoreCase);

    private static readonly string Root = Environment.GetEnvironmentVariable("TURTLEQUEST_TRACE_DIR") ??
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "run", "traces");

    public static void WritePlanner(string eventType, object payload)
    {
        Write("planner", eventType, payload);
    }

    public static void WriteRun(string runId, string eventType, object payload)
    {
        Write(runId, eventType, payload);
    }

    public static bool TryReadRunTrace(string runId, out string trace)
    {
        trace = "";
        var path = TracePath(runId);
        if (!File.Exists(path))
        {
            return false;
        }

        trace = File.ReadAllText(path);
        return true;
    }

    private static void Write(string scopeId, string eventType, object payload)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var safeScope = SafeScope(scopeId);
            var directory = Path.Combine(Root, safeScope);
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(new
            {
                observedAt = DateTimeOffset.UtcNow,
                scopeId = safeScope,
                eventType,
                payload
            }, JsonOptions);

            lock (Gate)
            {
                File.AppendAllText(Path.Combine(directory, "events.jsonl"), line + Environment.NewLine);
            }
        }
        catch
        {
            // Tracing must not affect turtle execution.
        }
    }

    private static string TracePath(string runId) =>
        Path.Combine(Root, SafeScope(runId), "events.jsonl");

    private static string SafeScope(string value)
    {
        var chars = value
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            .ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}

public static class TurtlePlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<TurtlePlannerGenerateResult> GenerateAsync(
        TurtlePlannerGenerateRequest request,
        TurtleBehaviorCatalog behaviorCatalog,
        TurtleQuestCookbook cookbook,
        object routeMemorySnapshot)
    {
        var repairs = new List<TurtlePlannerRepairAttempt>();
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? "deterministic"
            : request.Mode.Trim().ToLowerInvariant();
        var behavior = string.Equals(mode, "agentica", StringComparison.Ordinal)
            ? TurtleBehaviorMatcher.OpenGoal(request.Request.Message)
            : TurtleBehaviorMatcher.Match(request.Request.Message);
        var context = BuildContext(request.Request, behavior, behaviorCatalog, cookbook, routeMemorySnapshot);

        var plan = mode switch
        {
            "deterministic" => TurtlePlanCompiler.Compile(request.Request, behavior, behaviorCatalog),
            "mock-llm" => MockLlmPlan(request.Request, behavior, behaviorCatalog, repairs, Math.Max(0, request.RepairAttempts)),
            "agentica" => await AgenticaSubprocessPlanner.GenerateAsync(request.Request, behavior, context, repairs, Math.Max(0, request.RepairAttempts)),
            _ => BlockedAgenticaPlan(request.Request, behavior, $"Unknown planner mode '{request.Mode}'.")
        };
        var commandBudget = plan.Validation.CommandBudget > 0
            ? plan.Validation.CommandBudget
            : behavior.CommandBudget;
        var validation = TurtlePlanCompiler.ValidateCompiledPlan(plan, commandBudget);
        plan = plan with { Validation = validation };

        return new TurtlePlannerGenerateResult(
            mode,
            context,
            plan,
            validation,
            repairs,
            validation.Valid,
            Run: null);
    }

    public static async Task<TurtleRuntimeReplanResult> GenerateRuntimeContinuationAsync(
        string mode,
        TurtleRuntimeReplanContext context,
        int repairAttempts)
    {
        var repairs = new List<TurtlePlannerRepairAttempt>();
        var normalizedMode = string.IsNullOrWhiteSpace(mode)
            ? "agentica"
            : mode.Trim().ToLowerInvariant();
        if (TryBuildReturnToPositionContinuation(context, out var targetedContinuation))
        {
            return new TurtleRuntimeReplanResult(
                normalizedMode,
                context,
                targetedContinuation,
                targetedContinuation.Validation,
                repairs,
                Applied: false,
                Continuation: null);
        }

        var plan = normalizedMode switch
        {
            "agentica" => await AgenticaSubprocessPlanner.GenerateContinuationAsync(context, repairs, repairAttempts),
            "mock-llm" => MockContinuationPlan(context),
            _ => BlockedRuntimePlan(context, $"Unknown runtime replan mode '{mode}'.")
        };

        return new TurtleRuntimeReplanResult(
            normalizedMode,
            context,
            plan,
            plan.Validation,
            repairs,
            Applied: false,
            Continuation: null);
    }

    private static bool TryBuildReturnToPositionContinuation(
        TurtleRuntimeReplanContext context,
        out TurtleCompiledPlan plan)
    {
        plan = default!;
        if (context.FailedReceipt.Action != "branchMinePattern")
        {
            return false;
        }

        var message = context.FailedReceipt.Message ?? "";
        if (!message.Contains("recoveryHint=return_to_position_or_stop", StringComparison.Ordinal) ||
            !TryExtractPositionToken(message, "returnTarget", out var target))
        {
            return false;
        }

        var steps = new[]
        {
            new TurtleCompiledPlanStep("emitStatus", new Dictionary<string, object?>
            {
                ["stage"] = "return_to_position_recovery",
                ["reason"] = context.FailedReceipt.Action,
                ["target"] = $"{target.X},{target.Y},{target.Z}"
            }),
            new TurtleCompiledPlanStep("returnToPosition", new Dictionary<string, object?>
            {
                ["x"] = target.X,
                ["y"] = target.Y,
                ["z"] = target.Z,
                ["budget"] = 192
            }),
            new TurtleCompiledPlanStep("emitStatus", new Dictionary<string, object?>
            {
                ["stage"] = "return_to_position_or_stopped",
                ["target"] = $"{target.X},{target.Y},{target.Z}"
            }),
            new TurtleCompiledPlanStep("completeObjective", new Dictionary<string, object?>
            {
                ["artifactKind"] = "turtlequest.objective_partial_recovery",
                ["stage"] = "return_to_position_or_stopped"
            })
        };
        var validation = TurtlePlanCompiler.ValidateContinuationPlan(steps, 16);
        plan = new TurtleCompiledPlan(
            $"plan-{Guid.NewGuid():N}",
            "targeted_runtime_continuation",
            context.Run.Behavior.BehaviorId,
            "host_recovery",
            new Dictionary<string, object?>
            {
                ["failedAction"] = context.FailedReceipt.Action,
                ["failedCommandId"] = context.FailedReceipt.CommandId,
                ["recoveryHint"] = "return_to_position_or_stop",
                ["returnTarget"] = $"{target.X},{target.Y},{target.Z}",
                ["returnCurrent"] = ExtractToken(message, "returnCurrent"),
                ["pathDepth"] = ExtractToken(message, "pathDepth"),
                ["pathTail"] = ExtractToken(message, "pathTail")
            },
            steps,
            validation);
        return true;
    }

    private static bool TryExtractPositionToken(string message, string key, out Position position)
    {
        position = default!;
        var token = ExtractToken(message, key);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        position = new Position(x, y, z);
        return true;
    }

    private static string? ExtractToken(string? message, string key)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var match = Regex.Match(message, $@"(?:^|[;\s]){Regex.Escape(key)}=([^;\s.]+)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static TurtlePlannerContext BuildContext(
        TurtleUserRequest request,
        TurtleBehavior behavior,
        TurtleBehaviorCatalog behaviorCatalog,
        TurtleQuestCookbook cookbook,
        object routeMemorySnapshot)
    {
        var executable = behaviorCatalog.Contains(behavior.BehaviorId) ||
            TurtlePlanCompiler.IsKnownCompiledBehavior(behavior.BehaviorId);
        var warnings = new List<string>();
        if (!executable)
        {
            warnings.Add($"No deterministic executor exists for {behavior.BehaviorId}; compile flattened IR or ask a clarifying question.");
        }

        if (behavior.Arguments.TryGetValue("missing", out var missing) && missing is not null)
        {
            warnings.Add($"Missing required parameter(s): {missing}.");
        }

        return new TurtlePlannerContext(
            request,
            new TurtlePlanPreview(
                behavior.BehaviorId,
                executable ? "known_or_compilable" : "requires_planning",
                behavior.Arguments,
                executable,
                warnings),
            TurtlePlanCompiler.SupportedPrimitiveActions,
            cookbook.Snapshot(),
            TurtleEnvironmentProfile.Default,
            routeMemorySnapshot,
            TurtlePlanCompiler.ExecutionRules);
    }

    private static TurtleCompiledPlan MockLlmPlan(
        TurtleUserRequest request,
        TurtleBehavior behavior,
        TurtleBehaviorCatalog behaviorCatalog,
        List<TurtlePlannerRepairAttempt> repairs,
        int repairAttempts)
    {
        if (behavior.BehaviorId != TurtleBehaviorIds.ExcavateRectangularPit)
        {
            return TurtlePlanCompiler.Compile(request, behavior, behaviorCatalog);
        }

        var badSteps = new[]
        {
            new TurtleCompiledPlanStep("startBehavior", new Dictionary<string, object?>()),
            new TurtleCompiledPlanStep("digDown", new Dictionary<string, object?>())
        };
        var badValidation = TurtlePlanCompiler.ValidatePlan(badSteps, behavior.CommandBudget);
        repairs.Add(new TurtlePlannerRepairAttempt(
            1,
            "mock-llm intentionally omitted completeObjective to exercise validator repair feedback.",
            badValidation));

        if (repairAttempts <= 0)
        {
            return new TurtleCompiledPlan(
                $"plan-{Guid.NewGuid():N}",
                "mock_llm_invalid",
                behavior.BehaviorId,
                "mock_llm",
                behavior.Arguments,
                badSteps,
                badValidation);
        }

        return TurtlePlanCompiler.Compile(request, behavior, behaviorCatalog) with
        {
            PlanKind = "mock_llm_repaired",
            Source = "mock_llm"
        };
    }

    private static TurtleCompiledPlan BlockedAgenticaPlan(
        TurtleUserRequest request,
        TurtleBehavior behavior,
        string reason)
    {
        var validation = TurtlePlanCompiler.ValidatePlan(
            [],
            behavior.CommandBudget,
            [reason]);
        return new TurtleCompiledPlan(
            $"plan-{Guid.NewGuid():N}",
            "planner_blocked",
            behavior.BehaviorId,
            "planner",
            behavior.Arguments,
            [],
            validation);
    }

    private static TurtleCompiledPlan MockContinuationPlan(TurtleRuntimeReplanContext context)
    {
        var steps = new[]
        {
            new TurtleCompiledPlanStep("emitStatus", new Dictionary<string, object?>
            {
                ["message"] = $"Runtime replan observed failed {context.FailedReceipt.Action}; stopping safely."
            }),
            new TurtleCompiledPlanStep("completeObjective", new Dictionary<string, object?>
            {
                ["artifactKind"] = "turtlequest.objective_completed"
            })
        };
        var validation = TurtlePlanCompiler.ValidateContinuationPlan(steps, 16);
        return new TurtleCompiledPlan(
            $"plan-{Guid.NewGuid():N}",
            "mock_runtime_continuation",
            context.Run.Behavior.BehaviorId,
            "mock_llm",
            new Dictionary<string, object?>
            {
                ["failedAction"] = context.FailedReceipt.Action,
                ["failedCommandId"] = context.FailedReceipt.CommandId
            },
            steps,
            validation);
    }

    private static TurtleCompiledPlan BlockedRuntimePlan(TurtleRuntimeReplanContext context, string reason)
    {
        var validation = TurtlePlanCompiler.ValidateContinuationPlan(
            [],
            Math.Max(1, context.Run.Behavior.CommandBudget),
            [reason]);
        return new TurtleCompiledPlan(
            $"plan-{Guid.NewGuid():N}",
            "runtime_replan_blocked",
            context.Run.Behavior.BehaviorId,
            "runtime_replan",
            new Dictionary<string, object?>
            {
                ["failedAction"] = context.FailedReceipt.Action,
                ["failedCommandId"] = context.FailedReceipt.CommandId
            },
            [],
            validation);
    }

    private static class AgenticaSubprocessPlanner
    {
        public static async Task<TurtleCompiledPlan> GenerateAsync(
            TurtleUserRequest request,
            TurtleBehavior behavior,
            TurtlePlannerContext context,
            List<TurtlePlannerRepairAttempt> repairs,
            int repairAttempts)
        {
            var command = ReadPlannerEnvironment("COMMAND");
            if (string.IsNullOrWhiteSpace(command))
            {
                return BlockedAgenticaPlan(
                    request,
                    behavior,
                    "Agentica subprocess planner is not configured. Set TURTLEQUEST_AGENTICA_PLANNER_COMMAND to a command that reads planner/context JSON from stdin and returns TurtleCompiledPlan JSON on stdout.");
            }

            TurtleCompiledPlan? lastPlan = null;
            for (var attempt = 1; attempt <= Math.Max(1, repairAttempts + 1); attempt++)
            {
                var commandRequest = new TurtleAgenticaPlannerCommandRequest(
                    request.Message,
                    attempt,
                    context,
                    repairs.ToArray());

                var result = await InvokePlannerCommandAsync(command, commandRequest).ConfigureAwait(false);
                if (!result.Success)
                {
                    return BlockedAgenticaPlan(request, behavior, result.Error ?? "Agentica subprocess planner failed.");
                }

                if (!TryReadPlan(result.Stdout, out var plan, out var parseError))
                {
                    return BlockedAgenticaPlan(request, behavior, parseError);
                }

                var commandBudget = plan.Validation.CommandBudget > 0
                    ? plan.Validation.CommandBudget
                    : behavior.CommandBudget;
                var validation = TurtlePlanCompiler.ValidateCompiledPlan(plan, commandBudget);
                lastPlan = plan with
                {
                    Source = "agentica_subprocess",
                    Validation = validation
                };

                if (validation.Valid)
                {
                    return lastPlan;
                }

                repairs.Add(new TurtlePlannerRepairAttempt(
                    attempt,
                    "Agentica subprocess plan failed validation.",
                    validation));
            }

            return lastPlan ?? BlockedAgenticaPlan(request, behavior, "Agentica subprocess planner did not return a plan.");
        }

        public static async Task<TurtleCompiledPlan> GenerateContinuationAsync(
            TurtleRuntimeReplanContext context,
            List<TurtlePlannerRepairAttempt> repairs,
            int repairAttempts)
        {
            var command = ReadPlannerEnvironment("COMMAND");
            if (string.IsNullOrWhiteSpace(command))
            {
                return BlockedRuntimePlan(
                    context,
                    "Agentica subprocess planner is not configured. Set TURTLEQUEST_AGENTICA_PLANNER_COMMAND to a command that reads runtime replan JSON from stdin and returns TurtleCompiledPlan JSON on stdout.");
            }

            TurtleCompiledPlan? lastPlan = null;
            for (var attempt = 1; attempt <= Math.Max(1, repairAttempts + 1); attempt++)
            {
                var commandRequest = new TurtleAgenticaReplanCommandRequest(
                    context.Goal,
                    attempt,
                    context,
                    repairs.ToArray());

                var result = await InvokePlannerCommandAsync(command, commandRequest).ConfigureAwait(false);
                if (!result.Success)
                {
                    return BlockedRuntimePlan(context, result.Error ?? "Agentica subprocess runtime replan failed.");
                }

                if (!TryReadPlan(result.Stdout, out var plan, out var parseError))
                {
                    return BlockedRuntimePlan(context, parseError);
                }

                var commandBudget = plan.Validation.CommandBudget > 0
                    ? plan.Validation.CommandBudget
                    : Math.Max(1, context.Run.Behavior.CommandBudget);
                var validation = TurtlePlanCompiler.ValidateContinuationPlan(plan.Steps, commandBudget);
                lastPlan = plan with
                {
                    Source = "agentica_subprocess_runtime_replan",
                    Validation = validation
                };

                if (validation.Valid)
                {
                    return lastPlan;
                }

                repairs.Add(new TurtlePlannerRepairAttempt(
                    attempt,
                    "Agentica subprocess runtime continuation failed validation.",
                    validation));
            }

            return lastPlan ?? BlockedRuntimePlan(context, "Agentica subprocess runtime replan did not return a plan.");
        }

        private static async Task<PlannerProcessResult> InvokePlannerCommandAsync(
            string command,
            object request)
        {
            var timeoutSecondsText = ReadPlannerEnvironment("TIMEOUT_SECONDS");
            var timeoutSeconds = int.TryParse(timeoutSecondsText, out var parsedTimeout) && parsedTimeout > 0
                ? parsedTimeout
                : 120;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = ReadPlannerEnvironment("ARGS") ?? "",
                WorkingDirectory = ReadPlannerEnvironment("CWD") ?? Environment.CurrentDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    return new PlannerProcessResult(false, "", "", "Agentica subprocess planner did not start.");
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    return new PlannerProcessResult(false, stdout, stderr, $"Agentica subprocess planner exited with code {process.ExitCode}: {stderr}");
                }

                return new PlannerProcessResult(true, stdout, stderr, null);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new PlannerProcessResult(false, "", "", $"Agentica subprocess planner timed out after {timeoutSeconds} seconds.");
            }
            catch (Exception exception)
            {
                TryKill(process);
                return new PlannerProcessResult(false, "", "", $"Agentica subprocess planner failed to start or run: {exception.Message}");
            }
        }

        private static bool TryReadPlan(string stdout, out TurtleCompiledPlan plan, out string error)
        {
            plan = default!;
            error = "";
            if (string.IsNullOrWhiteSpace(stdout))
            {
                error = "Agentica subprocess planner returned empty stdout.";
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(stdout);
                var root = document.RootElement;
                var planElement = root.TryGetProperty("plan", out var nestedPlan)
                    ? nestedPlan
                    : root;
                var parsed = planElement.Deserialize<TurtleCompiledPlan>(JsonOptions);
                if (parsed is null)
                {
                    error = "Agentica subprocess planner returned null plan JSON.";
                    return false;
                }

                plan = parsed;
                return true;
            }
            catch (JsonException exception)
            {
                error = $"Agentica subprocess planner returned invalid plan JSON: {exception.Message}";
                return false;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        private sealed record PlannerProcessResult(
            bool Success,
            string Stdout,
            string Stderr,
            string? Error);

        private static string? ReadPlannerEnvironment(string suffix) =>
            Environment.GetEnvironmentVariable($"TURTLEQUEST_AGENTICA_PLANNER_{suffix}");
    }
}

public static class TurtleBehaviorIds
{
    public const string OpenGoal = "turtlequest.open_goal";
    public const string DigLineReturn = "turtlequest.dig_line_return";
    public const string TunnelLine = "turtlequest.tunnel_line";
    public const string BranchTunnel = "turtlequest.branch_tunnel";
    public const string BranchMinePattern = "turtlequest.branch_mine_pattern";
    public const string BootstrapHomeStorage = "turtlequest.bootstrap_home_storage";
    public const string DepositInventory = "turtlequest.deposit_inventory";
    public const string ReturnToWaypoint = "turtlequest.return_to_waypoint";
    public const string ExcavateRectangularPit = "turtlequest.excavate_rectangular_pit";
    public const string BuildColumn = "turtlequest.build_column";
    public const string FindTree = "turtlequest.find_tree";
    public const string HarvestTree = "turtlequest.harvest_tree";
    public const string FindDiamonds = "turtlequest.find_diamonds";
    public const string BuildTower = "turtlequest.build_tower";
    public const string BuildHouse = "turtlequest.build_house";
    public const string RecoverTurtle = "turtlequest.recover_turtle";
    public const string Assist = "turtlequest.assist";
    public const string Follow = "turtlequest.follow";
    public const string Unsupported = "turtlequest.unsupported";
}

public sealed class TurtleQuestBoardCatalog
{
    private readonly Dictionary<string, TurtleQuestBoard> _boards;

    private TurtleQuestBoardCatalog(Dictionary<string, TurtleQuestBoard> boards)
    {
        _boards = boards;
    }

    public static TurtleQuestBoardCatalog Load()
    {
        var boards = new Dictionary<string, TurtleQuestBoard>(StringComparer.Ordinal);
        var root = TurtleQuestPaths.FindRepositoryRoot();
        var boardDirectory = root is null ? null : Path.Combine(root, "boards");

        if (boardDirectory is not null && Directory.Exists(boardDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(boardDirectory, "*.json"))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var rootElement = document.RootElement;
                var boardId = rootElement.GetProperty("boardId").GetString();
                if (string.IsNullOrWhiteSpace(boardId))
                {
                    continue;
                }

                var quests = new List<TurtleQuestDefinition>();
                foreach (var questElement in rootElement.GetProperty("quests").EnumerateArray())
                {
                    var questId = questElement.GetProperty("questId").GetString() ?? "";
                    var title = questElement.GetProperty("title").GetString() ?? questId;
                    var prompt = questElement.GetProperty("prompt").GetString() ?? title;
                    var behaviorId = questElement.GetProperty("behaviorId").GetString() ?? TurtleBehaviorIds.Unsupported;
                    var arguments = questElement.TryGetProperty("arguments", out var argsElement)
                        ? ReadObject(argsElement)
                        : new Dictionary<string, object?>();
                    var successCriteria = questElement.TryGetProperty("successCriteria", out var criteriaElement)
                        ? ReadObject(criteriaElement)
                        : new Dictionary<string, object?>();

                    quests.Add(new TurtleQuestDefinition(
                        questId,
                        title,
                        prompt,
                        behaviorId,
                        arguments,
                        successCriteria));
                }

                boards[boardId] = new TurtleQuestBoard(
                    boardId,
                    rootElement.GetProperty("title").GetString() ?? boardId,
                    quests);
            }
        }

        return new TurtleQuestBoardCatalog(boards);
    }

    public IReadOnlyList<object> Snapshot() =>
        _boards.Values
            .OrderBy(board => board.BoardId, StringComparer.Ordinal)
            .Select(board => new
            {
                board.BoardId,
                board.Title,
                QuestCount = board.Quests.Count
            })
            .Cast<object>()
            .ToArray();

    public bool TryGetBoard(string boardId, out TurtleQuestBoard board) =>
        _boards.TryGetValue(boardId, out board!);

    public bool TryGetQuest(
        string boardId,
        string questId,
        out TurtleQuestBoard board,
        out TurtleQuestDefinition quest)
    {
        quest = null!;
        if (!_boards.TryGetValue(boardId, out board!))
        {
            return false;
        }

        quest = board.Quests.FirstOrDefault(item => string.Equals(item.QuestId, questId, StringComparison.Ordinal))!;
        return quest is not null;
    }

    private static Dictionary<string, object?> ReadObject(JsonElement element)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            values[property.Name] = ReadValue(property.Value);
        }

        return values;
    }

    private static object? ReadValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt32(out var intValue) ? intValue : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => ReadObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ReadValue).ToArray(),
            _ => null
        };
}

public static class TurtleQuestPaths
{
    public static string? FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "behaviors")) ||
                File.Exists(Path.Combine(current.FullName, "TurtleQuest.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}

public sealed class TurtleQuestCookbook
{
    private readonly IReadOnlyList<object> _examples;

    private TurtleQuestCookbook(IReadOnlyList<object> examples)
    {
        _examples = examples;
    }

    public static TurtleQuestCookbook Load()
    {
        var examples = new List<object>();
        var root = TurtleQuestPaths.FindRepositoryRoot();
        var examplesDirectory = root is null ? null : Path.Combine(root, "examples");
        if (examplesDirectory is not null && Directory.Exists(examplesDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(examplesDirectory, "*.json"))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var rootElement = document.RootElement;
                examples.Add(new
                {
                    Id = rootElement.TryGetProperty("id", out var id) ? id.GetString() : Path.GetFileNameWithoutExtension(file),
                    Goal = rootElement.TryGetProperty("goal", out var goal) ? goal.GetString() : "",
                    BehaviorId = rootElement.TryGetProperty("behaviorId", out var behaviorId) ? behaviorId.GetString() : ""
                });
            }
        }

        return new TurtleQuestCookbook(examples);
    }

    public IReadOnlyList<object> Snapshot() => _examples;
}

public static class TurtlePlanCompiler
{
    private static readonly string[] SupportedPrimitiveActionList =
    [
        "startBehavior",
        "inspect",
        "inspectUp",
        "inspectDown",
        "moveForward",
        "moveBackward",
        "moveUp",
        "moveDown",
        "face",
        "moveTowardRelative",
        "recoverToGround",
        "markWaypoint",
        "returnToPosition",
        "tunnelLine",
        "branchTunnel",
        "branchMinePattern",
        "turnLeft",
        "turnRight",
        "dig",
        "digUp",
        "digDown",
        "digRememberedTarget",
        "fellRememberedTree",
        "place",
        "placeUp",
        "placeDown",
        "placeStorage",
        "selectSlot",
        "getInventory",
        "discardJunk",
        "drop",
        "dropUp",
        "dropDown",
        "suck",
        "suckUp",
        "suckDown",
        "craft",
        "detect",
        "detectUp",
        "detectDown",
        "depositInventory",
        "scanNearby",
        "returnHome",
        "emitStatus",
        "completeObjective"
    ];

    private static readonly HashSet<string> LegalPrimitiveActions = new(
        SupportedPrimitiveActionList,
        StringComparer.Ordinal);

    public static IReadOnlyList<string> SupportedPrimitiveActions => SupportedPrimitiveActionList;

    public static bool IsKnownCompiledBehavior(string behaviorId) =>
        behaviorId is TurtleBehaviorIds.ExcavateRectangularPit
            or TurtleBehaviorIds.TunnelLine
            or TurtleBehaviorIds.BranchTunnel
            or TurtleBehaviorIds.BranchMinePattern
            or TurtleBehaviorIds.BootstrapHomeStorage
            or TurtleBehaviorIds.DepositInventory
            or TurtleBehaviorIds.BuildColumn
            or TurtleBehaviorIds.FindTree
            or TurtleBehaviorIds.HarvestTree
            or TurtleBehaviorIds.RecoverTurtle;

    public static IReadOnlyList<string> ExecutionRules =>
    [
        "Return flattened primitive steps only; do not return repeat, while, branch, or macro nodes.",
        "The first step must be startBehavior.",
        "The final step must be completeObjective.",
        "Use only supported primitive actions.",
        "Stay within the command budget.",
        "If the goal cannot be compiled safely with supported primitives, ask for clarification or return a blocked plan."
    ];

    public static TurtleCompiledPlanValidation ValidatePlan(
        IReadOnlyList<TurtleCompiledPlanStep> steps,
        int commandBudget,
        IReadOnlyList<string>? preErrors = null)
    {
        return ValidateSteps(
            steps,
            commandBudget,
            RequireStartBehavior: true,
            RequireCompleteObjective: true,
            preErrors);
    }

    public static TurtleCompiledPlanValidation ValidateCompiledPlan(
        TurtleCompiledPlan plan,
        int commandBudget,
        IReadOnlyList<string>? preErrors = null)
    {
        var validation = ValidatePlan(plan.Steps, commandBudget, preErrors);
        var errors = validation.Errors.ToList();
        var warnings = validation.Warnings.ToList();

        if (plan.BehaviorId == TurtleBehaviorIds.BuildColumn)
        {
            ValidateBuildColumn(plan, errors, warnings);
        }
        else if (plan.BehaviorId == TurtleBehaviorIds.FindTree)
        {
            ValidateFindTree(plan, errors, warnings);
        }
        else if (plan.BehaviorId == TurtleBehaviorIds.HarvestTree)
        {
            ValidateHarvestTree(plan, errors, warnings);
        }
        else if (plan.BehaviorId == TurtleBehaviorIds.RecoverTurtle)
        {
            ValidateRecoverTurtle(plan, errors, warnings);
        }
        else if (plan.BehaviorId == TurtleBehaviorIds.TunnelLine)
        {
            ValidateTunnelLine(plan, errors, warnings);
        }
        else if (plan.BehaviorId == TurtleBehaviorIds.BranchTunnel)
        {
            ValidateSingleHostPrimitive(plan, "branchTunnel", errors);
        }
        else if (plan.BehaviorId == TurtleBehaviorIds.BranchMinePattern)
        {
            ValidateSingleHostPrimitive(plan, "branchMinePattern", errors);
        }
        else if (plan.BehaviorId == TurtleBehaviorIds.BootstrapHomeStorage)
        {
            ValidateSingleHostPrimitive(plan, "placeStorage", errors);
        }
        else if (plan.BehaviorId == TurtleBehaviorIds.DepositInventory)
        {
            ValidateSingleHostPrimitive(plan, "depositInventory", errors);
        }

        return new TurtleCompiledPlanValidation(
            errors.Count == 0,
            validation.CommandCount,
            validation.CommandBudget,
            errors,
            warnings);
    }

    public static TurtleCompiledPlanValidation ValidateContinuationPlan(
        IReadOnlyList<TurtleCompiledPlanStep> steps,
        int commandBudget,
        IReadOnlyList<string>? preErrors = null)
    {
        return ValidateSteps(
            steps,
            commandBudget,
            RequireStartBehavior: false,
            RequireCompleteObjective: true,
            preErrors);
    }

    private static TurtleCompiledPlanValidation ValidateSteps(
        IReadOnlyList<TurtleCompiledPlanStep> steps,
        int commandBudget,
        bool RequireStartBehavior,
        bool RequireCompleteObjective,
        IReadOnlyList<string>? preErrors = null)
    {
        var errors = new List<string>(preErrors ?? []);
        var warnings = new List<string>();

        if (commandBudget <= 0)
        {
            errors.Add("Command budget must be greater than zero.");
        }

        if (steps.Count == 0)
        {
            errors.Add("Compiled plan contains no executable steps.");
        }

        if (steps.Count > commandBudget)
        {
            errors.Add($"Compiled plan has {steps.Count} commands, exceeding budget {commandBudget}.");
        }

        if (RequireStartBehavior &&
            steps.Count > 0 &&
            !string.Equals(steps[0].Action, "startBehavior", StringComparison.Ordinal))
        {
            errors.Add("Compiled plan must start with startBehavior.");
        }

        var completeObjectiveIndexes = new List<int>();
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (!LegalPrimitiveActions.Contains(step.Action))
            {
                errors.Add($"Illegal or unsupported primitive action at step {index}: {step.Action}.");
            }

            if (string.Equals(step.Action, "completeObjective", StringComparison.Ordinal))
            {
                completeObjectiveIndexes.Add(index);
            }
        }

        if (RequireCompleteObjective && steps.Count > 0 && completeObjectiveIndexes.Count == 0)
        {
            errors.Add("Compiled plan must finish with completeObjective.");
        }
        else if (completeObjectiveIndexes.Count > 1)
        {
            errors.Add("Compiled plan may contain only one completeObjective step.");
        }
        else if (completeObjectiveIndexes.Count == 1 && completeObjectiveIndexes[0] != steps.Count - 1)
        {
            errors.Add("completeObjective must be the final step.");
        }

        return new TurtleCompiledPlanValidation(
            errors.Count == 0,
            steps.Count,
            commandBudget,
            errors,
            warnings);
    }

    private static void ValidateBuildColumn(
        TurtleCompiledPlan plan,
        List<string> errors,
        List<string> warnings)
    {
        var height = plan.Arguments.TryGetValue("height", out var heightValue) && heightValue is not null
            ? ToInt32(heightValue)
            : 0;
        if (height <= 0)
        {
            errors.Add("build_column requires a positive height argument.");
            return;
        }

        var moveUpCount = plan.Steps.Count(step => step.Action == "moveUp");
        var placeDownCount = plan.Steps.Count(step => step.Action == "placeDown");
        var placeUpCount = plan.Steps.Count(step => step.Action == "placeUp");
        if (placeUpCount > 0)
        {
            errors.Add("build_column must not use placeUp; use moveUp then placeDown so the turtle does not block its own movement.");
        }

        if (moveUpCount != height)
        {
            errors.Add($"build_column expected {height} moveUp step(s), found {moveUpCount}.");
        }

        if (placeDownCount != height)
        {
            errors.Add($"build_column expected {height} placeDown step(s), found {placeDownCount}.");
        }

        var workSteps = plan.Steps
            .Where(step => step.Action is "inspectUp" or "moveUp" or "placeDown")
            .Select(step => step.Action)
            .ToArray();
        for (var i = 0; i < workSteps.Length; i += 3)
        {
            if (i + 2 >= workSteps.Length ||
                workSteps[i] != "inspectUp" ||
                workSteps[i + 1] != "moveUp" ||
                workSteps[i + 2] != "placeDown")
            {
                errors.Add("build_column work steps must repeat inspectUp, moveUp, then placeDown for each layer.");
                break;
            }
        }

        if (!plan.Steps.Any(step => step.Action == "selectSlot"))
        {
            warnings.Add("build_column plan does not select an inventory slot before placement.");
        }
    }

    private static void ValidateFindTree(
        TurtleCompiledPlan plan,
        List<string> errors,
        List<string> warnings)
    {
        var scanCount = plan.Steps.Count(step => step.Action == "scanNearby");
        if (scanCount == 0)
        {
            errors.Add("find_tree requires at least one scanNearby step.");
        }

        if (plan.Steps.Any(step => step.Action is "dig" or "digUp" or "digDown" or "moveForward" or "moveBackward" or "moveUp" or "moveDown"))
        {
            warnings.Add("find_tree should only gather evidence; harvest and navigation belong to later behavior slices.");
        }
    }

    private static void ValidateHarvestTree(
        TurtleCompiledPlan plan,
        List<string> errors,
        List<string> warnings)
    {
        var scanCount = plan.Steps.Count(step => step.Action == "scanNearby");
        var moveTowardCount = plan.Steps.Count(step => step.Action == "moveTowardRelative");
        var rememberedDigCount = plan.Steps.Count(step => step.Action == "digRememberedTarget");
        var fellCount = plan.Steps.Count(step => step.Action == "fellRememberedTree");
        var returnHomeCount = plan.Steps.Count(step => step.Action == "returnHome");
        var inventoryCount = plan.Steps.Count(step => step.Action == "getInventory");
        var targetCount = PlanTargetCount(plan.Arguments);
        if (scanCount == 0)
        {
            errors.Add("harvest_tree stage one requires scanNearby before movement.");
        }

        if (scanCount < targetCount)
        {
            errors.Add($"harvest_tree targetCount={targetCount} requires at least {targetCount} scanNearby step(s).");
        }

        if (moveTowardCount < targetCount)
        {
            errors.Add($"harvest_tree targetCount={targetCount} requires at least {targetCount} moveTowardRelative step(s).");
        }

        if (rememberedDigCount < targetCount)
        {
            errors.Add($"harvest_tree targetCount={targetCount} requires at least {targetCount} digRememberedTarget step(s).");
        }

        if (fellCount < targetCount)
        {
            errors.Add($"harvest_tree targetCount={targetCount} requires at least {targetCount} fellRememberedTree step(s).");
        }

        if (returnHomeCount == 0)
        {
            errors.Add("harvest_tree requires returnHome before completeObjective.");
        }

        if (inventoryCount == 0)
        {
            warnings.Add("harvest_tree should include getInventory after felling so collection evidence is visible in the trace.");
        }

        if (plan.Steps.Any(step => step.Action is "dig" or "digUp" or "digDown"))
        {
            warnings.Add("harvest_tree should use digRememberedTarget instead of raw dig primitives for target-bound cutting.");
        }
    }

    private static void ValidateRecoverTurtle(
        TurtleCompiledPlan plan,
        List<string> errors,
        List<string> warnings)
    {
        var recoverCount = plan.Steps.Count(step => step.Action == "recoverToGround");
        if (recoverCount == 0)
        {
            errors.Add("recover_turtle requires one recoverToGround step.");
        }

        if (recoverCount > 1)
        {
            warnings.Add("recover_turtle should normally use one recoverToGround step; the executor owns the bounded descent loop.");
        }

        if (plan.Steps.Any(step => step.Action is "dig" or "digUp" or "digDown" or "moveForward" or "moveUp" or "moveDown"))
        {
            warnings.Add("recover_turtle should delegate descent to recoverToGround instead of raw movement or digging primitives.");
        }
    }

    private static void ValidateTunnelLine(
        TurtleCompiledPlan plan,
        List<string> errors,
        List<string> warnings)
    {
        var tunnelCount = plan.Steps.Count(step => step.Action == "tunnelLine");
        if (tunnelCount != 1)
        {
            errors.Add($"tunnel_line requires exactly one tunnelLine primitive, found {tunnelCount}.");
        }

        if (!plan.Steps.Any(step => step.Action == "getInventory"))
        {
            warnings.Add("tunnel_line should include getInventory before or after tunneling so inventory pressure is visible.");
        }

        if (plan.Arguments.TryGetValue("height", out var heightValue) && heightValue is not null && ToInt32(heightValue) > 2)
        {
            warnings.Add("tunnel_line v0 executes height 1 or 2; larger tunnel profiles should compile to later mining primitives.");
        }

        if (plan.Arguments.TryGetValue("height", out var lowHeightValue) && lowHeightValue is not null && ToInt32(lowHeightValue) < TurtleEnvironmentProfile.Default.MinimumPlayerTunnelHeight)
        {
            warnings.Add("tunnel_line below two blocks high is turtle-only clearance; player-traversable mining routes should use height=2.");
        }
    }

    private static void ValidateSingleHostPrimitive(TurtleCompiledPlan plan, string action, List<string> errors)
    {
        var count = plan.Steps.Count(step => step.Action == action);
        if (count != 1)
        {
            errors.Add($"{plan.BehaviorId} requires exactly one {action} primitive, found {count}.");
        }
    }

    private static int ToInt32(object value) =>
        value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            double doubleValue => checked((int)doubleValue),
            decimal decimalValue => checked((int)decimalValue),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue) => intValue,
            JsonElement element when element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var intValue) => intValue,
            string stringValue when int.TryParse(stringValue, out var intValue) => intValue,
            IConvertible convertible => Convert.ToInt32(convertible, CultureInfo.InvariantCulture),
            _ => 0
        };

    private static int PlanTargetCount(IReadOnlyDictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue("targetCount", out var targetCountValue) && targetCountValue is not null)
        {
            return Math.Clamp(ToInt32(targetCountValue), 1, 8);
        }

        if (arguments.TryGetValue("count", out var countValue) && countValue is not null)
        {
            return Math.Clamp(ToInt32(countValue), 1, 8);
        }

        if (arguments.TryGetValue("trees", out var treesValue) && treesValue is not null)
        {
            return Math.Clamp(ToInt32(treesValue), 1, 8);
        }

        return 1;
    }

    public static TurtleCompiledPlan Compile(
        TurtleUserRequest request,
        TurtleBehavior behavior,
        TurtleBehaviorCatalog behaviorCatalog)
    {
        if (behavior.BehaviorId == TurtleBehaviorIds.BuildColumn)
        {
            return CompileColumn(behavior);
        }

        if (behavior.BehaviorId == TurtleBehaviorIds.FindTree)
        {
            return CompileFindTree(behavior);
        }

        if (behavior.BehaviorId == TurtleBehaviorIds.HarvestTree)
        {
            return CompileHarvestTreeStageOne(behavior);
        }

        if (behavior.BehaviorId == TurtleBehaviorIds.RecoverTurtle)
        {
            return CompileRecoverTurtle(behavior);
        }

        if (behavior.BehaviorId == TurtleBehaviorIds.TunnelLine)
        {
            return CompileTunnelLine(behavior);
        }

        if (behavior.BehaviorId == TurtleBehaviorIds.BranchTunnel)
        {
            return CompileBranchTunnel(behavior);
        }

        if (behavior.BehaviorId == TurtleBehaviorIds.BranchMinePattern)
        {
            return CompileBranchMinePattern(behavior);
        }

        if (behavior.BehaviorId == TurtleBehaviorIds.BootstrapHomeStorage)
        {
            return CompileBootstrapHomeStorage(behavior);
        }

        if (behavior.BehaviorId == TurtleBehaviorIds.DepositInventory)
        {
            return CompileDepositInventory(behavior);
        }

        if (behaviorCatalog.TryExpand("plan-preview", behavior, Command, out var catalogCommands))
        {
            var steps = catalogCommands
                .Select(command => new TurtleCompiledPlanStep(command.Action, command.Arguments))
                .ToArray();
            return Plan(
                "catalog",
                behavior.BehaviorId,
                "catalog",
                behavior.Arguments,
                steps,
                behavior.CommandBudget);
        }

        if (behavior.BehaviorId == TurtleBehaviorIds.ExcavateRectangularPit)
        {
            return CompilePit(request, behavior);
        }

        return Plan(
            "unsupported",
            behavior.BehaviorId,
            "deterministic_baseline",
            behavior.Arguments,
            [new TurtleCompiledPlanStep("emitStatus", new Dictionary<string, object?>
            {
                ["message"] = $"No compile path is available for {behavior.BehaviorId}."
            })],
            behavior.CommandBudget);
    }

    private static TurtleCompiledPlan CompilePit(TurtleUserRequest request, TurtleBehavior behavior)
    {
        var errors = new List<string>();
        if (!behavior.Arguments.TryGetValue("width", out var widthValue))
        {
            errors.Add("Missing width.");
        }

        if (!behavior.Arguments.TryGetValue("length", out var lengthValue))
        {
            errors.Add("Missing length.");
        }

        if (!behavior.Arguments.TryGetValue("depth", out var depthValue))
        {
            errors.Add("Missing depth.");
        }

        if (errors.Count > 0)
        {
            return Plan(
                "needs_clarification",
                behavior.BehaviorId,
                "deterministic_baseline",
                behavior.Arguments,
                [],
                behavior.CommandBudget,
                errors);
        }

        var width = Convert.ToInt32(widthValue);
        var length = Convert.ToInt32(lengthValue);
        var depth = Convert.ToInt32(depthValue);
        var returnHome = behavior.Arguments.TryGetValue("returnHome", out var returnValue) &&
            returnValue is bool shouldReturn &&
            shouldReturn;

        if (width > 7 || length > 7 || depth > 1)
        {
            errors.Add("Deterministic pit compiler v0 supports width <= 7, length <= 7, and depth = 1. Larger or deeper pits are reserved for LLM-backed planning.");
        }

        if (returnHome)
        {
            errors.Add("Deterministic pit compiler v0 does not yet compile breadcrumb return. This is the first LLM-backed execution boundary.");
        }

        if (errors.Count > 0)
        {
            return Plan(
                "compile_blocked",
                behavior.BehaviorId,
                "deterministic_baseline",
                behavior.Arguments,
                [],
                behavior.CommandBudget,
                errors);
        }

        var steps = new List<TurtleCompiledPlanStep>
        {
            new("startBehavior", new Dictionary<string, object?>
            {
                ["behaviorId"] = behavior.BehaviorId,
                ["arguments"] = behavior.Arguments
            })
        };

        for (var row = 0; row < length; row++)
        {
            for (var column = 0; column < width; column++)
            {
                steps.Add(new TurtleCompiledPlanStep("inspectDown", new Dictionary<string, object?>()));
                steps.Add(new TurtleCompiledPlanStep("digDown", new Dictionary<string, object?>()));
                if (column < width - 1)
                {
                    steps.Add(new TurtleCompiledPlanStep("moveForward", new Dictionary<string, object?>()));
                }
            }

            if (row < length - 1)
            {
                var turn = row % 2 == 0 ? "turnRight" : "turnLeft";
                steps.Add(new TurtleCompiledPlanStep(turn, new Dictionary<string, object?>()));
                steps.Add(new TurtleCompiledPlanStep("moveForward", new Dictionary<string, object?>()));
                steps.Add(new TurtleCompiledPlanStep(turn, new Dictionary<string, object?>()));
            }
        }

        steps.Add(new TurtleCompiledPlanStep("completeObjective", new Dictionary<string, object?>
        {
            ["artifactKind"] = "turtlequest.objective_completed"
        }));

        return Plan(
            "compiled_behavior",
            behavior.BehaviorId,
            "deterministic_baseline",
            behavior.Arguments,
            steps,
            behavior.CommandBudget);
    }

    private static TurtleCompiledPlan CompileRecoverTurtle(TurtleBehavior behavior)
    {
        var maxDown = behavior.Arguments.TryGetValue("maxDown", out var maxDownValue) && maxDownValue is not null
            ? Math.Clamp(Convert.ToInt32(maxDownValue, CultureInfo.InvariantCulture), 1, 64)
            : 32;
        var returnHome = behavior.Arguments.TryGetValue("returnHome", out var returnValue) &&
            returnValue is bool shouldReturn &&
            shouldReturn;

        var steps = new List<TurtleCompiledPlanStep>
        {
            new("startBehavior", new Dictionary<string, object?>
            {
                ["behaviorId"] = behavior.BehaviorId,
                ["arguments"] = behavior.Arguments
            }),
            new("recoverToGround", new Dictionary<string, object?>
            {
                ["maxDown"] = maxDown,
                ["digSoftBelow"] = true
            })
        };

        if (returnHome)
        {
            steps.Add(new TurtleCompiledPlanStep("returnHome", new Dictionary<string, object?>
            {
                ["mode"] = "breadcrumbs",
                ["optional"] = true
            }));
        }

        steps.Add(new TurtleCompiledPlanStep("emitStatus", new Dictionary<string, object?>
        {
            ["stage"] = returnHome ? "recovered_to_ground_return_attempted" : "recovered_to_ground",
            ["behaviorId"] = behavior.BehaviorId
        }));
        steps.Add(new TurtleCompiledPlanStep("completeObjective", new Dictionary<string, object?>
        {
            ["artifactKind"] = "turtlequest.objective_completed",
            ["stage"] = "recovered_to_ground"
        }));

        return Plan(
            "compiled_behavior",
            behavior.BehaviorId,
            "deterministic_baseline",
            behavior.Arguments,
            steps,
            behavior.CommandBudget);
    }

    private static TurtleCompiledPlan CompileTunnelLine(TurtleBehavior behavior)
    {
        var length = behavior.Arguments.TryGetValue("length", out var lengthValue) && lengthValue is not null
            ? Math.Clamp(Convert.ToInt32(lengthValue, CultureInfo.InvariantCulture), 1, 64)
            : 6;
        var height = behavior.Arguments.TryGetValue("height", out var heightValue) && heightValue is not null
            ? Math.Clamp(Convert.ToInt32(heightValue, CultureInfo.InvariantCulture), 1, 2)
            : 2;
        var returnHome = !behavior.Arguments.TryGetValue("returnHome", out var returnValue) ||
            returnValue is not bool shouldReturn ||
            shouldReturn;
        var routeId = behavior.Arguments.TryGetValue("routeId", out var routeValue) && routeValue is not null
            ? Convert.ToString(routeValue, CultureInfo.InvariantCulture)
            : $"route-{Guid.NewGuid():N}";

        var steps = new List<TurtleCompiledPlanStep>
        {
            new("startBehavior", new Dictionary<string, object?>
            {
                ["behaviorId"] = behavior.BehaviorId,
                ["arguments"] = behavior.Arguments
            }),
            new("emitStatus", new Dictionary<string, object?>
            {
                ["stage"] = "planning",
                ["behaviorId"] = behavior.BehaviorId
            }),
            new("getInventory", new Dictionary<string, object?>()),
            new("emitStatus", new Dictionary<string, object?>
            {
                ["stage"] = "tunneling",
                ["behaviorId"] = behavior.BehaviorId,
                ["length"] = length,
                ["height"] = height
            }),
            new("tunnelLine", new Dictionary<string, object?>
            {
                ["length"] = length,
                ["height"] = height,
                ["routeId"] = routeId
            }),
            new("emitStatus", new Dictionary<string, object?>
            {
                ["stage"] = "inventory_pressure_check",
                ["behaviorId"] = behavior.BehaviorId
            }),
            new("getInventory", new Dictionary<string, object?>())
        };

        if (returnHome)
        {
            steps.Add(new TurtleCompiledPlanStep("emitStatus", new Dictionary<string, object?>
            {
                ["stage"] = "returning",
                ["behaviorId"] = behavior.BehaviorId
            }));
            steps.Add(new TurtleCompiledPlanStep("returnHome", new Dictionary<string, object?>
            {
                ["mode"] = "breadcrumbs"
            }));
        }

        steps.Add(new TurtleCompiledPlanStep("emitStatus", new Dictionary<string, object?>
        {
            ["stage"] = returnHome ? "tunnel_line_returned" : "tunnel_line_completed",
            ["behaviorId"] = behavior.BehaviorId
        }));
        steps.Add(new TurtleCompiledPlanStep("completeObjective", new Dictionary<string, object?>
        {
            ["artifactKind"] = "turtlequest.objective_completed",
            ["stage"] = returnHome ? "tunnel_line_returned" : "tunnel_line_completed"
        }));

        return Plan(
            "compiled_behavior",
            behavior.BehaviorId,
            "deterministic_baseline",
            behavior.Arguments,
            steps,
            behavior.CommandBudget);
    }

    private static TurtleCompiledPlan CompileBranchTunnel(TurtleBehavior behavior)
    {
        var length = IntArgument(behavior, "length", 6, 1, 64);
        var height = IntArgument(behavior, "height", 2, 1, 2);
        var side = StringArgument(behavior, "side", "left");
        var routeId = StringArgument(behavior, "routeId", $"route-branch-{Guid.NewGuid():N}");
        var steps = new List<TurtleCompiledPlanStep>
        {
            new("startBehavior", new Dictionary<string, object?> { ["behaviorId"] = behavior.BehaviorId, ["arguments"] = behavior.Arguments }),
            new("markWaypoint", new Dictionary<string, object?> { ["name"] = "branch_origin" }),
            new("emitStatus", new Dictionary<string, object?> { ["stage"] = "branch_started", ["behaviorId"] = behavior.BehaviorId, ["side"] = side, ["length"] = length }),
            new("branchTunnel", new Dictionary<string, object?>
            {
                ["side"] = side,
                ["length"] = length,
                ["height"] = height,
                ["routeId"] = routeId,
                ["returnToOrigin"] = true
            }),
            new("emitStatus", new Dictionary<string, object?> { ["stage"] = "branch_returned_to_origin", ["behaviorId"] = behavior.BehaviorId }),
            new("completeObjective", new Dictionary<string, object?> { ["artifactKind"] = "turtlequest.objective_completed", ["stage"] = "branch_returned_to_origin" })
        };

        return Plan("compiled_behavior", behavior.BehaviorId, "deterministic_baseline", behavior.Arguments, steps, behavior.CommandBudget);
    }

    private static TurtleCompiledPlan CompileBranchMinePattern(TurtleBehavior behavior)
    {
        var mainLength = IntArgument(behavior, "mainLength", 9, 1, 64);
        var branchLength = IntArgument(behavior, "branchLength", 6, 1, 64);
        var branchCount = IntArgument(behavior, "branchCount", 2, 1, 8);
        var spacing = IntArgument(behavior, "spacing", 3, 1, 16);
        var height = IntArgument(behavior, "height", 2, 1, 2);
        var mainRouteId = StringArgument(behavior, "mainRouteId", $"route-main-{Guid.NewGuid():N}");
        var steps = new List<TurtleCompiledPlanStep>
        {
            new("startBehavior", new Dictionary<string, object?> { ["behaviorId"] = behavior.BehaviorId, ["arguments"] = behavior.Arguments }),
            new("markWaypoint", new Dictionary<string, object?> { ["name"] = "branch_mine_start" }),
            new("getInventory", new Dictionary<string, object?>()),
            new("emitStatus", new Dictionary<string, object?>
            {
                ["stage"] = "branch_mine_started",
                ["behaviorId"] = behavior.BehaviorId,
                ["mainLength"] = mainLength,
                ["branchLength"] = branchLength,
                ["branchCount"] = branchCount
            }),
            new("branchMinePattern", new Dictionary<string, object?>
            {
                ["mainLength"] = mainLength,
                ["branchLength"] = branchLength,
                ["branchCount"] = branchCount,
                ["spacing"] = spacing,
                ["height"] = height,
                ["mainRouteId"] = mainRouteId,
                ["sidePattern"] = StringArgument(behavior, "sidePattern", "alternating"),
                ["returnHome"] = BoolArgument(behavior, "returnHome", true)
            }),
            new("getInventory", new Dictionary<string, object?>()),
            new("emitStatus", new Dictionary<string, object?> { ["stage"] = "branch_mine_completed", ["behaviorId"] = behavior.BehaviorId }),
            new("completeObjective", new Dictionary<string, object?> { ["artifactKind"] = "turtlequest.objective_completed", ["stage"] = "branch_mine_completed" })
        };

        return Plan("compiled_behavior", behavior.BehaviorId, "deterministic_baseline", behavior.Arguments, steps, behavior.CommandBudget);
    }

    private static TurtleCompiledPlan CompileBootstrapHomeStorage(TurtleBehavior behavior)
    {
        var storageKind = StringArgument(behavior, "storageKind", "barrel");
        var waypointName = StringArgument(behavior, "waypointName", "home_storage");
        var placement = StringArgument(behavior, "placement", "front");
        var steps = new List<TurtleCompiledPlanStep>
        {
            new("startBehavior", new Dictionary<string, object?> { ["behaviorId"] = behavior.BehaviorId, ["arguments"] = behavior.Arguments }),
            new("emitStatus", new Dictionary<string, object?> { ["stage"] = "storage_preflight", ["behaviorId"] = behavior.BehaviorId }),
            new("getInventory", new Dictionary<string, object?>()),
            new("placeStorage", new Dictionary<string, object?>
            {
                ["storageKind"] = storageKind,
                ["waypointName"] = waypointName,
                ["placement"] = placement
            }),
            new("emitStatus", new Dictionary<string, object?> { ["stage"] = "home_storage_recorded", ["behaviorId"] = behavior.BehaviorId, ["waypointName"] = waypointName }),
            new("completeObjective", new Dictionary<string, object?> { ["artifactKind"] = "turtlequest.objective_completed", ["stage"] = "home_storage_ready" })
        };

        return Plan("compiled_behavior", behavior.BehaviorId, "deterministic_baseline", behavior.Arguments, steps, behavior.CommandBudget);
    }

    private static TurtleCompiledPlan CompileDepositInventory(TurtleBehavior behavior)
    {
        var direction = StringArgument(behavior, "direction", "front");
        var steps = new List<TurtleCompiledPlanStep>
        {
            new("startBehavior", new Dictionary<string, object?> { ["behaviorId"] = behavior.BehaviorId, ["arguments"] = behavior.Arguments }),
            new("emitStatus", new Dictionary<string, object?> { ["stage"] = "deposit_preflight", ["behaviorId"] = behavior.BehaviorId }),
            new("getInventory", new Dictionary<string, object?>()),
            new("depositInventory", new Dictionary<string, object?>
            {
                ["direction"] = direction,
                ["keepSelected"] = BoolArgument(behavior, "keepSelected", true)
            }),
            new("getInventory", new Dictionary<string, object?>()),
            new("emitStatus", new Dictionary<string, object?> { ["stage"] = "deposit_completed", ["behaviorId"] = behavior.BehaviorId }),
            new("completeObjective", new Dictionary<string, object?> { ["artifactKind"] = "turtlequest.objective_completed", ["stage"] = "deposit_completed" })
        };

        return Plan("compiled_behavior", behavior.BehaviorId, "deterministic_baseline", behavior.Arguments, steps, behavior.CommandBudget);
    }

    private static int IntArgument(TurtleBehavior behavior, string name, int fallback, int minimum, int maximum) =>
        behavior.Arguments.TryGetValue(name, out var value) && value is not null
            ? Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), minimum, maximum)
            : fallback;

    private static string StringArgument(TurtleBehavior behavior, string name, string fallback) =>
        behavior.Arguments.TryGetValue(name, out var value) && value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))
            ? Convert.ToString(value, CultureInfo.InvariantCulture)!
            : fallback;

    private static bool BoolArgument(TurtleBehavior behavior, string name, bool fallback) =>
        behavior.Arguments.TryGetValue(name, out var value) && value is not null
            ? value is bool boolValue ? boolValue : bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) ? parsed : fallback
            : fallback;

    private static TurtleCompiledPlan CompileColumn(TurtleBehavior behavior)
    {
        var errors = new List<string>();
        if (!behavior.Arguments.TryGetValue("height", out var heightValue))
        {
            errors.Add("Missing height.");
        }

        var height = heightValue is null ? 0 : Convert.ToInt32(heightValue, CultureInfo.InvariantCulture);
        if (height < 1 || height > 16)
        {
            errors.Add("Build column compiler v0 supports height 1 through 16.");
        }

        if (errors.Count > 0)
        {
            return Plan(
                "compile_blocked",
                behavior.BehaviorId,
                "deterministic_baseline",
                behavior.Arguments,
                [],
                behavior.CommandBudget,
                errors);
        }

        var slot = behavior.Arguments.TryGetValue("slot", out var slotValue) && slotValue is not null
            ? Convert.ToInt32(slotValue, CultureInfo.InvariantCulture)
            : 1;
        var steps = new List<TurtleCompiledPlanStep>
        {
            new("startBehavior", new Dictionary<string, object?>
            {
                ["behaviorId"] = behavior.BehaviorId,
                ["arguments"] = behavior.Arguments
            }),
            new("selectSlot", new Dictionary<string, object?>
            {
                ["slot"] = slot
            }),
            new("getInventory", new Dictionary<string, object?>())
        };

        for (var i = 0; i < height; i++)
        {
            steps.Add(new TurtleCompiledPlanStep("inspectUp", new Dictionary<string, object?>()));
            steps.Add(new TurtleCompiledPlanStep("moveUp", new Dictionary<string, object?>()));
            steps.Add(new TurtleCompiledPlanStep("placeDown", new Dictionary<string, object?>()));
            steps.Add(new TurtleCompiledPlanStep("emitStatus", new Dictionary<string, object?>
            {
                ["stage"] = "work_progress",
                ["behaviorId"] = behavior.BehaviorId,
                ["completedLayers"] = i + 1,
                ["totalLayers"] = height
            }));
        }

        steps.Add(new TurtleCompiledPlanStep("completeObjective", new Dictionary<string, object?>
        {
            ["artifactKind"] = "turtlequest.objective_completed"
        }));

        return Plan(
            "compiled_behavior",
            behavior.BehaviorId,
            "deterministic_baseline",
            behavior.Arguments,
            steps,
            behavior.CommandBudget);
    }

    private static TurtleCompiledPlan CompileFindTree(TurtleBehavior behavior)
    {
        var radius = behavior.Arguments.TryGetValue("radius", out var radiusValue) && radiusValue is not null
            ? Math.Clamp(Convert.ToInt32(radiusValue, CultureInfo.InvariantCulture), 1, 16)
            : 12;

        var steps = new List<TurtleCompiledPlanStep>
        {
            new("startBehavior", new Dictionary<string, object?>
            {
                ["behaviorId"] = behavior.BehaviorId,
                ["arguments"] = behavior.Arguments
            }),
            new("scanNearby", new Dictionary<string, object?>
            {
                ["radius"] = radius,
                ["tag"] = "minecraft:logs",
                ["blockContains"] = "_log",
                ["maxMatches"] = 16
            }),
            new("emitStatus", new Dictionary<string, object?>
            {
                ["stage"] = "scan_complete",
                ["behaviorId"] = behavior.BehaviorId,
                ["target"] = "minecraft:logs"
            }),
            new("completeObjective", new Dictionary<string, object?>
            {
                ["artifactKind"] = "turtlequest.objective_completed"
            })
        };

        return Plan(
            "compiled_behavior",
            behavior.BehaviorId,
            "deterministic_baseline",
            behavior.Arguments,
            steps,
            behavior.CommandBudget);
    }

    private static TurtleCompiledPlan CompileHarvestTreeStageOne(TurtleBehavior behavior)
    {
        var radius = behavior.Arguments.TryGetValue("radius", out var radiusValue) && radiusValue is not null
            ? Math.Clamp(Convert.ToInt32(radiusValue, CultureInfo.InvariantCulture), 1, 16)
            : 12;
        var budget = behavior.Arguments.TryGetValue("pathBudget", out var budgetValue) && budgetValue is not null
            ? Math.Clamp(Convert.ToInt32(budgetValue, CultureInfo.InvariantCulture), 1, 32)
            : radius;
        var targetCount = behavior.Arguments.TryGetValue("targetCount", out var targetCountValue) && targetCountValue is not null
            ? Math.Clamp(Convert.ToInt32(targetCountValue, CultureInfo.InvariantCulture), 1, 8)
            : 1;

        var steps = new List<TurtleCompiledPlanStep>
        {
            new("startBehavior", new Dictionary<string, object?>
            {
                ["behaviorId"] = behavior.BehaviorId,
                ["arguments"] = behavior.Arguments
            })
        };

        for (var i = 0; i < targetCount; i++)
        {
            steps.AddRange([
                new("scanNearby", new Dictionary<string, object?>
                {
                    ["radius"] = radius,
                    ["tag"] = "minecraft:logs",
                    ["blockContains"] = "_log",
                    ["maxMatches"] = 16
                }),
                new("moveTowardRelative", new Dictionary<string, object?>
                {
                    ["source"] = "lastScanNearest",
                    ["budget"] = budget,
                    ["stopAdjacent"] = true
                }),
                new("digRememberedTarget", new Dictionary<string, object?>
                {
                    ["source"] = "lastScanNearest",
                    ["expectedTag"] = "minecraft:logs"
                }),
                new("fellRememberedTree", new Dictionary<string, object?>
                {
                    ["maxHeight"] = 12,
                    ["expectedTag"] = "minecraft:logs"
                }),
                new("getInventory", new Dictionary<string, object?>()),
                new("returnHome", new Dictionary<string, object?>
                {
                    ["mode"] = "breadcrumbs"
                }),
                new("emitStatus", new Dictionary<string, object?>
                {
                    ["stage"] = i + 1 == targetCount ? "full_tree_felled_returned" : "tree_felled_returned",
                    ["behaviorId"] = behavior.BehaviorId,
                    ["target"] = "minecraft:logs",
                    ["completedTrees"] = i + 1,
                    ["targetTrees"] = targetCount
                })
            ]);
        }

        steps.Add(new TurtleCompiledPlanStep("completeObjective", new Dictionary<string, object?>
        {
            ["artifactKind"] = "turtlequest.objective_completed",
            ["stage"] = "full_tree_felled_returned",
            ["targetTrees"] = targetCount
        }));

        return Plan(
            "compiled_behavior",
            behavior.BehaviorId,
            "deterministic_baseline",
            behavior.Arguments,
            steps,
            behavior.CommandBudget);
    }

    private static TurtleCompiledPlan Plan(
        string planKind,
        string behaviorId,
        string source,
        IReadOnlyDictionary<string, object?> arguments,
        IReadOnlyList<TurtleCompiledPlanStep> steps,
        int commandBudget,
        IReadOnlyList<string>? preErrors = null)
    {
        var validation = ValidatePlan(steps, commandBudget, preErrors ?? []);
        return new TurtleCompiledPlan(
            $"plan-{Guid.NewGuid():N}",
            planKind,
            behaviorId,
            source,
            arguments,
            steps,
            validation);
    }

    private static NextTurtleCommand Command(
        string runId,
        string action,
        IReadOnlyDictionary<string, object?>? arguments = null) =>
        new(runId, $"cmd-{Guid.NewGuid():N}", action, arguments ?? new Dictionary<string, object?>());
}

public sealed class TurtleBehaviorCatalog
{
    private readonly Dictionary<string, TurtleBehaviorSpec> _behaviors;

    private TurtleBehaviorCatalog(Dictionary<string, TurtleBehaviorSpec> behaviors)
    {
        _behaviors = behaviors;
    }

    public static TurtleBehaviorCatalog Load()
    {
        var behaviors = new Dictionary<string, TurtleBehaviorSpec>(StringComparer.Ordinal);
        var root = TurtleQuestPaths.FindRepositoryRoot();
        var behaviorDirectory = root is null ? null : Path.Combine(root, "behaviors");

        if (behaviorDirectory is not null && Directory.Exists(behaviorDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(behaviorDirectory, "*.json"))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var rootElement = document.RootElement;
                var id = rootElement.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                behaviors[id] = new TurtleBehaviorSpec(
                    id,
                    ReadSteps(rootElement.GetProperty("steps")));
            }
        }

        return new TurtleBehaviorCatalog(behaviors);
    }

    public bool TryExpand(
        string runId,
        TurtleBehavior behavior,
        Func<string, string, IReadOnlyDictionary<string, object?>?, NextTurtleCommand> commandFactory,
        out IReadOnlyList<NextTurtleCommand> commands)
    {
        commands = [];
        if (!_behaviors.TryGetValue(behavior.BehaviorId, out var spec))
        {
            return false;
        }

        var expanded = new List<NextTurtleCommand>();
        ExpandSteps(runId, behavior, spec.Steps, commandFactory, expanded);
        commands = expanded;
        return true;
    }

    public bool Contains(string behaviorId) => _behaviors.ContainsKey(behaviorId);

    public IReadOnlyList<object> Snapshot() =>
        _behaviors.Values
            .OrderBy(behavior => behavior.Id, StringComparer.Ordinal)
            .Select(behavior => new
            {
                behavior.Id,
                StepCount = behavior.Steps.Count
            })
            .Cast<object>()
            .ToArray();

    private static IReadOnlyList<TurtleBehaviorStep> ReadSteps(JsonElement stepsElement)
    {
        var steps = new List<TurtleBehaviorStep>();
        foreach (var stepElement in stepsElement.EnumerateArray())
        {
            if (stepElement.TryGetProperty("action", out var actionElement))
            {
                steps.Add(new TurtleBehaviorStep(
                    actionElement.GetString() ?? "",
                    RepeatCount: null,
                    Steps: []));
                continue;
            }

            if (stepElement.TryGetProperty("repeat", out var repeatElement))
            {
                var count = repeatElement.GetProperty("count");
                var repeatCount = count.ValueKind == JsonValueKind.String
                    ? count.GetString()
                    : count.GetInt32().ToString();
                steps.Add(new TurtleBehaviorStep(
                    Action: null,
                    RepeatCount: repeatCount,
                    Steps: ReadSteps(repeatElement.GetProperty("steps"))));
            }
        }

        return steps;
    }

    private static void ExpandSteps(
        string runId,
        TurtleBehavior behavior,
        IReadOnlyList<TurtleBehaviorStep> steps,
        Func<string, string, IReadOnlyDictionary<string, object?>?, NextTurtleCommand> commandFactory,
        List<NextTurtleCommand> commands)
    {
        foreach (var step in steps)
        {
            if (!string.IsNullOrWhiteSpace(step.Action))
            {
                var arguments = step.Action == "startBehavior"
                    ? new Dictionary<string, object?>
                    {
                        ["behaviorRunId"] = behavior.BehaviorRunId,
                        ["behaviorId"] = behavior.BehaviorId,
                        ["arguments"] = behavior.Arguments
                    }
                    : step.Action == "completeObjective"
                        ? new Dictionary<string, object?> { ["artifactKind"] = "turtlequest.objective_completed" }
                        : null;
                commands.Add(commandFactory(runId, step.Action, arguments));
                continue;
            }

            var count = ResolveCount(step.RepeatCount, behavior);
            for (var i = 0; i < count; i++)
            {
                ExpandSteps(runId, behavior, step.Steps, commandFactory, commands);
            }
        }
    }

    private static int ResolveCount(string? count, TurtleBehavior behavior)
    {
        if (string.IsNullOrWhiteSpace(count))
        {
            return 0;
        }

        if (count.StartsWith('$') &&
            behavior.Arguments.TryGetValue(count[1..], out var value) &&
            value is not null)
        {
            return Math.Max(0, Convert.ToInt32(value));
        }

        return int.TryParse(count, out var parsed) ? Math.Max(0, parsed) : 0;
    }

    private sealed record TurtleBehaviorSpec(string Id, IReadOnlyList<TurtleBehaviorStep> Steps);

    private sealed record TurtleBehaviorStep(string? Action, string? RepeatCount, IReadOnlyList<TurtleBehaviorStep> Steps);
}

public static partial class TurtleBehaviorMatcher
{
    public static TurtleBehavior OpenGoal(string message) =>
        new(
            $"tqb-{Guid.NewGuid():N}",
            TurtleBehaviorIds.OpenGoal,
            new Dictionary<string, object?>
            {
                ["originalMessage"] = message,
                ["missionClass"] = "open_goal",
                ["plannerAuthority"] = "agentica_selects_behavior",
                ["returnHomeDefault"] = true
            },
            CommandBudget: 512);

    public static TurtleBehavior Match(string message)
    {
        var length = ExtractLength(message);
        var normalized = message.ToLowerInvariant();

        if (normalized.Contains("recover", StringComparison.Ordinal) ||
            normalized.Contains("unstuck", StringComparison.Ordinal) ||
            normalized.Contains("stuck", StringComparison.Ordinal) ||
            normalized.Contains("descend", StringComparison.Ordinal) ||
            normalized.Contains("rescue", StringComparison.Ordinal))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.RecoverTurtle,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "recovery",
                    ["maxDown"] = 32,
                    ["digSoftBelow"] = true,
                    ["returnHome"] = normalized.Contains("return", StringComparison.Ordinal) ||
                        normalized.Contains("home", StringComparison.Ordinal) ||
                        normalized.Contains("start", StringComparison.Ordinal)
                },
                CommandBudget: 24);
        }

        if (normalized.Contains("follow", StringComparison.Ordinal))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.Follow,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "mobility",
                    ["requiresLlmPlan"] = true,
                    ["blockedReason"] = "Follow needs live target tracking and movement policy."
                },
                CommandBudget: 256);
        }

        if (normalized.Contains("assist", StringComparison.Ordinal) ||
            normalized.Contains("help me", StringComparison.Ordinal) ||
            normalized.Contains("what can you do", StringComparison.Ordinal))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.Assist,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "diagnostic",
                    ["returnHome"] = false
                },
                CommandBudget: 16);
        }

        if ((normalized.Contains("tree", StringComparison.Ordinal) ||
             normalized.Contains("wood", StringComparison.Ordinal) ||
             normalized.Contains("log", StringComparison.Ordinal)) &&
            (normalized.Contains("harvest", StringComparison.Ordinal) ||
             normalized.Contains("chop", StringComparison.Ordinal) ||
             normalized.Contains("cut", StringComparison.Ordinal) ||
             normalized.Contains("treecap", StringComparison.Ordinal) ||
             normalized.Contains("gather", StringComparison.Ordinal) ||
             normalized.Contains("collect", StringComparison.Ordinal) ||
             normalized.Contains("fetch", StringComparison.Ordinal)))
        {
            var targetCount = ExtractResourceCount(message);
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.HarvestTree,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "resource_acquisition",
                    ["targetResource"] = "minecraft:logs",
                    ["radius"] = 12,
                    ["pathBudget"] = 12,
                    ["targetCount"] = targetCount,
                    ["stage"] = "full_tree_felling",
                    ["returnHome"] = true
                },
                CommandBudget: Math.Clamp(targetCount * 96, 96, 512));
        }

        if ((normalized.Contains("tree", StringComparison.Ordinal) ||
             normalized.Contains("wood", StringComparison.Ordinal) ||
             normalized.Contains("log", StringComparison.Ordinal)) &&
            (normalized.Contains("find", StringComparison.Ordinal) ||
             normalized.Contains("locate", StringComparison.Ordinal) ||
             normalized.Contains("scan", StringComparison.Ordinal) ||
             normalized.Contains("nearby", StringComparison.Ordinal)))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.FindTree,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "resource_scouting",
                    ["targetResource"] = "minecraft:logs",
                    ["radius"] = 12,
                    ["returnHome"] = false
                },
                CommandBudget: 24);
        }

        if (normalized.Contains("diamond", StringComparison.Ordinal) ||
            normalized.Contains("diamonds", StringComparison.Ordinal))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.FindDiamonds,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "resource_acquisition",
                    ["targetResource"] = "minecraft:diamond",
                    ["requiresLlmPlan"] = true
                },
                CommandBudget: 512);
        }

        if ((normalized.Contains("storage", StringComparison.Ordinal) ||
             normalized.Contains("barrel", StringComparison.Ordinal) ||
             normalized.Contains("chest", StringComparison.Ordinal)) &&
            (normalized.Contains("create", StringComparison.Ordinal) ||
             normalized.Contains("place", StringComparison.Ordinal) ||
             normalized.Contains("bootstrap", StringComparison.Ordinal) ||
             normalized.Contains("home", StringComparison.Ordinal) ||
             normalized.Contains("record", StringComparison.Ordinal)))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.BootstrapHomeStorage,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "storage_upkeep",
                    ["requirementId"] = "home_storage",
                    ["storageKind"] = normalized.Contains("chest", StringComparison.Ordinal) ? "chest" : "barrel",
                    ["waypointName"] = "home_storage",
                    ["placement"] = normalized.Contains("below", StringComparison.Ordinal) ? "down" : "front",
                    ["resumeRecommended"] = normalized.Contains("then", StringComparison.Ordinal)
                },
                CommandBudget: 32);
        }

        if (normalized.Contains("deposit", StringComparison.Ordinal) ||
            normalized.Contains("drop off", StringComparison.Ordinal) ||
            normalized.Contains("unload", StringComparison.Ordinal))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.DepositInventory,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "storage_upkeep",
                    ["direction"] = normalized.Contains("below", StringComparison.Ordinal) ? "down" : "front",
                    ["keepSelected"] = true
                },
                CommandBudget: 48);
        }

        if (normalized.Contains("column", StringComparison.Ordinal) ||
            normalized.Contains("pillar", StringComparison.Ordinal))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.BuildColumn,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "construction",
                    ["structureClass"] = "column",
                    ["height"] = Math.Clamp(length ?? 5, 1, 16),
                    ["slot"] = 1,
                    ["returnHome"] = false
                },
                CommandBudget: 96);
        }

        if (normalized.Contains("tower", StringComparison.Ordinal))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.BuildTower,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "construction",
                    ["structureClass"] = "tower",
                    ["requiresLlmPlan"] = true
                },
                CommandBudget: 512);
        }

        if (normalized.Contains("house", StringComparison.Ordinal) ||
            normalized.Contains("cabin", StringComparison.Ordinal) ||
            normalized.Contains("home", StringComparison.Ordinal))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.BuildHouse,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "construction",
                    ["structureClass"] = "house",
                    ["requiresLlmPlan"] = true
                },
                CommandBudget: 1024);
        }

        if ((normalized.Contains("pit", StringComparison.Ordinal) ||
             normalized.Contains("excavate", StringComparison.Ordinal)) &&
            (normalized.Contains("dig", StringComparison.Ordinal) ||
             normalized.Contains("excavate", StringComparison.Ordinal)))
        {
            var dimensions = ExtractRectangularDimensions(message);
            var depth = ExtractDepth(message);
            var arguments = new Dictionary<string, object?>
            {
                ["originalMessage"] = message,
                ["returnHome"] = normalized.Contains("return", StringComparison.Ordinal)
            };

            if (dimensions is not null)
            {
                arguments["width"] = dimensions.Value.Width;
                arguments["length"] = dimensions.Value.Length;
            }

            if (depth is not null)
            {
                arguments["depth"] = depth.Value;
            }
            else
            {
                arguments["missing"] = "depth";
                arguments["clarificationPrompt"] = "How deep should the pit be?";
            }

            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.ExcavateRectangularPit,
                arguments,
                CommandBudget: 256);
        }

        if ((normalized.Contains("tunnel", StringComparison.Ordinal) ||
             normalized.Contains("mineshaft", StringComparison.Ordinal)) &&
            (normalized.Contains("dig", StringComparison.Ordinal) ||
             normalized.Contains("mine", StringComparison.Ordinal) ||
             normalized.Contains("clear", StringComparison.Ordinal) ||
             normalized.Contains("make", StringComparison.Ordinal)))
        {
            if (normalized.Contains("branch", StringComparison.Ordinal))
            {
                var branchCount = ExtractBranchCount(message);
                var branchLength = ExtractBranchLength(message) ?? 6;
                var mainLength = ExtractMainLength(message) ?? Math.Max(9, branchCount * 3);
                return new TurtleBehavior(
                    $"tqb-{Guid.NewGuid():N}",
                    TurtleBehaviorIds.BranchMinePattern,
                    new Dictionary<string, object?>
                    {
                        ["originalMessage"] = message,
                        ["missionClass"] = "mining_route",
                        ["structureClass"] = "branch_mine_pattern",
                        ["mainLength"] = Math.Clamp(mainLength, 1, 64),
                        ["branchLength"] = Math.Clamp(branchLength, 1, 64),
                        ["branchCount"] = branchCount,
                        ["spacing"] = 3,
                        ["height"] = ExtractTunnelHeight(message) ?? 2,
                        ["sidePattern"] = "alternating",
                        ["returnHome"] = normalized.Contains("return", StringComparison.Ordinal) ||
                            normalized.Contains("back", StringComparison.Ordinal) ||
                            normalized.Contains("home", StringComparison.Ordinal)
                    },
                    CommandBudget: 64);
            }

            var tunnelLength = Math.Clamp(length ?? 6, 1, 64);
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.TunnelLine,
                new Dictionary<string, object?>
                {
                    ["originalMessage"] = message,
                    ["missionClass"] = "mining_route",
                    ["structureClass"] = "line_tunnel",
                    ["length"] = tunnelLength,
                    ["height"] = ExtractTunnelHeight(message) ?? 2,
                    ["returnHome"] = normalized.Contains("return", StringComparison.Ordinal) ||
                        normalized.Contains("back", StringComparison.Ordinal) ||
                        normalized.Contains("home", StringComparison.Ordinal)
                },
                CommandBudget: Math.Clamp(tunnelLength + 16, 24, 96));
        }

        if (normalized.Contains("dig", StringComparison.Ordinal) &&
            (normalized.Contains("straight", StringComparison.Ordinal) ||
             normalized.Contains("line", StringComparison.Ordinal) ||
             normalized.Contains("tunnel", StringComparison.Ordinal) ||
             normalized.Contains("forward", StringComparison.Ordinal)) &&
            normalized.Contains("return", StringComparison.Ordinal))
        {
            return new TurtleBehavior(
                $"tqb-{Guid.NewGuid():N}",
                TurtleBehaviorIds.DigLineReturn,
                new Dictionary<string, object?>
                {
                    ["length"] = Math.Clamp(length ?? 5, 1, 64),
                    ["returnHome"] = true
                },
                CommandBudget: 64);
        }

        return new TurtleBehavior(
            $"tqb-{Guid.NewGuid():N}",
            TurtleBehaviorIds.Unsupported,
            new Dictionary<string, object?>
            {
                ["originalMessage"] = message
            },
            CommandBudget: 4);
    }

    private static int? ExtractLength(string message)
    {
        var match = PositiveIntegerRegex().Match(message);
        return match.Success && int.TryParse(match.Value, out var value) ? value : null;
    }

    private static int ExtractResourceCount(string message)
    {
        var match = PositiveIntegerRegex().Match(message);
        return match.Success && int.TryParse(match.Value, out var value)
            ? Math.Clamp(value, 1, 8)
            : 1;
    }

    private static int ExtractBranchCount(string message)
    {
        var normalized = message.ToLowerInvariant();
        var branchMatch = Regex.Match(normalized, @"(?<count>\d+)\s+(?:side\s+)?branches?", RegexOptions.CultureInvariant);
        if (branchMatch.Success && int.TryParse(branchMatch.Groups["count"].Value, out var branchCount))
        {
            return Math.Clamp(branchCount, 1, 8);
        }

        return normalized.Contains("two ", StringComparison.Ordinal) ? 2 : 2;
    }

    private static int? ExtractBranchLength(string message)
    {
        var match = Regex.Match(message, @"(?<length>\d+)\s*(?:block\s*)?(?:side\s*)?branches?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["length"].Value, out var value)
            ? Math.Clamp(value, 1, 64)
            : null;
    }

    private static int? ExtractMainLength(string message)
    {
        var match = Regex.Match(message, @"main\s+tunnel\s+(?<length>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["length"].Value, out var value)
            ? Math.Clamp(value, 1, 64)
            : null;
    }

    private static (int Width, int Length)? ExtractRectangularDimensions(string message)
    {
        var match = RectangularDimensionsRegex().Match(message);
        if (!match.Success ||
            !int.TryParse(match.Groups["width"].Value, out var width) ||
            !int.TryParse(match.Groups["length"].Value, out var length))
        {
            return null;
        }

        return (Math.Clamp(width, 1, 16), Math.Clamp(length, 1, 16));
    }

    private static int? ExtractDepth(string message)
    {
        var match = DepthRegex().Match(message);
        return match.Success && int.TryParse(match.Groups["depth"].Value, out var depth)
            ? Math.Clamp(depth, 1, 16)
            : null;
    }

    private static int? ExtractTunnelHeight(string message)
    {
        var match = TunnelHeightRegex().Match(message);
        return match.Success && int.TryParse(match.Groups["height"].Value, out var height)
            ? Math.Clamp(height, 1, 2)
            : null;
    }

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex PositiveIntegerRegex();

    [GeneratedRegex(@"\b(?<width>\d+)\s*x\s*(?<length>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RectangularDimensionsRegex();

    [GeneratedRegex(@"\b(?<depth>\d+)\s*(?:blocks?\s*)?deep\b", RegexOptions.IgnoreCase)]
    private static partial Regex DepthRegex();

    [GeneratedRegex(@"\b(?<height>\d+)\s*(?:blocks?\s*)?(?:high|tall)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TunnelHeightRegex();
}
