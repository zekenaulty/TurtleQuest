package com.turtlequest;

import com.mojang.brigadier.arguments.StringArgumentType;
import com.mojang.brigadier.arguments.IntegerArgumentType;
import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.regex.Pattern;
import dan200.computercraft.api.turtle.TurtleCommandResult;
import dan200.computercraft.shared.turtle.blocks.TurtleBlockEntity;
import dan200.computercraft.shared.turtle.core.InteractDirection;
import dan200.computercraft.shared.turtle.core.MoveDirection;
import dan200.computercraft.shared.turtle.core.TurnDirection;
import dan200.computercraft.shared.turtle.core.TurtleMoveCommand;
import dan200.computercraft.shared.turtle.core.TurtlePlaceCommand;
import dan200.computercraft.shared.turtle.core.TurtleCraftCommand;
import dan200.computercraft.shared.turtle.core.TurtleDetectCommand;
import dan200.computercraft.shared.turtle.core.TurtleDropCommand;
import dan200.computercraft.shared.turtle.core.TurtleSuckCommand;
import dan200.computercraft.shared.turtle.core.TurtleToolCommand;
import dan200.computercraft.shared.turtle.core.TurtleTurnCommand;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.Commands;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.block.state.properties.BlockStateProperties;
import net.neoforged.fml.common.Mod;
import net.neoforged.neoforge.common.NeoForge;
import net.neoforged.neoforge.event.RegisterCommandsEvent;
import net.neoforged.neoforge.event.entity.player.PlayerEvent;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

@Mod(TurtleQuestMod.MOD_ID)
public final class TurtleQuestMod {
    public static final String MOD_ID = "turtlequest";
    private static final Logger LOGGER = LoggerFactory.getLogger(TurtleQuestMod.class);
    private static final int TURTLE_SEARCH_RADIUS = 16;
    private static final String KIT_GRANTED_TAG = "turtlequest.kit_granted";
    private static final HttpClient HTTP = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(10))
            .build();
    private static final ScheduledExecutorService EXECUTOR = Executors.newScheduledThreadPool(2, runnable -> {
        var thread = new Thread(runnable, "TurtleQuest executor");
        thread.setDaemon(true);
        return thread;
    });
    private static final ConcurrentHashMap<String, TurtleBinding> RUN_BINDINGS = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, String> TURTLE_NAMES = new ConcurrentHashMap<>();
    private static final Set<UUID> KIT_GRANTED = ConcurrentHashMap.newKeySet();
    private static final String BRIDGE_URL = System.getenv().getOrDefault(
            "TURTLEQUEST_BRIDGE_URL",
            "http://127.0.0.1:57421");
    private static final boolean AUTO_REPLAN_ON_BLOCKED = Boolean.parseBoolean(System.getenv().getOrDefault(
            "TURTLEQUEST_AUTO_REPLAN_ON_BLOCKED",
            "false"));
    private static final String RUNTIME_REPLAN_MODE = System.getenv().getOrDefault(
            "TURTLEQUEST_RUNTIME_REPLAN_MODE",
            "agentica");
    private static final int RUNTIME_REPLAN_ATTEMPTS = Integer.parseInt(System.getenv().getOrDefault(
            "TURTLEQUEST_RUNTIME_REPLAN_ATTEMPTS",
            "1"));
    private static final int BRIDGE_REQUEST_TIMEOUT_SECONDS = Integer.parseInt(System.getenv().getOrDefault(
            "TURTLEQUEST_BRIDGE_REQUEST_TIMEOUT_SECONDS",
            "15"));
    private static final int PLANNER_REQUEST_TIMEOUT_SECONDS = Integer.parseInt(System.getenv().getOrDefault(
            "TURTLEQUEST_PLANNER_REQUEST_TIMEOUT_SECONDS",
            System.getenv().getOrDefault("TURTLEQUEST_AGENTICA_PLANNER_TIMEOUT_SECONDS", "240")));
    private static final boolean CHAT_PROGRESS_ENABLED = Boolean.parseBoolean(System.getenv().getOrDefault(
            "TURTLEQUEST_CHAT_PROGRESS_ENABLED",
            "true"));
    private static final long CHAT_PROGRESS_MIN_INTERVAL_MS = Long.parseLong(System.getenv().getOrDefault(
            "TURTLEQUEST_CHAT_PROGRESS_MIN_INTERVAL_MS",
            "2500"));
    private static final List<String> DEFAULT_JUNK_DISCARD_PRIORITY = List.of(
            "minecraft:dirt",
            "minecraft:coarse_dirt",
            "minecraft:rooted_dirt",
            "minecraft:gravel",
            "minecraft:cobblestone",
            "minecraft:cobbled_deepslate",
            "minecraft:diorite",
            "minecraft:granite",
            "minecraft:andesite",
            "minecraft:tuff",
            "minecraft:calcite",
            "minecraft:sand",
            "minecraft:red_sand",
            "minecraft:sandstone",
            "minecraft:red_sandstone",
            "minecraft:netherrack",
            "minecraft:basalt",
            "minecraft:blackstone");

    public TurtleQuestMod() {
        NeoForge.EVENT_BUS.addListener(TurtleQuestMod::registerCommands);
        NeoForge.EVENT_BUS.addListener(TurtleQuestMod::onPlayerLoggedIn);
        LOGGER.info("TurtleQuest loaded. Bridge URL: {}", BRIDGE_URL);
    }

    private static void registerCommands(RegisterCommandsEvent event) {
        event.getDispatcher().register(Commands.literal("tq")
                .then(Commands.literal("ask")
                        .then(Commands.literal("nearest")
                                .then(Commands.argument("prompt", StringArgumentType.greedyString())
                                        .executes(context -> askNearest(
                                                context.getSource(),
                                                StringArgumentType.getString(context, "prompt"))))))
                .then(Commands.literal("status")
                        .then(Commands.argument("runId", StringArgumentType.word())
                                .executes(context -> bridgeGet(
                                        context.getSource(),
                                        "/runs/" + StringArgumentType.getString(context, "runId"),
                                        "TurtleQuest run status"))))
                .then(Commands.literal("simulate")
                        .then(Commands.argument("runId", StringArgumentType.word())
                                .executes(context -> bridgePost(
                                        context.getSource(),
                                        "/runs/" + StringArgumentType.getString(context, "runId") + "/simulate",
                                        "",
                                        "TurtleQuest simulated run"))))
                .then(Commands.literal("replan")
                        .then(Commands.argument("runId", StringArgumentType.word())
                                .executes(context -> bridgePost(
                                        context.getSource(),
                                        "/runs/" + StringArgumentType.getString(context, "runId") + "/replan",
                                        runtimeReplanJson(),
                                        "TurtleQuest runtime replan"))))
                .then(Commands.literal("snapshot")
                        .then(Commands.literal("nearest")
                                .then(Commands.argument("x", IntegerArgumentType.integer(1, 32))
                                        .then(Commands.argument("y", IntegerArgumentType.integer(1, 32))
                                                .then(Commands.argument("z", IntegerArgumentType.integer(1, 32))
                                                        .executes(context -> snapshotNearest(
                                                                context.getSource(),
                                                                IntegerArgumentType.getInteger(context, "x"),
                                                                IntegerArgumentType.getInteger(context, "y"),
                                                                IntegerArgumentType.getInteger(context, "z"))))))))
                .then(Commands.literal("diff")
                        .then(Commands.argument("beforeSnapshotId", StringArgumentType.word())
                                .then(Commands.argument("afterSnapshotId", StringArgumentType.word())
                                        .executes(context -> diffSnapshots(
                                                context.getSource(),
                                                StringArgumentType.getString(context, "beforeSnapshotId"),
                                                StringArgumentType.getString(context, "afterSnapshotId"))))))
                .then(Commands.literal("kit")
                        .executes(context -> grantKitCommand(context.getSource())))
                .then(Commands.literal("name")
                        .then(Commands.literal("nearest")
                                .then(Commands.argument("name", StringArgumentType.greedyString())
                                        .executes(context -> nameNearest(
                                                context.getSource(),
                                                StringArgumentType.getString(context, "name")))))));
    }

    private static void onPlayerLoggedIn(PlayerEvent.PlayerLoggedInEvent event) {
        if (!(event.getEntity() instanceof ServerPlayer player)) {
            return;
        }

        if (!player.getTags().contains(KIT_GRANTED_TAG) && KIT_GRANTED.add(player.getUUID())) {
            grantKit(player, false);
            player.addTag(KIT_GRANTED_TAG);
        }
    }

    private static int grantKitCommand(CommandSourceStack source) {
        ServerPlayer player;
        try {
            player = source.getPlayerOrException();
        } catch (Exception exception) {
            source.sendFailure(Component.literal("TurtleQuest kit must be granted to a player."));
            return 0;
        }

        grantKit(player, true);
        return 1;
    }

    private static int nameNearest(CommandSourceStack source, String name) {
        ServerPlayer player;
        try {
            player = source.getPlayerOrException();
        } catch (Exception exception) {
            source.sendFailure(Component.literal("TurtleQuest turtle naming must be done by a player for this slice."));
            return 0;
        }

        var binding = findNearestTurtle(player);
        if (binding.isEmpty()) {
            source.sendFailure(Component.literal("No CC:T turtle-like block found within " + TURTLE_SEARCH_RADIUS + " blocks."));
            return 0;
        }

        var cleanName = sanitizeTurtleName(name);
        if (cleanName.isBlank()) {
            source.sendFailure(Component.literal("TurtleQuest turtle name cannot be blank."));
            return 0;
        }

        var turtle = binding.get();
        TURTLE_NAMES.put(turtle.identityKey(), cleanName);
        turtle.displayName = cleanName;
        source.sendSuccess(
                () -> Component.literal("TurtleQuest named nearest turtle " + cleanName + " (" + turtle.turtleId() + ")."),
                false);
        return 1;
    }

    private static int snapshotNearest(CommandSourceStack source, int sizeX, int sizeY, int sizeZ) {
        ServerPlayer player;
        try {
            player = source.getPlayerOrException();
        } catch (Exception exception) {
            source.sendFailure(Component.literal("TurtleQuest snapshots must be captured by a player for this smoke slice."));
            return 0;
        }

        var binding = findNearestTurtle(player);
        if (binding.isEmpty()) {
            source.sendFailure(Component.literal("No CC:T turtle-like block found within " + TURTLE_SEARCH_RADIUS + " blocks."));
            return 0;
        }

        var turtle = binding.get();
        var snapshotId = "tqsnap-" + UUID.randomUUID().toString().replace("-", "");
        var body = snapshotJson(snapshotId, turtle, sizeX, sizeY, sizeZ);
        var request = HttpRequest.newBuilder()
                .uri(URI.create(BRIDGE_URL + "/snapshots"))
                .timeout(Duration.ofSeconds(5))
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body))
                .build();

        sendAsync(source, request, "TurtleQuest snapshot " + snapshotId);
        return 1;
    }

    private static int diffSnapshots(CommandSourceStack source, String beforeSnapshotId, String afterSnapshotId) {
        var body = "{"
                + "\"beforeSnapshotId\":\"" + json(beforeSnapshotId) + "\","
                + "\"afterSnapshotId\":\"" + json(afterSnapshotId) + "\""
                + "}";
        var request = HttpRequest.newBuilder()
                .uri(URI.create(BRIDGE_URL + "/snapshots/diff"))
                .timeout(Duration.ofSeconds(5))
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body))
                .build();

        sendAsync(source, request, "TurtleQuest snapshot diff");
        return 1;
    }

    private static void grantKit(ServerPlayer player, boolean manual) {
        var granted = new ArrayList<String>();
        giveRegistryItem(player, "computercraft:turtle_normal", 3).ifPresent(granted::add);
        giveRegistryItem(player, "computercraft:turtle_advanced", 2).ifPresent(granted::add);
        give(player, new ItemStack(Items.DIAMOND_PICKAXE, 2), "minecraft:diamond_pickaxe", granted);
        give(player, new ItemStack(Items.DIAMOND_SHOVEL, 1), "minecraft:diamond_shovel", granted);
        give(player, new ItemStack(Items.COBBLESTONE, 64), "minecraft:cobblestone", granted);
        give(player, new ItemStack(Items.TORCH, 32), "minecraft:torch", granted);

        player.sendSystemMessage(Component.literal(
                "TurtleQuest dev kit " + (manual ? "granted" : "granted on login") + ": " + String.join(", ", granted)));
    }

    private static Optional<String> giveRegistryItem(ServerPlayer player, String itemId, int count) {
        var item = BuiltInRegistries.ITEM.get(ResourceLocation.parse(itemId));
        if (item == Items.AIR) {
            LOGGER.warn("TurtleQuest dev kit item '{}' was not found.", itemId);
            return Optional.empty();
        }

        give(player, new ItemStack(item, count), itemId, null);
        return Optional.of(itemId + " x" + count);
    }

    private static void give(ServerPlayer player, ItemStack stack, String label, List<String> granted) {
        if (!player.getInventory().add(stack)) {
            player.drop(stack, false);
        }

        if (granted != null) {
            granted.add(label + " x" + stack.getCount());
        }
    }

    private static int askNearest(CommandSourceStack source, String prompt) {
        ServerPlayer player;
        try {
            player = source.getPlayerOrException();
        } catch (Exception exception) {
            source.sendFailure(Component.literal("TurtleQuest prompts must be sent by a player for this smoke slice."));
            return 0;
        }

        var binding = findNearestTurtle(player);
        if (binding.isEmpty()) {
            source.sendFailure(Component.literal("No CC:T turtle-like block found within " + TURTLE_SEARCH_RADIUS + " blocks."));
            return 0;
        }

        var turtle = binding.get();
        var body = requestJson(turtle, player, prompt);
        var request = HttpRequest.newBuilder()
                .uri(URI.create(BRIDGE_URL + "/turtles/" + turtle.turtleId() + "/messages"))
                .timeout(Duration.ofSeconds(PLANNER_REQUEST_TIMEOUT_SECONDS))
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body))
                .build();

        source.sendSuccess(
                () -> Component.literal("TurtleQuest found " + turtle.turtleId() + "; posting prompt to bridge..."),
                false);

        HTTP.sendAsync(request, HttpResponse.BodyHandlers.ofString())
                .whenComplete((response, error) -> {
                    if (error != null) {
                        LOGGER.warn("Failed to post TurtleQuest prompt.", error);
                        failure(source, "TurtleQuest bridge request failed: " + rootMessage(error));
                        return;
                    }

                    if (response.statusCode() < 200 || response.statusCode() >= 300) {
                        LOGGER.warn("TurtleQuest bridge returned HTTP {}: {}", response.statusCode(), response.body());
                        failure(source, "TurtleQuest bridge returned HTTP " + response.statusCode());
                        return;
                    }

                    var runId = JsonFields.string(response.body(), "runId").orElse("");
                    if (!runId.isBlank()) {
                        RUN_BINDINGS.put(runId, turtle);
                        turtle.ownerPlayerId = player.getUUID();
                        startExecutor(runId, turtle);
                    }

                    success(source, "TurtleQuest run accepted: " + runId + ". Progress updates will stay scoped to you.");
                });

        return 1;
    }

    private static Optional<TurtleBinding> findNearestTurtle(ServerPlayer player) {
        ServerLevel level = player.serverLevel();
        BlockPos center = player.blockPosition();
        BlockPos min = center.offset(-TURTLE_SEARCH_RADIUS, -TURTLE_SEARCH_RADIUS, -TURTLE_SEARCH_RADIUS);
        BlockPos max = center.offset(TURTLE_SEARCH_RADIUS, TURTLE_SEARCH_RADIUS, TURTLE_SEARCH_RADIUS);

        return BlockPos.betweenClosedStream(min, max)
                .map(BlockPos::immutable)
                .map(pos -> new TurtleCandidate(pos, level.getBlockState(pos)))
                .filter(candidate -> isComputerCraftTurtle(candidate.state()))
                .min(Comparator.comparingDouble(candidate -> candidate.pos().distSqr(center)))
                .map(candidate -> {
                    var facing = facingOf(candidate.state()).orElse(player.getDirection());
                    var identityKey = turtleIdentityKey(level, candidate.pos());
                    var displayName = TURTLE_NAMES.getOrDefault(identityKey, "");
                    return new TurtleBinding(
                            "turtle@" + candidate.pos().getX() + "," + candidate.pos().getY() + "," + candidate.pos().getZ(),
                            level,
                            candidate.pos(),
                            facing,
                            level.dimension().location().toString(),
                            player.getUUID(),
                            identityKey,
                            displayName);
                });
    }

    private static boolean isComputerCraftTurtle(BlockState state) {
        ResourceLocation id = BuiltInRegistries.BLOCK.getKey(state.getBlock());
        return "computercraft".equals(id.getNamespace()) && id.getPath().contains("turtle");
    }

    private static Optional<Direction> facingOf(BlockState state) {
        if (state.hasProperty(BlockStateProperties.FACING)) {
            return Optional.of(state.getValue(BlockStateProperties.FACING));
        }

        if (state.hasProperty(BlockStateProperties.HORIZONTAL_FACING)) {
            return Optional.of(state.getValue(BlockStateProperties.HORIZONTAL_FACING));
        }

        return Optional.empty();
    }

    private static void startExecutor(String runId, TurtleBinding binding) {
        EXECUTOR.execute(() -> {
            try {
                progress(binding, "Thinking...", true);
                for (int i = 0; i < 512; i++) {
                    var commandResponse = bridgeRequest("GET", "/runs/" + runId + "/next-command", "");
                    if (commandResponse.statusCode() == 202) {
                        LOGGER.info("Run {} is waiting for a compiled TurtleQuest plan.", runId);
                        progress(binding, "Planning turtle workflow...", false);
                        Thread.sleep(1000);
                        continue;
                    }

                    if (commandResponse.statusCode() == 204) {
                        progress(binding, "No more turtle commands are queued.", true);
                        return;
                    }

                    if (commandResponse.statusCode() < 200 || commandResponse.statusCode() >= 300) {
                        LOGGER.warn("Executor poll for {} returned HTTP {}: {}", runId, commandResponse.statusCode(), commandResponse.body());
                        return;
                    }

                    var command = TurtleCommand.fromJson(commandResponse.body());
                    if (command.isEmpty()) {
                        LOGGER.warn("Executor could not parse command for {}: {}", runId, commandResponse.body());
                        return;
                    }

                    progressForCommand(binding, command.get());
                    var receipt = executeOnServerThread(binding, command.get()).join();
                    var receiptResponse = bridgeRequest("POST", "/runs/" + runId + "/receipts", receipt.toJson());
                    if (receiptResponse.statusCode() < 200 || receiptResponse.statusCode() >= 300) {
                        LOGGER.warn("Receipt post for {} returned HTTP {}: {}", runId, receiptResponse.statusCode(), receiptResponse.body());
                        return;
                    }

                    if (!receipt.success()) {
                        progress(binding, "Blocked during " + receipt.action() + ": " + summarizeReceiptMessage(receipt.message()), true);
                        if (!AUTO_REPLAN_ON_BLOCKED) {
                            LOGGER.info("Run {} blocked after failed {} receipt. Auto replan is disabled.", runId, receipt.action());
                            return;
                        }

                        progress(binding, "Requesting runtime replan from receipts...", true);
                            var replanResponse = bridgeRequest("POST", "/runs/" + runId + "/replan", runtimeReplanJson(), PLANNER_REQUEST_TIMEOUT_SECONDS);
                        if (replanResponse.statusCode() < 200 || replanResponse.statusCode() >= 300) {
                            LOGGER.warn("Runtime replan for {} returned HTTP {}: {}", runId, replanResponse.statusCode(), replanResponse.body());
                            progress(binding, "Runtime replan failed with HTTP " + replanResponse.statusCode() + ".", true);
                            return;
                        }

                        LOGGER.info("Runtime replan accepted for {} after failed {} receipt.", runId, receipt.action());
                        progress(binding, "Runtime replan accepted; continuing with recovery commands.", true);
                    }

                    if ("completeObjective".equals(receipt.action()) && receipt.success()) {
                        progress(binding, "Objective complete: " + summarizeReceiptMessage(receipt.message()), true);
                    }

                    Thread.sleep(200);
                }
            } catch (InterruptedException exception) {
                Thread.currentThread().interrupt();
            } catch (Exception exception) {
                LOGGER.warn("TurtleQuest executor failed for run {}.", runId, exception);
            }
        });
    }

    private static CompletableFuture<TurtleReceipt> executeOnServerThread(TurtleBinding binding, TurtleCommand command) {
        var result = new CompletableFuture<TurtleReceipt>();
        binding.level().getServer().execute(() -> {
            try {
                result.complete(executeCommand(binding, command));
            } catch (Exception exception) {
                result.completeExceptionally(exception);
            }
        });
        return result.orTimeout(Math.max(10, BRIDGE_REQUEST_TIMEOUT_SECONDS), TimeUnit.SECONDS);
    }

    private static TurtleReceipt executeCommand(TurtleBinding binding, TurtleCommand command) {
        var blockEntity = binding.level().getBlockEntity(binding.pos());
        if (!(blockEntity instanceof TurtleBlockEntity turtle)) {
            return receipt(command, binding, false, null, "Bound turtle is no longer present.", List.of("turtle_missing"));
        }

        return switch (command.action()) {
            case "startBehavior" -> startBehavior(command, binding);
            case "inspect" -> inspect(command, binding);
            case "inspectUp" -> inspectAt(command, binding, binding.pos().above(), "Block above inspected.");
            case "inspectDown" -> inspectAt(command, binding, binding.pos().below(), "Block below inspected.");
            case "emitStatus" -> receipt(command, binding, true, null, "Status emitted by TurtleQuest executor.", List.of());
            case "dig" -> runTurtleCommand(command, binding, TurtleToolCommand.dig(InteractDirection.FORWARD, null), "Dug block ahead.");
            case "digUp" -> runTurtleCommand(command, binding, TurtleToolCommand.dig(InteractDirection.UP, null), "Dug block above.");
            case "digDown" -> runTurtleCommand(command, binding, TurtleToolCommand.dig(InteractDirection.DOWN, null), "Dug block below.");
            case "digRememberedTarget" -> digRememberedTarget(command, binding, turtle);
            case "fellRememberedTree" -> fellRememberedTree(command, binding, turtle);
            case "turnLeft" -> runTurn(command, binding, turtle, TurnDirection.LEFT, "Turned left.");
            case "turnRight" -> runTurnRight(command, binding, turtle);
            case "moveForward" -> runMoveForward(command, binding, turtle);
            case "moveBackward" -> runMoveBackward(command, binding, turtle);
            case "moveUp" -> runMove(command, binding, turtle, MoveDirection.UP, "Moved up.");
            case "moveDown" -> runMove(command, binding, turtle, MoveDirection.DOWN, "Moved down.");
            case "face" -> face(command, binding, turtle);
            case "moveTowardRelative" -> moveTowardRelative(command, binding, turtle);
            case "recoverToGround" -> recoverToGround(command, binding, turtle);
            case "markWaypoint" -> markWaypoint(command, binding);
            case "returnToPosition" -> returnToPosition(command, binding, turtle);
            case "tunnelLine" -> tunnelLine(command, binding, turtle);
            case "branchTunnel" -> branchTunnel(command, binding, turtle);
            case "branchMinePattern" -> branchMinePattern(command, binding, turtle);
            case "place" -> runPlace(command, binding, InteractDirection.FORWARD, "Placed block ahead.");
            case "placeUp" -> runPlace(command, binding, InteractDirection.UP, "Placed block above.");
            case "placeDown" -> runPlace(command, binding, InteractDirection.DOWN, "Placed block below.");
            case "placeStorage" -> placeStorage(command, binding, turtle);
            case "selectSlot" -> selectSlot(command, binding, turtle);
            case "getInventory" -> getInventory(command, binding, turtle);
            case "discardJunk" -> discardJunk(command, binding, turtle);
            case "drop" -> runDrop(command, binding, turtle, InteractDirection.FORWARD, "Dropped items ahead.");
            case "dropUp" -> runDrop(command, binding, turtle, InteractDirection.UP, "Dropped items above.");
            case "dropDown" -> runDrop(command, binding, turtle, InteractDirection.DOWN, "Dropped items below.");
            case "suck" -> runSuck(command, binding, turtle, InteractDirection.FORWARD, "Collected items ahead.");
            case "suckUp" -> runSuck(command, binding, turtle, InteractDirection.UP, "Collected items above.");
            case "suckDown" -> runSuck(command, binding, turtle, InteractDirection.DOWN, "Collected items below.");
            case "craft" -> runCraft(command, binding, turtle);
            case "detect" -> runDetect(command, binding, InteractDirection.FORWARD, "Detected block ahead.");
            case "detectUp" -> runDetect(command, binding, InteractDirection.UP, "Detected block above.");
            case "detectDown" -> runDetect(command, binding, InteractDirection.DOWN, "Detected block below.");
            case "depositInventory" -> depositInventory(command, binding, turtle);
            case "scanNearby" -> scanNearby(command, binding);
            case "returnHome" -> runReturnHome(command, binding, turtle);
            case "completeObjective" -> completeObjective(command, binding);
            default -> receipt(command, binding, false, null, "Unsupported TurtleQuest command: " + command.action(), List.of("unsupported_command"));
        };
    }

    private static TurtleReceipt startBehavior(TurtleCommand command, TurtleBinding binding) {
        JsonFields.string(command.rawJson(), "behaviorId").ifPresent(behaviorId -> binding.currentBehaviorId = behaviorId);
        return receipt(command, binding, true, null, "Behavior started on bound turtle.", List.of());
    }

    private static TurtleReceipt runMoveForward(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var before = turtle.getAccess().getPosition();
        ensureSolarFuel(turtle);
        var result = new TurtleMoveCommand(MoveDirection.FORWARD).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        if (result.isSuccess()) {
            var after = turtle.getAccess().getPosition();
            if (manhattan(after, binding.startPos()) > manhattan(before, binding.startPos())) {
                binding.forwardMoves++;
            } else {
                binding.returnMoves++;
            }
            binding.path.add(after);
        }

        return receiptFromResult(command, binding, result, "Moved forward.");
    }

    private static TurtleReceipt runTurnRight(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        return runTurn(command, binding, turtle, TurnDirection.RIGHT, "Turned right.");
    }

    private static TurtleReceipt runTurn(
            TurtleCommand command,
            TurtleBinding binding,
            TurtleBlockEntity turtle,
            TurnDirection direction,
            String successMessage) {
        var result = new TurtleTurnCommand(direction).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        return receiptFromResult(command, binding, result, successMessage);
    }

    private static TurtleReceipt runMoveBackward(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        ensureSolarFuel(turtle);
        var result = new TurtleMoveCommand(MoveDirection.BACK).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        if (result.isSuccess()) {
            binding.backwardMoves++;
        }

        return receiptFromResult(command, binding, result, "Moved backward toward start.");
    }

    private static TurtleReceipt runMove(
            TurtleCommand command,
            TurtleBinding binding,
            TurtleBlockEntity turtle,
            MoveDirection direction,
            String successMessage) {
        ensureSolarFuel(turtle);
        var result = new TurtleMoveCommand(direction).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        return receiptFromResult(command, binding, result, successMessage);
    }

    private static TurtleReceipt face(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var directionName = JsonFields.string(command.rawJson(), "direction").orElse("");
        var target = parseHorizontalDirection(directionName);
        if (target.isEmpty()) {
            return receipt(command, binding, false, blockAhead(binding), "face requires direction north, east, south, or west.", List.of("invalid_direction"));
        }

        var result = turnTo(turtle, binding, target.get());
        if (!result.isSuccess()) {
            return receiptFromResult(command, binding, result, "Failed to face " + target.get().getName() + ".");
        }

        return receipt(command, binding, true, blockAhead(binding), "Facing " + target.get().getName() + ".", List.of());
    }

    private static TurtleReceipt moveTowardRelative(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var dx = JsonFields.integer(command.rawJson(), "dx").orElse(binding.lastScanRelativeX);
        var dz = JsonFields.integer(command.rawJson(), "dz").orElse(binding.lastScanRelativeZ);
        if (dx == null || dz == null) {
            return receipt(command, binding, false, blockAhead(binding), "moveTowardRelative has no dx/dz and no remembered scan target.", List.of("no_scan_target"));
        }

        var budget = Math.max(1, Math.min(32, JsonFields.integer(command.rawJson(), "budget")
                .or(() -> JsonFields.integer(command.rawJson(), "pathBudget"))
                .orElse(12)));
        var stopAdjacent = JsonFields.bool(command.rawJson(), "stopAdjacent").orElse(true);
        var remainingX = dx;
        var remainingZ = dz;
        var steps = 0;
        TurtleCommandResult lastResult = TurtleCommandResult.success();

        while (steps < budget && horizontalDistance(remainingX, remainingZ) > (stopAdjacent ? 1 : 0)) {
            var targetDirection = nextDirectionToward(remainingX, remainingZ);
            if (targetDirection.isEmpty()) {
                break;
            }

            lastResult = turnTo(turtle, binding, targetDirection.get());
            if (!lastResult.isSuccess()) {
                return receiptFromResult(command, binding, lastResult, "moveTowardRelative failed while turning.");
            }

            ensureSolarFuel(turtle);
            lastResult = new TurtleMoveCommand(MoveDirection.FORWARD).execute(turtle.getAccess());
            binding.refreshFrom(turtle);
            if (!lastResult.isSuccess()) {
                return receiptFromResult(command, binding, lastResult, "moveTowardRelative blocked after " + steps + " step(s).");
            }

            if (targetDirection.get() == Direction.EAST) {
                remainingX--;
            } else if (targetDirection.get() == Direction.WEST) {
                remainingX++;
            } else if (targetDirection.get() == Direction.SOUTH) {
                remainingZ--;
            } else if (targetDirection.get() == Direction.NORTH) {
                remainingZ++;
            }

            binding.path.add(binding.pos());
            binding.approachMoves++;
            steps++;
        }

        var arrived = horizontalDistance(remainingX, remainingZ) <= (stopAdjacent ? 1 : 0);
        var hazards = arrived ? List.<String>of() : List.of("path_budget_exhausted");
        var message = "moveTowardRelative steps=" + steps
                + "; remainingDx=" + remainingX
                + "; remainingDz=" + remainingZ
                + "; stopAdjacent=" + stopAdjacent
                + "; arrived=" + arrived;
        binding.lastScanRelativeX = remainingX;
        binding.lastScanRelativeZ = remainingZ;
        return receipt(command, binding, arrived, blockAhead(binding), message, hazards);
    }

    private static TurtleReceipt digRememberedTarget(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        if (binding.lastScanRelativeX == null || binding.lastScanRelativeY == null || binding.lastScanRelativeZ == null) {
            return receipt(command, binding, false, blockAhead(binding), "digRememberedTarget has no remembered scan target.", List.of("no_scan_target"));
        }

        var dx = binding.lastScanRelativeX;
        var dy = binding.lastScanRelativeY;
        var dz = binding.lastScanRelativeZ;
        if (dy != 0 || horizontalDistance(dx, dz) != 1) {
            return receipt(
                    command,
                    binding,
                    false,
                    blockAhead(binding),
                    "Remembered target is not horizontally adjacent. relative=" + dx + "," + dy + "," + dz,
                    List.of("target_not_adjacent"));
        }

        var direction = nextDirectionToward(dx, dz);
        if (direction.isEmpty()) {
            return receipt(command, binding, false, blockAhead(binding), "No direction toward remembered target.", List.of("target_not_adjacent"));
        }

        var turnResult = turnTo(turtle, binding, direction.get());
        if (!turnResult.isSuccess()) {
            return receiptFromResult(command, binding, turnResult, "Could not face remembered target.");
        }

        var blockId = blockAhead(binding).toLowerCase();
        if (!isLogLike(blockId)) {
            binding.ignoredScanTargets.add(positionKey(binding.pos().relative(direction.get())));
            return receipt(command, binding, false, blockAhead(binding), "Remembered target is no longer log-like: " + blockId, List.of("target_changed"));
        }

        var targetPos = binding.pos().relative(direction.get());
        var beforeInventory = inventorySummary(turtle);
        var result = TurtleToolCommand.dig(InteractDirection.FORWARD, null).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        var inventoryDelta = inventoryDelta(beforeInventory, inventorySummary(turtle));
        if (result.isSuccess()) {
            binding.rememberedTargetDigCount++;
            binding.lastHarvestBasePos = targetPos.immutable();
            binding.lastScanRelativeX = null;
            binding.lastScanRelativeY = null;
            binding.lastScanRelativeZ = null;
            binding.lastScanBlockId = "";
        }

        return receiptFromResult(command, binding, result, "Cut remembered log target: " + blockId + ".", inventoryDelta);
    }

    private static TurtleReceipt fellRememberedTree(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        if (binding.lastHarvestBasePos == null) {
            return receipt(command, binding, false, blockAhead(binding), "fellRememberedTree has no harvested base log position.", List.of("no_tree_base"));
        }

        var maxHeight = Math.max(1, Math.min(32, JsonFields.integer(command.rawJson(), "maxHeight").orElse(12)));
        var base = binding.lastHarvestBasePos;
        if (!binding.pos().equals(base)) {
            if (manhattan(binding.pos(), base) != 1) {
                return receipt(command, binding, false, blockAhead(binding), "Turtle is not adjacent to harvested tree base.", List.of("tree_base_not_reachable"));
            }

            var direction = directionBetween(binding.pos(), base);
            if (direction.isEmpty()) {
                return receipt(command, binding, false, blockAhead(binding), "Could not determine direction to tree base.", List.of("tree_base_not_reachable"));
            }

            var turnResult = turnTo(turtle, binding, direction.get());
            if (!turnResult.isSuccess()) {
                return receiptFromResult(command, binding, turnResult, "Could not face harvested tree base.");
            }

            ensureSolarFuel(turtle);
            var enterResult = new TurtleMoveCommand(MoveDirection.FORWARD).execute(turtle.getAccess());
            binding.refreshFrom(turtle);
            if (!enterResult.isSuccess()) {
                return receiptFromResult(command, binding, enterResult, "Could not move into harvested tree base.");
            }

            binding.path.add(binding.pos());
            binding.approachMoves++;
        }

        var beforeInventory = inventorySummary(turtle);
        var cutCount = 0;
        var climbCount = 0;
        TurtleCommandResult lastResult = TurtleCommandResult.success();
        while (cutCount < maxHeight && isLogLike(blockAt(binding, binding.pos().above()).toLowerCase())) {
            lastResult = TurtleToolCommand.dig(InteractDirection.UP, null).execute(turtle.getAccess());
            binding.refreshFrom(turtle);
            if (!lastResult.isSuccess()) {
                return receiptFromResult(command, binding, lastResult, "Tree felling blocked while cutting upward.", inventoryDelta(beforeInventory, inventorySummary(turtle)));
            }

            cutCount++;
            binding.verticalLogDigCount++;

            var nextLogAboveClearedSpace = isLogLike(blockAt(binding, binding.pos().above(2)).toLowerCase());
            if (!nextLogAboveClearedSpace) {
                break;
            }

            ensureSolarFuel(turtle);
            lastResult = new TurtleMoveCommand(MoveDirection.UP).execute(turtle.getAccess());
            binding.refreshFrom(turtle);
            if (!lastResult.isSuccess()) {
                return receiptFromResult(command, binding, lastResult, "Tree felling blocked while climbing trunk.", inventoryDelta(beforeInventory, inventorySummary(turtle)));
            }

            binding.path.add(binding.pos());
            climbCount++;
        }

        var inventoryDelta = inventoryDelta(beforeInventory, inventorySummary(turtle));
        if (cutCount == 0) {
            binding.ignoredScanTargets.add(positionKey(base));
            return receipt(command, binding, false, blockAhead(binding), "No vertical log blocks were found above harvested base.", List.of("no_vertical_logs"), inventoryDelta);
        }

        var descendResult = descendToTreeBase(turtle, binding, base, climbCount);
        if (!descendResult.isSuccess()) {
            return receiptFromResult(
                    command,
                    binding,
                    descendResult,
                    "Tree felling could not descend back to trunk base.",
                    inventoryDelta);
        }

        binding.harvestedTreeColumns.add(columnKey(base));
        binding.lastHarvestBasePos = null;
        var message = "Felled remembered tree trunk upward; verticalLogsCut=" + cutCount + "; climbs=" + climbCount + "; descendedToBase=true.";
        return receipt(command, binding, true, blockAhead(binding), message, List.of(), inventoryDelta);
    }

    private static TurtleCommandResult descendToTreeBase(TurtleBlockEntity turtle, TurtleBinding binding, BlockPos base, int climbCount) {
        TurtleCommandResult lastResult = TurtleCommandResult.success();
        for (var i = 0; i < climbCount && binding.pos().getY() > base.getY(); i++) {
            trimCurrentBreadcrumb(binding, binding.pos());
            ensureSolarFuel(turtle);
            lastResult = new TurtleMoveCommand(MoveDirection.DOWN).execute(turtle.getAccess());
            binding.refreshFrom(turtle);
            if (!lastResult.isSuccess()) {
                return lastResult;
            }
        }

        return binding.pos().getY() == base.getY()
                ? TurtleCommandResult.success()
                : TurtleCommandResult.failure("Did not return to tree base height.");
    }

    private static TurtleReceipt runPlace(
            TurtleCommand command,
            TurtleBinding binding,
            InteractDirection direction,
            String successMessage) {
        var blockEntity = binding.level().getBlockEntity(binding.pos());
        if (!(blockEntity instanceof TurtleBlockEntity turtle)) {
            return receipt(command, binding, false, null, "Bound turtle is no longer present.", List.of("turtle_missing"));
        }

        var result = new TurtlePlaceCommand(direction, new Object[0]).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        if (result.isSuccess()) {
            binding.placeCount++;
            if (direction == InteractDirection.DOWN) {
                binding.placeDownCount++;
            }
        }

        return receiptFromResult(command, binding, result, successMessage);
    }

    private static TurtleReceipt runDrop(
            TurtleCommand command,
            TurtleBinding binding,
            TurtleBlockEntity turtle,
            InteractDirection direction,
            String successMessage) {
        var count = Math.max(1, Math.min(64, JsonFields.integer(command.rawJson(), "count").orElse(64)));
        var before = inventorySummary(turtle);
        var result = new TurtleDropCommand(direction, count).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        var after = inventorySummary(turtle);
        return receiptFromResult(command, binding, result, successMessage, inventoryDelta(before, after));
    }

    private static TurtleReceipt runSuck(
            TurtleCommand command,
            TurtleBinding binding,
            TurtleBlockEntity turtle,
            InteractDirection direction,
            String successMessage) {
        var count = Math.max(1, Math.min(64, JsonFields.integer(command.rawJson(), "count").orElse(64)));
        var before = inventorySummary(turtle);
        var result = new TurtleSuckCommand(direction, count).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        var after = inventorySummary(turtle);
        return receiptFromResult(command, binding, result, successMessage, inventoryDelta(before, after));
    }

    private static TurtleReceipt runCraft(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var count = Math.max(1, Math.min(64, JsonFields.integer(command.rawJson(), "count").orElse(1)));
        var before = inventorySummary(turtle);
        var result = new TurtleCraftCommand(count).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        var after = inventorySummary(turtle);
        return receiptFromResult(command, binding, result, "Crafted item(s).", inventoryDelta(before, after));
    }

    private static TurtleReceipt runDetect(
            TurtleCommand command,
            TurtleBinding binding,
            InteractDirection direction,
            String successMessage) {
        var block = switch (direction) {
            case UP -> blockAt(binding, binding.pos().above());
            case DOWN -> blockAt(binding, binding.pos().below());
            default -> blockAhead(binding);
        };
        var blockEntity = binding.level().getBlockEntity(binding.pos());
        if (!(blockEntity instanceof TurtleBlockEntity turtle)) {
            return receipt(command, binding, false, block, "Bound turtle is no longer present.", List.of("turtle_missing"));
        }

        var result = new TurtleDetectCommand(direction).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        return receiptFromResult(command, binding, result, successMessage + " block=" + block + ".");
    }

    private static TurtleReceipt placeStorage(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var storageKind = JsonFields.string(command.rawJson(), "storageKind").orElse("barrel");
        var waypointName = JsonFields.string(command.rawJson(), "waypointName").orElse("home_storage");
        var placement = JsonFields.string(command.rawJson(), "placement").orElse("front");
        var direction = interactDirection(placement);
        var inventory = turtle.getAccess().getInventory();
        var selectedSlotBefore = turtle.getAccess().getSelectedSlot();
        var storageSlot = findStorageSlot(inventory, storageKind);
        if (storageSlot < 0) {
            return receipt(command, binding, false, blockAhead(binding), "No " + storageKind + " storage block found in turtle inventory.", List.of("storage_item_missing"));
        }

        var before = inventorySummary(turtle);
        turtle.getAccess().setSelectedSlot(storageSlot);
        var target = switch (direction) {
            case UP -> binding.pos().above();
            case DOWN -> binding.pos().below();
            default -> binding.pos().relative(binding.facing());
        };
        var result = new TurtlePlaceCommand(direction, new Object[0]).execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        turtle.getAccess().setSelectedSlot(selectedSlotBefore);
        var after = inventorySummary(turtle);
        if (result.isSuccess()) {
            binding.placeCount++;
        }

        var storageBlock = blockAt(binding, target);
        var message = result.isSuccess()
                ? "Placed storage waypointId="
                        + waypointName
                        + "; name="
                        + waypointName
                        + "; storageKind="
                        + storageKind
                        + "; storagePosition="
                        + target.getX()
                        + ","
                        + target.getY()
                        + ","
                        + target.getZ()
                        + "; block="
                        + storageBlock
                        + "."
                : "Storage placement failed for " + storageKind + ".";
        return receiptFromResult(command, binding, result, message, inventoryDelta(before, after));
    }

    private static TurtleReceipt depositInventory(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var direction = interactDirection(JsonFields.string(command.rawJson(), "direction").orElse("front"));
        var keepSelected = JsonFields.bool(command.rawJson(), "keepSelected").orElse(true);
        var before = inventorySummary(turtle);
        var inventory = turtle.getAccess().getInventory();
        var selectedSlot = turtle.getAccess().getSelectedSlot();
        var depositedSlots = 0;
        TurtleCommandResult lastResult = TurtleCommandResult.success();

        for (var slot = 0; slot < inventory.getContainerSize(); slot++) {
            if (keepSelected && slot == selectedSlot) {
                continue;
            }

            var stack = inventory.getItem(slot);
            if (stack.isEmpty()) {
                continue;
            }

            var itemId = BuiltInRegistries.ITEM.getKey(stack.getItem()).toString();
            if (isProtectedDepositItem(itemId)) {
                continue;
            }

            turtle.getAccess().setSelectedSlot(slot);
            lastResult = new TurtleDropCommand(direction, stack.getCount()).execute(turtle.getAccess());
            binding.refreshFrom(turtle);
            if (!lastResult.isSuccess()) {
                turtle.getAccess().setSelectedSlot(selectedSlot);
                var afterFailure = inventorySummary(turtle);
                return receiptFromResult(command, binding, lastResult, "Deposit failed after " + depositedSlots + " slot(s).", inventoryDelta(before, afterFailure));
            }

            depositedSlots++;
        }

        turtle.getAccess().setSelectedSlot(selectedSlot);
        var after = inventorySummary(turtle);
        var message = "Deposited inventory slots=" + depositedSlots + "; direction=" + direction.name().toLowerCase() + ".";
        return receipt(command, binding, true, blockAhead(binding), message, List.of(), inventoryDelta(before, after));
    }

    private static TurtleReceipt selectSlot(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var slot = JsonFields.integer(command.rawJson(), "slot").orElse(1);
        if (slot < 1 || slot > 16) {
            return receipt(command, binding, false, blockAhead(binding), "Slot must be between 1 and 16.", List.of("invalid_slot"));
        }

        turtle.getAccess().setSelectedSlot(slot - 1);
        return receipt(command, binding, true, blockAhead(binding), "Selected turtle slot " + slot + ".", List.of());
    }

    private static TurtleReceipt getInventory(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var inventory = turtle.getAccess().getInventory();
        var occupied = 0;
        var selectedSlot = turtle.getAccess().getSelectedSlot() + 1;
        var items = new ArrayList<String>();
        for (var slot = 0; slot < inventory.getContainerSize(); slot++) {
            var stack = inventory.getItem(slot);
            if (stack.isEmpty()) {
                continue;
            }

            occupied++;
            var itemId = BuiltInRegistries.ITEM.getKey(stack.getItem()).toString();
            items.add((slot + 1) + ":" + itemId + "x" + stack.getCount());
        }

        return receipt(
                command,
                binding,
                true,
                blockAhead(binding),
                "Inventory selectedSlot=" + selectedSlot + "; occupiedSlots=" + occupied + "; items=" + String.join(";", items),
                List.of());
    }

    private static TurtleReceipt discardJunk(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var maxCount = Math.max(1, Math.min(64, JsonFields.integer(command.rawJson(), "maxCount").orElse(64)));
        var inventory = turtle.getAccess().getInventory();

        for (var junkId : DEFAULT_JUNK_DISCARD_PRIORITY) {
            for (var slot = 0; slot < inventory.getContainerSize(); slot++) {
                var stack = inventory.getItem(slot);
                if (stack.isEmpty()) {
                    continue;
                }

                var itemId = BuiltInRegistries.ITEM.getKey(stack.getItem()).toString();
                if (!junkId.equals(itemId)) {
                    continue;
                }

                var removed = Math.min(maxCount, stack.getCount());
                var replacement = stack.copy();
                replacement.shrink(removed);
                inventory.setItem(slot, replacement.isEmpty() ? ItemStack.EMPTY : replacement);
                inventory.setChanged();

                var delta = new LinkedHashMap<String, Integer>();
                delta.put(itemId, -removed);
                var message = "Magic trash can discarded junk from slot "
                        + (slot + 1)
                        + ": "
                        + itemId
                        + "x"
                        + removed
                        + ".";
                return receipt(command, binding, true, blockAhead(binding), message, List.of(), delta);
            }
        }

        return receipt(
                command,
                binding,
                true,
                blockAhead(binding),
                "Magic trash can found no default junk item to discard.",
                List.of("no_junk_to_discard"));
    }

    private static TurtleReceipt markWaypoint(TurtleCommand command, TurtleBinding binding) {
        var name = JsonFields.string(command.rawJson(), "name").orElse("waypoint");
        var waypointId = name + "-" + binding.pos().getX() + "-" + binding.pos().getY() + "-" + binding.pos().getZ();
        var message = "Marked waypointId=" + waypointId
                + "; name=" + name
                + "; position=" + binding.pos().toShortString()
                + "; facing=" + binding.facing().getSerializedName()
                + ".";
        return receipt(command, binding, true, blockAhead(binding), message, List.of());
    }

    private static TurtleReceipt returnToPosition(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var x = JsonFields.integer(command.rawJson(), "x");
        var y = JsonFields.integer(command.rawJson(), "y");
        var z = JsonFields.integer(command.rawJson(), "z");
        if (x.isEmpty() || y.isEmpty() || z.isEmpty()) {
            return receipt(command, binding, false, blockAhead(binding), "returnToPosition requires x, y, and z arguments.", List.of("target_position_missing"));
        }

        var budget = Math.max(1, Math.min(512, JsonFields.integer(command.rawJson(), "budget").orElse(128)));
        var target = new BlockPos(x.get(), y.get(), z.get());
        var start = binding.pos();
        var result = returnToPosition(turtle, binding, target, budget);
        var message = result.isSuccess()
                ? "Returned to position target="
                        + target.getX()
                        + ","
                        + target.getY()
                        + ","
                        + target.getZ()
                        + "; start="
                        + start.getX()
                        + ","
                        + start.getY()
                        + ","
                        + start.getZ()
                        + "."
                : "Return to position failed for target="
                        + target.getX()
                        + ","
                        + target.getY()
                        + ","
                        + target.getZ()
                        + ".";
        return receiptFromResult(command, binding, result, message);
    }

    private static TurtleReceipt tunnelLine(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var length = Math.max(1, Math.min(64, JsonFields.integer(command.rawJson(), "length").orElse(6)));
        var height = Math.max(1, Math.min(2, JsonFields.integer(command.rawJson(), "height").orElse(2)));
        var routeId = JsonFields.string(command.rawJson(), "routeId").orElse("route-" + UUID.randomUUID().toString().replace("-", ""));
        var beforeInventory = inventorySummary(turtle);
        var start = binding.pos();
        var stepsCompleted = 0;
        var blocksRemoved = 0;
        TurtleCommandResult lastResult = TurtleCommandResult.success();

        for (var step = 0; step < length; step++) {
            var aheadId = blockAhead(binding).toLowerCase();
            if (!isPassableForDescent(aheadId)) {
                lastResult = TurtleToolCommand.dig(InteractDirection.FORWARD, null).execute(turtle.getAccess());
                binding.refreshFrom(turtle);
                if (!lastResult.isSuccess()) {
                    return tunnelLineReceipt(
                            command,
                            binding,
                            false,
                            "Tunnel line blocked while digging forward at step " + (step + 1) + ".",
                            List.of("tunnel_forward_dig_blocked"),
                            beforeInventory,
                            turtle,
                            routeId,
                            start,
                            length,
                            height,
                            stepsCompleted,
                            blocksRemoved);
                }

                blocksRemoved++;
            }

            ensureSolarFuel(turtle);
            lastResult = new TurtleMoveCommand(MoveDirection.FORWARD).execute(turtle.getAccess());
            binding.refreshFrom(turtle);
            if (!lastResult.isSuccess()) {
                return tunnelLineReceipt(
                        command,
                        binding,
                        false,
                        "Tunnel line blocked while moving forward at step " + (step + 1) + ".",
                        List.of("tunnel_forward_move_blocked"),
                        beforeInventory,
                        turtle,
                        routeId,
                        start,
                        length,
                        height,
                        stepsCompleted,
                        blocksRemoved);
            }

            binding.forwardMoves++;
            binding.tunnelLineSteps++;
            binding.path.add(binding.pos());
            stepsCompleted++;

            if (height >= 2) {
                var aboveId = blockAt(binding, binding.pos().above()).toLowerCase();
                if (!isPassableForDescent(aboveId)) {
                    lastResult = TurtleToolCommand.dig(InteractDirection.UP, null).execute(turtle.getAccess());
                    binding.refreshFrom(turtle);
                    if (!lastResult.isSuccess()) {
                        return tunnelLineReceipt(
                                command,
                                binding,
                                false,
                                "Tunnel line blocked while clearing destination headroom at step " + (step + 1) + ".",
                                List.of("tunnel_headroom_dig_blocked"),
                                beforeInventory,
                                turtle,
                                routeId,
                                start,
                                length,
                                height,
                                stepsCompleted,
                                blocksRemoved);
                    }

                    blocksRemoved++;
                }
            }
        }

        binding.tunnelLineBlocksRemoved += blocksRemoved;

        return tunnelLineReceipt(
                command,
                binding,
                true,
                "Tunnel line completed.",
                List.of(),
                beforeInventory,
                turtle,
                routeId,
                start,
                length,
                height,
                stepsCompleted,
                blocksRemoved);
    }

    private static TurtleReceipt branchTunnel(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var side = JsonFields.string(command.rawJson(), "side").orElse("left").toLowerCase();
        var length = Math.max(1, Math.min(64, JsonFields.integer(command.rawJson(), "length").orElse(6)));
        var height = Math.max(1, Math.min(2, JsonFields.integer(command.rawJson(), "height").orElse(2)));
        var routeId = JsonFields.string(command.rawJson(), "routeId").orElse("route-branch-" + UUID.randomUUID().toString().replace("-", ""));
        var origin = binding.pos();
        var originalFacing = binding.facing();
        var branchFacing = "right".equals(side) ? rightOf(originalFacing) : leftOf(originalFacing);
        var beforeInventory = inventorySummary(turtle);

        var turnResult = turnTo(turtle, binding, branchFacing);
        if (!turnResult.isSuccess()) {
            return receiptFromResult(command, binding, turnResult, "Branch tunnel failed while turning toward " + side + " branch.");
        }

        var digResult = digTunnelForward(turtle, binding, length, height);
        if (!digResult.success()) {
            return branchTunnelReceipt(command, binding, false, digResult.message(), digResult.hazards(), beforeInventory, turtle, routeId, origin, originalFacing, branchFacing, side, length, height, digResult);
        }

        var returnResult = returnToPosition(turtle, binding, origin, 128);
        if (!returnResult.isSuccess()) {
            return branchTunnelReceipt(command, binding, false, "Branch tunnel could not return to origin.", List.of("branch_return_failed"), beforeInventory, turtle, routeId, origin, originalFacing, branchFacing, side, length, height, digResult);
        }

        var restoreFacing = turnTo(turtle, binding, originalFacing);
        if (!restoreFacing.isSuccess()) {
            return branchTunnelReceipt(command, binding, false, "Branch tunnel returned but could not restore original facing.", List.of("branch_facing_restore_failed"), beforeInventory, turtle, routeId, origin, originalFacing, branchFacing, side, length, height, digResult);
        }

        binding.branchTunnelCount++;
        return branchTunnelReceipt(command, binding, true, "Branch tunnel completed and returned to origin.", List.of(), beforeInventory, turtle, routeId, origin, originalFacing, branchFacing, side, length, height, digResult);
    }

    private static TurtleReceipt branchMinePattern(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var mainLength = Math.max(1, Math.min(64, JsonFields.integer(command.rawJson(), "mainLength").orElse(9)));
        var branchLength = Math.max(1, Math.min(64, JsonFields.integer(command.rawJson(), "branchLength").orElse(6)));
        var branchCount = Math.max(1, Math.min(8, JsonFields.integer(command.rawJson(), "branchCount").orElse(2)));
        var spacing = Math.max(1, Math.min(16, JsonFields.integer(command.rawJson(), "spacing").orElse(3)));
        var height = Math.max(1, Math.min(2, JsonFields.integer(command.rawJson(), "height").orElse(2)));
        var mainRouteId = JsonFields.string(command.rawJson(), "mainRouteId").orElse("route-main-" + UUID.randomUUID().toString().replace("-", ""));
        var sidePattern = JsonFields.string(command.rawJson(), "sidePattern").orElse("alternating").toLowerCase();
        var returnHome = JsonFields.bool(command.rawJson(), "returnHome").orElse(true);
        var start = binding.pos();
        var startFacing = binding.facing();
        var beforeInventory = inventorySummary(turtle);
        var mainSteps = 0;
        var branchesCompleted = 0;
        var blocksRemoved = 0;

        while (mainSteps < mainLength) {
            var segment = Math.min(spacing, mainLength - mainSteps);
            var mainResult = digTunnelForward(turtle, binding, segment, height);
            blocksRemoved += mainResult.blocksRemoved();
            if (!mainResult.success()) {
                return branchMinePatternReceipt(command, binding, false, mainResult.message(), mainResult.hazards(), beforeInventory, turtle, mainRouteId, start, startFacing, mainLength, branchLength, branchCount, branchesCompleted, blocksRemoved);
            }

            mainSteps += segment;
            if (branchesCompleted < branchCount) {
                var side = branchSide(sidePattern, branchesCompleted);
                var branchFacing = "right".equals(side) ? rightOf(startFacing) : leftOf(startFacing);
                var branchOrigin = binding.pos();
                var turnResult = turnTo(turtle, binding, branchFacing);
                if (!turnResult.isSuccess()) {
                    return branchMinePatternReceipt(command, binding, false, "Branch mine failed while turning toward branch.", List.of("branch_turn_failed"), beforeInventory, turtle, mainRouteId, start, startFacing, mainLength, branchLength, branchCount, branchesCompleted, blocksRemoved);
                }

                var branchResult = digTunnelForward(turtle, binding, branchLength, height);
                blocksRemoved += branchResult.blocksRemoved();
                if (!branchResult.success()) {
                    return branchMinePatternReceipt(command, binding, false, branchResult.message(), branchResult.hazards(), beforeInventory, turtle, mainRouteId, start, startFacing, mainLength, branchLength, branchCount, branchesCompleted, blocksRemoved);
                }

                var returnResult = returnToPosition(turtle, binding, branchOrigin, 128);
                if (!returnResult.isSuccess()) {
                    return branchMinePatternReceipt(command, binding, false, "Branch mine could not return from side branch.", List.of("branch_return_failed"), beforeInventory, turtle, mainRouteId, start, startFacing, mainLength, branchLength, branchCount, branchesCompleted, blocksRemoved);
                }

                var restoreMain = turnTo(turtle, binding, startFacing);
                if (!restoreMain.isSuccess()) {
                    return branchMinePatternReceipt(command, binding, false, "Branch mine could not restore main route facing.", List.of("main_facing_restore_failed"), beforeInventory, turtle, mainRouteId, start, startFacing, mainLength, branchLength, branchCount, branchesCompleted, blocksRemoved);
                }

                branchesCompleted++;
            }
        }

        if (returnHome) {
            var returnResult = returnToPosition(turtle, binding, start, 256);
            if (!returnResult.isSuccess()) {
                return branchMinePatternReceipt(command, binding, false, "Branch mine completed but could not return home.", List.of("branch_mine_return_failed"), beforeInventory, turtle, mainRouteId, start, startFacing, mainLength, branchLength, branchCount, branchesCompleted, blocksRemoved);
            }

            turnTo(turtle, binding, startFacing);
        }

        binding.branchMinePatternCount++;
        return branchMinePatternReceipt(command, binding, true, "Branch mine pattern completed.", List.of(), beforeInventory, turtle, mainRouteId, start, startFacing, mainLength, branchLength, branchCount, branchesCompleted, blocksRemoved);
    }

    private static TurtleReceipt tunnelLineReceipt(
            TurtleCommand command,
            TurtleBinding binding,
            boolean success,
            String summary,
            List<String> hazards,
            Map<String, Integer> beforeInventory,
            TurtleBlockEntity turtle,
            String routeId,
            BlockPos start,
            int requestedLength,
            int height,
            int stepsCompleted,
            int blocksRemoved) {
        var slots = inventorySlots(turtle);
        var pressure = inventoryPressure(slots.freeSlots());
        var inventoryDelta = inventoryDelta(beforeInventory, inventorySummary(turtle));
        var message = summary
                + " routeId=" + routeId
                + "; requestedLength=" + requestedLength
                + "; height=" + height
                + "; stepsCompleted=" + stepsCompleted
                + "; blocksRemoved=" + blocksRemoved
                + "; start=" + start.toShortString()
                + "; current=" + binding.pos().toShortString()
                + "; facing=" + binding.facing().getSerializedName()
                + "; boundingBox=" + tunnelBoundingBox(start, binding.facing(), requestedLength, height)
                + "; clearance=player_walkable"
                + "; inventoryOccupiedSlots=" + slots.occupiedSlots()
                + "; inventoryFreeSlots=" + slots.freeSlots()
                + "; inventoryPressure=" + pressure
                + "; storageRequirement=" + (pressure.equals("high") || pressure.equals("full") ? "inventory_pressure" : "none")
                + ".";
        return receipt(command, binding, success, blockAhead(binding), message, hazards, inventoryDelta);
    }

    private static TurtleReceipt branchTunnelReceipt(
            TurtleCommand command,
            TurtleBinding binding,
            boolean success,
            String summary,
            List<String> hazards,
            Map<String, Integer> beforeInventory,
            TurtleBlockEntity turtle,
            String routeId,
            BlockPos origin,
            Direction originalFacing,
            Direction branchFacing,
            String side,
            int requestedLength,
            int height,
            DigTunnelResult digResult) {
        var slots = inventorySlots(turtle);
        var pressure = inventoryPressure(slots.freeSlots());
        var message = summary
                + " routeId=" + routeId
                + "; parentRouteId=run_start"
                + "; side=" + side
                + "; start=" + origin.toShortString()
                + "; current=" + binding.pos().toShortString()
                + "; originalFacing=" + originalFacing.getSerializedName()
                + "; branchFacing=" + branchFacing.getSerializedName()
                + "; requestedLength=" + requestedLength
                + "; height=" + height
                + "; stepsCompleted=" + digResult.stepsCompleted()
                + "; blocksRemoved=" + digResult.blocksRemoved()
                + "; clearance=player_walkable"
                + "; inventoryFreeSlots=" + slots.freeSlots()
                + "; inventoryPressure=" + pressure
                + ".";
        return receipt(command, binding, success, blockAhead(binding), message, hazards, inventoryDelta(beforeInventory, inventorySummary(turtle)));
    }

    private static TurtleReceipt branchMinePatternReceipt(
            TurtleCommand command,
            TurtleBinding binding,
            boolean success,
            String summary,
            List<String> hazards,
            Map<String, Integer> beforeInventory,
            TurtleBlockEntity turtle,
            String mainRouteId,
            BlockPos start,
            Direction startFacing,
            int mainLength,
            int branchLength,
            int branchCount,
            int branchesCompleted,
            int blocksRemoved) {
        var slots = inventorySlots(turtle);
        var pressure = inventoryPressure(slots.freeSlots());
        var message = summary
                + " mainRouteId=" + mainRouteId
                + "; routeId=" + mainRouteId
                + "; start=" + start.toShortString()
                + "; current=" + binding.pos().toShortString()
                + "; facing=" + startFacing.getSerializedName()
                + "; mainLength=" + mainLength
                + "; branchLength=" + branchLength
                + "; branchCount=" + branchCount
                + "; branchesCompleted=" + branchesCompleted
                + "; blocksRemoved=" + blocksRemoved
                + "; clearance=player_walkable"
                + "; inventoryFreeSlots=" + slots.freeSlots()
                + "; inventoryPressure=" + pressure
                + "; storageRequirement=" + (pressure.equals("high") || pressure.equals("full") ? "inventory_pressure" : "none")
                + ".";
        return receipt(command, binding, success, blockAhead(binding), message, hazards, inventoryDelta(beforeInventory, inventorySummary(turtle)));
    }

    private static DigTunnelResult digTunnelForward(TurtleBlockEntity turtle, TurtleBinding binding, int length, int height) {
        var stepsCompleted = 0;
        var blocksRemoved = 0;
        for (var step = 0; step < length; step++) {
            var aheadId = blockAhead(binding).toLowerCase();
            if (!isPassableForDescent(aheadId)) {
                var digForward = TurtleToolCommand.dig(InteractDirection.FORWARD, null).execute(turtle.getAccess());
                binding.refreshFrom(turtle);
                if (!digForward.isSuccess()) {
                    return new DigTunnelResult(false, stepsCompleted, blocksRemoved, "Dig forward blocked at step " + (step + 1) + ".", List.of("tunnel_forward_dig_blocked"));
                }

                blocksRemoved++;
            }

            ensureSolarFuel(turtle);
            var move = new TurtleMoveCommand(MoveDirection.FORWARD).execute(turtle.getAccess());
            binding.refreshFrom(turtle);
            if (!move.isSuccess()) {
                return new DigTunnelResult(false, stepsCompleted, blocksRemoved, "Move forward blocked at step " + (step + 1) + ".", List.of("tunnel_forward_move_blocked"));
            }

            binding.forwardMoves++;
            binding.tunnelLineSteps++;
            binding.path.add(binding.pos());
            stepsCompleted++;

            if (height >= 2) {
                var aboveId = blockAt(binding, binding.pos().above()).toLowerCase();
                if (!isPassableForDescent(aboveId)) {
                    var digUp = TurtleToolCommand.dig(InteractDirection.UP, null).execute(turtle.getAccess());
                    binding.refreshFrom(turtle);
                    if (!digUp.isSuccess()) {
                        return new DigTunnelResult(false, stepsCompleted, blocksRemoved, "Headroom dig blocked at step " + step + ".", List.of("tunnel_headroom_dig_blocked"));
                    }

                    blocksRemoved++;
                }
            }
        }

        return new DigTunnelResult(true, stepsCompleted, blocksRemoved, "Tunnel segment completed.", List.of());
    }

    private static TurtleCommandResult returnToPosition(TurtleBlockEntity turtle, TurtleBinding binding, BlockPos target, int maxSteps) {
        var attempts = 0;
        while (!binding.pos().equals(target) && attempts < maxSteps) {
            var current = binding.pos();
            trimCurrentBreadcrumb(binding, current);
            if (binding.path.isEmpty()) {
                TurtleCommandResult directResult;
                if (target.getY() > current.getY()) {
                    directResult = new TurtleMoveCommand(MoveDirection.UP).execute(turtle.getAccess());
                } else if (target.getY() < current.getY()) {
                    directResult = new TurtleMoveCommand(MoveDirection.DOWN).execute(turtle.getAccess());
                } else {
                    var direction = directStepDirection(current, target);
                    if (direction.isEmpty()) {
                        return TurtleCommandResult.failure("No direct step toward target.");
                    }

                    var turn = turnTo(turtle, binding, direction.get());
                    if (!turn.isSuccess()) {
                        return turn;
                    }

                    directResult = new TurtleMoveCommand(MoveDirection.FORWARD).execute(turtle.getAccess());
                }

                binding.refreshFrom(turtle);
                if (!directResult.isSuccess()) {
                    return directResult;
                }

                binding.returnMoves++;
                attempts++;
                continue;
            }

            var next = binding.path.remove(binding.path.size() - 1);
            TurtleCommandResult result;
            if (next.getY() > current.getY()) {
                result = new TurtleMoveCommand(MoveDirection.UP).execute(turtle.getAccess());
            } else if (next.getY() < current.getY()) {
                result = new TurtleMoveCommand(MoveDirection.DOWN).execute(turtle.getAccess());
            } else {
                var direction = directionBetween(current, next);
                if (direction.isEmpty()) {
                    return TurtleCommandResult.failure("Breadcrumb target is not adjacent.");
                }

                var turn = turnTo(turtle, binding, direction.get());
                if (!turn.isSuccess()) {
                    return turn;
                }

                result = new TurtleMoveCommand(MoveDirection.FORWARD).execute(turtle.getAccess());
            }

            binding.refreshFrom(turtle);
            if (!result.isSuccess()) {
                return result;
            }

            binding.returnMoves++;
            attempts++;
        }

        return binding.pos().equals(target)
                ? TurtleCommandResult.success()
                : TurtleCommandResult.failure("Return target budget exhausted.");
    }

    private static String branchSide(String sidePattern, int branchIndex) {
        if ("left".equals(sidePattern) || "left_only".equals(sidePattern)) {
            return "left";
        }

        if ("right".equals(sidePattern) || "right_only".equals(sidePattern)) {
            return "right";
        }

        return branchIndex % 2 == 0 ? "left" : "right";
    }

    private static String tunnelBoundingBox(BlockPos start, Direction facing, int requestedLength, int height) {
        var first = start.relative(facing);
        var last = start.relative(facing, Math.max(1, requestedLength));
        var minX = Math.min(first.getX(), last.getX());
        var maxX = Math.max(first.getX(), last.getX());
        var minZ = Math.min(first.getZ(), last.getZ());
        var maxZ = Math.max(first.getZ(), last.getZ());
        var minY = start.getY();
        var maxY = start.getY() + Math.max(1, height) - 1;
        return minX + "," + minY + "," + minZ + "->" + maxX + "," + maxY + "," + maxZ;
    }

    private static TurtleReceipt scanNearby(TurtleCommand command, TurtleBinding binding) {
        var radius = Math.max(1, Math.min(16, JsonFields.integer(command.rawJson(), "radius").orElse(8)));
        var maxMatches = Math.max(1, Math.min(64, JsonFields.integer(command.rawJson(), "maxMatches").orElse(16)));
        var blockContains = JsonFields.string(command.rawJson(), "blockContains").orElse("").toLowerCase();
        var tag = JsonFields.string(command.rawJson(), "tag").orElse("").toLowerCase();
        var queryLogs = tag.contains("logs") || blockContains.contains("log") || command.rawJson().toLowerCase().contains("minecraft:logs");
        var center = binding.pos();
        var matches = new ArrayList<ScannedBlock>();
        var excludedStaleTargets = 0;
        var rejectedElevatedTargets = 0;
        var baseCandidateCount = 0;
        var harvestScan = queryLogs && "turtlequest.harvest_tree".equals(binding.currentBehaviorId);
        var min = center.offset(-radius, -radius, -radius);
        var max = center.offset(radius, radius, radius);

        for (var candidate : BlockPos.betweenClosed(min, max)) {
            var state = binding.level().getBlockState(candidate);
            if (state.isAir()) {
                continue;
            }

            var blockId = BuiltInRegistries.BLOCK.getKey(state.getBlock()).toString().toLowerCase();
            var matched = false;
            if (!blockContains.isBlank() && blockId.contains(blockContains)) {
                matched = true;
            }

            if (queryLogs && isLogLike(blockId)) {
                matched = true;
            }

            if (!matched) {
                continue;
            }

            if (queryLogs && isStaleHarvestTarget(binding, candidate)) {
                excludedStaleTargets++;
                continue;
            }

            if (harvestScan && candidate.getY() != center.getY()) {
                rejectedElevatedTargets++;
                continue;
            }

            var baseCandidate = queryLogs && isLikelyTreeBase(binding, candidate);
            if (baseCandidate) {
                baseCandidateCount++;
            }

            matches.add(new ScannedBlock(
                    candidate.immutable(),
                    blockId,
                    manhattan(candidate, center),
                    baseCandidate ? 0 : 1,
                    Math.abs(candidate.getY() - center.getY())));
        }

        matches.sort(Comparator
                .comparingInt(ScannedBlock::qualityPenalty)
                .thenComparingInt(ScannedBlock::verticalPenalty)
                .thenComparingInt(ScannedBlock::distance));
        var sample = matches.stream()
                .limit(maxMatches)
                .map(match -> match.blockId()
                        + "@"
                        + relativePosition(match.pos(), center)
                        + "#d"
                        + match.distance()
                        + (match.qualityPenalty() == 0 ? "#base" : ""))
                .toList();
        binding.scanNearbyCount++;
        if (!matches.isEmpty()) {
            var nearest = matches.getFirst();
            binding.lastScanRelativeX = nearest.pos().getX() - center.getX();
            binding.lastScanRelativeY = nearest.pos().getY() - center.getY();
            binding.lastScanRelativeZ = nearest.pos().getZ() - center.getZ();
            binding.lastScanBlockId = nearest.blockId();
        } else {
            binding.lastScanRelativeX = null;
            binding.lastScanRelativeY = null;
            binding.lastScanRelativeZ = null;
            binding.lastScanBlockId = "";
        }
        var message = "scanNearby radius=" + radius
                + "; query=" + (queryLogs ? "minecraft:logs" : blockContains)
                + "; matches=" + matches.size()
                + "; staleExcluded=" + excludedStaleTargets
                + "; elevatedRejected=" + rejectedElevatedTargets
                + "; baseCandidates=" + baseCandidateCount
                + "; nearest=" + String.join(";", sample);
        return receipt(command, binding, true, blockAhead(binding), message, List.of());
    }

    private static boolean isStaleHarvestTarget(TurtleBinding binding, BlockPos pos) {
        return binding.harvestedTreeColumns.contains(columnKey(pos))
                || binding.ignoredScanTargets.contains(positionKey(pos));
    }

    private static boolean isLikelyTreeBase(TurtleBinding binding, BlockPos pos) {
        var blockId = blockAt(binding, pos).toLowerCase();
        if (!isLogLike(blockId)) {
            return false;
        }

        var below = blockAt(binding, pos.below()).toLowerCase();
        if (isLogLike(below)) {
            return false;
        }

        var above = blockAt(binding, pos.above()).toLowerCase();
        return isLogLike(above);
    }

    private static boolean isLogLike(String blockId) {
        return blockId.endsWith("_log")
                || blockId.endsWith("_wood")
                || blockId.endsWith("_stem")
                || blockId.endsWith("_hyphae")
                || blockId.contains(":stripped_") && (
                    blockId.endsWith("_log")
                    || blockId.endsWith("_wood")
                    || blockId.endsWith("_stem")
                    || blockId.endsWith("_hyphae"));
    }

    private static boolean isPassableForDescent(String blockId) {
        return blockId.equals("minecraft:air")
                || blockId.equals("minecraft:cave_air")
                || blockId.equals("minecraft:void_air");
    }

    private static boolean isSoftRecoverableBlock(String blockId) {
        return blockId.endsWith("_leaves")
                || blockId.endsWith("_vine")
                || blockId.equals("minecraft:vine")
                || blockId.equals("minecraft:short_grass")
                || blockId.equals("minecraft:tall_grass")
                || blockId.equals("minecraft:fern")
                || blockId.equals("minecraft:large_fern");
    }

    private static String relativePosition(BlockPos pos, BlockPos origin) {
        return (pos.getX() - origin.getX()) + "," + (pos.getY() - origin.getY()) + "," + (pos.getZ() - origin.getZ());
    }

    private static String positionKey(BlockPos pos) {
        return pos.getX() + "," + pos.getY() + "," + pos.getZ();
    }

    private static String columnKey(BlockPos pos) {
        return pos.getX() + "," + pos.getZ();
    }

    private static TurtleReceipt runReturnHome(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var attempts = 0;
        var optional = JsonFields.bool(command.rawJson(), "optional").orElse(false);
        TurtleCommandResult lastResult = TurtleCommandResult.success();
        while (!turtle.getAccess().getPosition().equals(binding.startPos()) && attempts < 128) {
            var current = turtle.getAccess().getPosition();
            trimCurrentBreadcrumb(binding, current);
            if (binding.path.isEmpty()) {
                if (manhattan(current, binding.startPos()) == 1) {
                    var direction = directionBetween(current, binding.startPos());
                    if (direction.isPresent()) {
                        var turnResult = turnTo(turtle, binding, direction.get());
                        if (!turnResult.isSuccess()) {
                            return receiptFromResult(command, binding, turnResult, "Return home failed while turning toward adjacent start.");
                        }

                        ensureSolarFuel(turtle);
                        var directResult = new TurtleMoveCommand(MoveDirection.FORWARD).execute(turtle.getAccess());
                        binding.refreshFrom(turtle);
                        if (!directResult.isSuccess()) {
                            return receiptFromResult(command, binding, directResult, "Return home failed while stepping into adjacent start.");
                        }

                        binding.returnMoves++;
                        attempts++;
                        continue;
                    }
                }

                if (optional) {
                    return receipt(command, binding, true, blockAhead(binding), "Optional return home skipped because no breadcrumb path is available.", List.of("return_path_missing_optional"));
                }

                return receipt(command, binding, false, blockAhead(binding), "Return home has no breadcrumb path back to start.", List.of("return_path_missing"));
            }

            var target = binding.path.remove(binding.path.size() - 1);
            TurtleCommandResult moveResult;
            ensureSolarFuel(turtle);
            if (target.getY() > current.getY()) {
                moveResult = new TurtleMoveCommand(MoveDirection.UP).execute(turtle.getAccess());
            } else if (target.getY() < current.getY()) {
                moveResult = new TurtleMoveCommand(MoveDirection.DOWN).execute(turtle.getAccess());
            } else {
                var direction = directionBetween(current, target);
                if (direction.isEmpty()) {
                    return receipt(command, binding, false, blockAhead(binding), "Return breadcrumb is not adjacent: " + target.toShortString(), List.of("return_path_invalid"));
                }

                var turnResult = turnTo(turtle, binding, direction.get());
                if (!turnResult.isSuccess()) {
                    return receiptFromResult(command, binding, turnResult, "Return home failed while turning toward breadcrumb.");
                }

                moveResult = new TurtleMoveCommand(MoveDirection.FORWARD).execute(turtle.getAccess());
            }

            lastResult = moveResult;
            binding.refreshFrom(turtle);
            attempts++;
            if (!lastResult.isSuccess()) {
                return receiptFromResult(command, binding, lastResult, "Return home blocked after " + attempts + " step(s).");
            }
        }

        var success = turtle.getAccess().getPosition().equals(binding.startPos());
        var message = success
                ? "Returned home after " + attempts + " breadcrumb step(s)."
                : "Return home exhausted recorded path without reaching start.";
        return receipt(command, binding, success, blockAhead(binding), message, success ? List.of() : List.of("return_home_failed"));
    }

    private static TurtleReceipt recoverToGround(TurtleCommand command, TurtleBinding binding, TurtleBlockEntity turtle) {
        var maxDown = Math.max(1, Math.min(64, JsonFields.integer(command.rawJson(), "maxDown").orElse(32)));
        var digSoftBelow = JsonFields.bool(command.rawJson(), "digSoftBelow").orElse(true);
        var descended = 0;
        var clearedSoftBlocks = 0;
        TurtleCommandResult lastResult = TurtleCommandResult.success();

        while (descended < maxDown) {
            var below = blockAt(binding, binding.pos().below()).toLowerCase();
            if (!isPassableForDescent(below)) {
                if (digSoftBelow && isSoftRecoverableBlock(below)) {
                    lastResult = TurtleToolCommand.dig(InteractDirection.DOWN, null).execute(turtle.getAccess());
                    binding.refreshFrom(turtle);
                    if (!lastResult.isSuccess()) {
                        return receiptFromResult(command, binding, lastResult, "Recovery failed while clearing soft block below.");
                    }

                    clearedSoftBlocks++;
                    below = blockAt(binding, binding.pos().below()).toLowerCase();
                    if (!isPassableForDescent(below)) {
                        var message = "Recovery stopped on support after clearing soft block; descended="
                                + descended
                                + "; clearedSoftBlocks="
                                + clearedSoftBlocks
                                + "; supportBelow="
                                + below
                                + ".";
                        return receipt(command, binding, true, blockAhead(binding), message, List.of());
                    }
                } else {
                    var message = "Recovery reached support; descended="
                            + descended
                            + "; clearedSoftBlocks="
                            + clearedSoftBlocks
                            + "; supportBelow="
                            + below
                            + ".";
                    return receipt(command, binding, true, blockAhead(binding), message, List.of());
                }
            }

            trimCurrentBreadcrumb(binding, binding.pos());
            ensureSolarFuel(turtle);
            lastResult = new TurtleMoveCommand(MoveDirection.DOWN).execute(turtle.getAccess());
            binding.refreshFrom(turtle);
            if (!lastResult.isSuccess()) {
                return receiptFromResult(command, binding, lastResult, "Recovery failed while descending.");
            }

            descended++;
        }

        var finalBelow = blockAt(binding, binding.pos().below()).toLowerCase();
        var grounded = !isPassableForDescent(finalBelow) && !isSoftRecoverableBlock(finalBelow);
        var message = "Recovery descent budget exhausted; descended="
                + descended
                + "; clearedSoftBlocks="
                + clearedSoftBlocks
                + "; supportBelow="
                + finalBelow
                + "; grounded="
                + grounded
                + ".";
        return receipt(command, binding, grounded, blockAhead(binding), message, grounded ? List.of() : List.of("recovery_descent_budget_exhausted"));
    }

    private static void trimCurrentBreadcrumb(TurtleBinding binding, BlockPos current) {
        while (!binding.path.isEmpty() && binding.path.get(binding.path.size() - 1).equals(current)) {
            binding.path.remove(binding.path.size() - 1);
        }
    }

    private static TurtleReceipt completeObjective(TurtleCommand command, TurtleBinding binding) {
        var atStart = binding.pos().equals(binding.startPos());
        var pitCompleted = "turtlequest.excavate_rectangular_pit".equals(binding.currentBehaviorId)
                && binding.digDownCount > 0;
        var columnCompleted = "turtlequest.build_column".equals(binding.currentBehaviorId)
                && binding.placeDownCount > 0;
        var treeScanCompleted = "turtlequest.find_tree".equals(binding.currentBehaviorId)
                && binding.scanNearbyCount > 0;
        var treeApproachCompleted = "turtlequest.harvest_tree".equals(binding.currentBehaviorId)
                && binding.scanNearbyCount > 0
                && binding.approachMoves > 0
                && binding.rememberedTargetDigCount > 0
                && binding.verticalLogDigCount > 0
                && atStart;
        var recoveryCompleted = "turtlequest.recover_turtle".equals(binding.currentBehaviorId)
                && !isPassableForDescent(blockAt(binding, binding.pos().below()).toLowerCase());
        var tunnelLineCompleted = "turtlequest.tunnel_line".equals(binding.currentBehaviorId)
                && binding.tunnelLineSteps > 0
                && (!atStart || binding.returnMoves > 0);
        var branchCompleted = "turtlequest.branch_tunnel".equals(binding.currentBehaviorId)
                && binding.branchTunnelCount > 0;
        var branchMineCompleted = "turtlequest.branch_mine_pattern".equals(binding.currentBehaviorId)
                && binding.branchMinePatternCount > 0;
        var diagnosticCompleted = "turtlequest.assist".equals(binding.currentBehaviorId);
        var success = (atStart && binding.forwardMoves > 0) || pitCompleted || columnCompleted || treeScanCompleted || treeApproachCompleted || recoveryCompleted || tunnelLineCompleted || branchCompleted || branchMineCompleted || diagnosticCompleted;
        var hazards = success ? List.<String>of() : List.of("objective_not_satisfied");
        var message = success
                ? completionMessage(binding)
                : "TurtleQuest objective is not satisfied; turtle is not back at start or did not move.";
        return receipt(command, binding, success, blockAhead(binding), message, hazards);
    }

    private static String completionMessage(TurtleBinding binding) {
        if ("turtlequest.excavate_rectangular_pit".equals(binding.currentBehaviorId)) {
            return "TurtleQuest pit objective completed with receipt-backed dig-down steps: " + binding.digDownCount + ".";
        }

        if ("turtlequest.assist".equals(binding.currentBehaviorId)) {
            return "TurtleQuest assist objective completed with local inspection receipts.";
        }

        if ("turtlequest.build_column".equals(binding.currentBehaviorId)) {
            return "TurtleQuest column objective completed with receipt-backed place-down steps: " + binding.placeDownCount + ".";
        }

        if ("turtlequest.find_tree".equals(binding.currentBehaviorId)) {
            return "TurtleQuest tree scan objective completed with receipt-backed scanNearby steps: " + binding.scanNearbyCount + ".";
        }

        if ("turtlequest.harvest_tree".equals(binding.currentBehaviorId)) {
            return "TurtleQuest tree harvest v0 completed with scan steps: "
                    + binding.scanNearbyCount
                    + ", approach moves: " + binding.approachMoves
                    + ", base cuts: " + binding.rememberedTargetDigCount
                    + ", vertical cuts: " + binding.verticalLogDigCount
                    + ", returnedHome: " + binding.pos().equals(binding.startPos()) + ".";
        }

        if ("turtlequest.recover_turtle".equals(binding.currentBehaviorId)) {
            return "TurtleQuest recovery completed; supportBelow="
                    + blockAt(binding, binding.pos().below()).toLowerCase()
                    + ", atStart: "
                    + binding.pos().equals(binding.startPos())
                    + ".";
        }

        if ("turtlequest.tunnel_line".equals(binding.currentBehaviorId)) {
            return "TurtleQuest tunnel line completed with steps: "
                    + binding.tunnelLineSteps
                    + ", blocksRemoved: "
                    + binding.tunnelLineBlocksRemoved
                    + ", returnedHome: "
                    + binding.pos().equals(binding.startPos())
                    + ".";
        }

        if ("turtlequest.branch_tunnel".equals(binding.currentBehaviorId)) {
            return "TurtleQuest branch tunnel completed with branch tunnels: "
                    + binding.branchTunnelCount
                    + ", returnedHome: "
                    + binding.pos().equals(binding.startPos())
                    + ".";
        }

        if ("turtlequest.branch_mine_pattern".equals(binding.currentBehaviorId)) {
            return "TurtleQuest branch mine pattern completed with patterns: "
                    + binding.branchMinePatternCount
                    + ", tunnel steps: "
                    + binding.tunnelLineSteps
                    + ", returnedHome: "
                    + binding.pos().equals(binding.startPos())
                    + ".";
        }

        return "TurtleQuest objective completed at start position with receipt-backed movement. Forward steps: "
                + binding.forwardMoves + ", return steps: " + binding.returnMoves + ".";
    }

    private static int manhattan(BlockPos left, BlockPos right) {
        return Math.abs(left.getX() - right.getX())
                + Math.abs(left.getY() - right.getY())
                + Math.abs(left.getZ() - right.getZ());
    }

    private static int horizontalDistance(int dx, int dz) {
        return Math.abs(dx) + Math.abs(dz);
    }

    private static Optional<Direction> parseHorizontalDirection(String value) {
        return switch (value.toLowerCase()) {
            case "north" -> Optional.of(Direction.NORTH);
            case "east" -> Optional.of(Direction.EAST);
            case "south" -> Optional.of(Direction.SOUTH);
            case "west" -> Optional.of(Direction.WEST);
            default -> Optional.empty();
        };
    }

    private static Optional<Direction> nextDirectionToward(int dx, int dz) {
        if (Math.abs(dx) >= Math.abs(dz) && dx != 0) {
            return Optional.of(dx > 0 ? Direction.EAST : Direction.WEST);
        }

        if (dz != 0) {
            return Optional.of(dz > 0 ? Direction.SOUTH : Direction.NORTH);
        }

        return Optional.empty();
    }

    private static Optional<Direction> directionBetween(BlockPos from, BlockPos to) {
        var dx = to.getX() - from.getX();
        var dz = to.getZ() - from.getZ();
        if (Math.abs(dx) + Math.abs(dz) != 1 || to.getY() != from.getY()) {
            return Optional.empty();
        }

        if (dx > 0) {
            return Optional.of(Direction.EAST);
        }

        if (dx < 0) {
            return Optional.of(Direction.WEST);
        }

        return Optional.of(dz > 0 ? Direction.SOUTH : Direction.NORTH);
    }

    private static TurtleCommandResult turnTo(TurtleBlockEntity turtle, TurtleBinding binding, Direction target) {
        if (target.getAxis().isVertical()) {
            return TurtleCommandResult.failure("Cannot face vertical direction.");
        }

        var current = turtle.getAccess().getDirection();
        for (var i = 0; i < 4 && current != target; i++) {
            var turn = rightTurnsTo(current, target) <= leftTurnsTo(current, target)
                    ? TurnDirection.RIGHT
                    : TurnDirection.LEFT;
            var result = new TurtleTurnCommand(turn).execute(turtle.getAccess());
            binding.refreshFrom(turtle);
            if (!result.isSuccess()) {
                return result;
            }

            current = turtle.getAccess().getDirection();
        }

        return current == target
                ? TurtleCommandResult.success()
                : TurtleCommandResult.failure("Could not face requested direction.");
    }

    private static int rightTurnsTo(Direction current, Direction target) {
        var steps = 0;
        var cursor = current;
        while (cursor != target && steps < 4) {
            cursor = rightOf(cursor);
            steps++;
        }

        return steps;
    }

    private static int leftTurnsTo(Direction current, Direction target) {
        var steps = 0;
        var cursor = current;
        while (cursor != target && steps < 4) {
            cursor = leftOf(cursor);
            steps++;
        }

        return steps;
    }

    private static Direction rightOf(Direction direction) {
        return switch (direction) {
            case NORTH -> Direction.EAST;
            case EAST -> Direction.SOUTH;
            case SOUTH -> Direction.WEST;
            case WEST -> Direction.NORTH;
            default -> Direction.NORTH;
        };
    }

    private static Direction leftOf(Direction direction) {
        return switch (direction) {
            case NORTH -> Direction.WEST;
            case WEST -> Direction.SOUTH;
            case SOUTH -> Direction.EAST;
            case EAST -> Direction.NORTH;
            default -> Direction.NORTH;
        };
    }

    private static void ensureSolarFuel(TurtleBlockEntity turtle) {
        var access = turtle.getAccess();
        if (access.isFuelNeeded() && access.getFuelLevel() < 1) {
            access.addFuel(1);
        }
    }

    private static TurtleReceipt runTurtleCommand(
            TurtleCommand command,
            TurtleBinding binding,
            dan200.computercraft.api.turtle.TurtleCommand turtleCommand,
            String successMessage) {
        var blockEntity = binding.level().getBlockEntity(binding.pos());
        if (!(blockEntity instanceof TurtleBlockEntity turtle)) {
            return receipt(command, binding, false, null, "Bound turtle is no longer present.", List.of("turtle_missing"));
        }

        var result = turtleCommand.execute(turtle.getAccess());
        binding.refreshFrom(turtle);
        if (result.isSuccess() && "digDown".equals(command.action())) {
            binding.digDownCount++;
        }

        return receiptFromResult(command, binding, result, successMessage);
    }

    private static TurtleReceipt receiptFromResult(
            TurtleCommand command,
            TurtleBinding binding,
            TurtleCommandResult result,
            String successMessage) {
        return receiptFromResult(command, binding, result, successMessage, Map.of());
    }

    private static TurtleReceipt receiptFromResult(
            TurtleCommand command,
            TurtleBinding binding,
            TurtleCommandResult result,
            String successMessage,
            Map<String, Integer> inventoryDelta) {
        if (result.isSuccess()) {
            return receipt(command, binding, true, blockAhead(binding), successMessage, List.of(), inventoryDelta);
        }

        var error = result.getErrorMessage() == null ? "Turtle command failed." : result.getErrorMessage();
        return receipt(command, binding, false, blockAhead(binding), error, List.of("cc_tweaked_command_failed"), inventoryDelta);
    }

    private static TurtleReceipt inspect(TurtleCommand command, TurtleBinding binding) {
        return receipt(command, binding, true, blockAhead(binding), "Block ahead inspected.", hazardsAhead(binding));
    }

    private static TurtleReceipt inspectAt(TurtleCommand command, TurtleBinding binding, BlockPos pos, String message) {
        return receipt(command, binding, true, blockAt(binding, pos), message, hazardsAt(binding, pos));
    }

    private static String blockAhead(TurtleBinding binding) {
        return blockAt(binding, binding.pos().relative(binding.facing()));
    }

    private static String blockAt(TurtleBinding binding, BlockPos pos) {
        var state = binding.level().getBlockState(pos);
        return BuiltInRegistries.BLOCK.getKey(state.getBlock()).toString();
    }

    private static List<String> hazardsAhead(TurtleBinding binding) {
        return hazardsAt(binding, binding.pos().relative(binding.facing()));
    }

    private static List<String> hazardsAt(TurtleBinding binding, BlockPos pos) {
        var hazards = new ArrayList<String>();
        var state = binding.level().getBlockState(pos);
        if (!state.getFluidState().isEmpty()) {
            hazards.add("fluid");
        }

        return hazards;
    }

    private static TurtleReceipt receipt(
            TurtleCommand command,
            TurtleBinding binding,
            boolean success,
            String blockAhead,
            String message,
            List<String> hazards) {
        return receipt(command, binding, success, blockAhead, message, hazards, Map.of());
    }

    private static TurtleReceipt receipt(
            TurtleCommand command,
            TurtleBinding binding,
            boolean success,
            String blockAhead,
            String message,
            List<String> hazards,
            Map<String, Integer> inventoryDelta) {
        return new TurtleReceipt(
                command.runId(),
                binding.turtleId(),
                command.commandId(),
                command.action(),
                success,
                binding.pos(),
                binding.facing().getSerializedName(),
                Instant.now().toString(),
                blockAhead,
                hazards,
                inventoryDelta,
                message);
    }

    private static Map<String, Integer> inventorySummary(TurtleBlockEntity turtle) {
        var summary = new HashMap<String, Integer>();
        var inventory = turtle.getAccess().getInventory();
        for (var slot = 0; slot < inventory.getContainerSize(); slot++) {
            var stack = inventory.getItem(slot);
            if (stack.isEmpty()) {
                continue;
            }

            var itemId = BuiltInRegistries.ITEM.getKey(stack.getItem()).toString();
            summary.merge(itemId, stack.getCount(), Integer::sum);
        }

        return summary;
    }

    private static Map<String, Integer> inventoryDelta(Map<String, Integer> before, Map<String, Integer> after) {
        var delta = new LinkedHashMap<String, Integer>();
        var keys = new ArrayList<String>();
        keys.addAll(before.keySet());
        for (var key : after.keySet()) {
            if (!keys.contains(key)) {
                keys.add(key);
            }
        }

        keys.sort(String::compareTo);
        for (var key : keys) {
            var change = after.getOrDefault(key, 0) - before.getOrDefault(key, 0);
            if (change != 0) {
                delta.put(key, change);
            }
        }

        return delta;
    }

    private static InventorySlots inventorySlots(TurtleBlockEntity turtle) {
        var inventory = turtle.getAccess().getInventory();
        var occupied = 0;
        for (var slot = 0; slot < inventory.getContainerSize(); slot++) {
            if (!inventory.getItem(slot).isEmpty()) {
                occupied++;
            }
        }

        return new InventorySlots(occupied, inventory.getContainerSize() - occupied);
    }

    private static String inventoryPressure(int freeSlots) {
        if (freeSlots <= 0) {
            return "full";
        }

        if (freeSlots <= 2) {
            return "high";
        }

        if (freeSlots <= 5) {
            return "medium";
        }

        return "low";
    }

    private static HttpResponse<String> bridgeRequest(String method, String path, String body) {
        return bridgeRequest(method, path, body, BRIDGE_REQUEST_TIMEOUT_SECONDS);
    }

    private static HttpResponse<String> bridgeRequest(String method, String path, String body, int timeoutSeconds) {
        try {
            var builder = HttpRequest.newBuilder()
                    .uri(URI.create(BRIDGE_URL + path))
                    .timeout(Duration.ofSeconds(timeoutSeconds));
            if ("GET".equals(method)) {
                builder.GET();
            } else {
                builder.header("Content-Type", "application/json")
                        .POST(HttpRequest.BodyPublishers.ofString(body));
            }

            return HTTP.send(builder.build(), HttpResponse.BodyHandlers.ofString());
        } catch (IOException exception) {
            throw new CompletionException(exception);
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            throw new CompletionException(exception);
        }
    }

    private static int bridgeGet(CommandSourceStack source, String path, String label) {
        var request = HttpRequest.newBuilder()
                .uri(URI.create(BRIDGE_URL + path))
                .timeout(Duration.ofSeconds(5))
                .GET()
                .build();

        sendAsync(source, request, label);
        return 1;
    }

    private static int bridgePost(CommandSourceStack source, String path, String body, String label) {
        var request = HttpRequest.newBuilder()
                .uri(URI.create(BRIDGE_URL + path))
                .timeout(Duration.ofSeconds(5))
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body))
                .build();

        sendAsync(source, request, label);
        return 1;
    }

    private static void sendAsync(CommandSourceStack source, HttpRequest request, String label) {
        HTTP.sendAsync(request, HttpResponse.BodyHandlers.ofString())
                .whenComplete((response, error) -> {
                    if (error != null) {
                        LOGGER.warn("{} bridge request failed.", label, error);
                        failure(source, label + " failed: " + rootMessage(error));
                        return;
                    }

                    if (response.statusCode() < 200 || response.statusCode() >= 300) {
                        LOGGER.warn("{} bridge request returned HTTP {}: {}", label, response.statusCode(), response.body());
                        failure(source, label + " returned HTTP " + response.statusCode());
                        return;
                    }

                    success(source, label + ": " + response.body());
                });
    }

    private static void success(CommandSourceStack source, String message) {
        source.getServer().execute(() -> source.sendSuccess(() -> Component.literal(message), false));
    }

    private static void failure(CommandSourceStack source, String message) {
        source.getServer().execute(() -> source.sendFailure(Component.literal(message)));
    }

    private static void progressForCommand(TurtleBinding binding, TurtleCommand command) {
        switch (command.action()) {
            case "startBehavior" -> progress(
                    binding,
                    "Starting " + JsonFields.string(command.rawJson(), "behaviorId").orElse("turtle behavior") + ".",
                    true);
            case "scanNearby" -> progress(binding, "Scanning nearby blocks.", false);
            case "moveTowardRelative" -> progress(binding, "Moving toward selected target.", false);
            case "digRememberedTarget" -> progress(binding, "Harvesting selected target block.", true);
            case "fellRememberedTree" -> progress(binding, "Felling remembered tree trunk.", true);
            case "recoverToGround" -> progress(binding, "Recovering to safe ground.", true);
            case "tunnelLine" -> progress(binding, "Tunneling forward and tracking inventory pressure.", true);
            case "markWaypoint" -> progress(binding, "Marking route waypoint.", true);
            case "returnToPosition" -> progress(binding, "Returning to a known position.", true);
            case "branchTunnel" -> progress(binding, "Digging side branch and returning to origin.", true);
            case "branchMinePattern" -> progress(binding, "Digging branch mine pattern.", true);
            case "placeStorage" -> progress(binding, "Placing and recording home storage.", true);
            case "depositInventory" -> progress(binding, "Depositing inventory into storage.", true);
            case "returnHome" -> progress(binding, "Returning by breadcrumbs.", true);
            case "discardJunk" -> progress(binding, "Clearing inventory pressure with default junk discard.", true);
            case "drop", "dropUp", "dropDown" -> progress(binding, "Dropping selected inventory.", true);
            case "suck", "suckUp", "suckDown" -> progress(binding, "Collecting inventory from adjacent block.", true);
            case "craft" -> progress(binding, "Crafting from turtle inventory.", true);
            case "detect", "detectUp", "detectDown" -> progress(binding, "Detecting adjacent block occupancy.", false);
            case "getInventory" -> progress(binding, "Checking inventory.", false);
            case "emitStatus" -> {
                var stage = JsonFields.string(command.rawJson(), "stage").orElse("");
                progress(binding, stage.isBlank() ? "Status update received." : "Status: " + stage + ".", true);
            }
            case "completeObjective" -> progress(binding, "Checking objective completion.", true);
            default -> {
                if (command.action().startsWith("move") || command.action().startsWith("dig") || command.action().startsWith("place")) {
                    progress(binding, "Executing " + command.action() + ".", false);
                }
            }
        }
    }

    private static void progress(TurtleBinding binding, String message, boolean force) {
        if (!CHAT_PROGRESS_ENABLED || binding.ownerPlayerId == null) {
            return;
        }

        var now = System.currentTimeMillis();
        if (!force && now - binding.lastProgressChatAtMs < CHAT_PROGRESS_MIN_INTERVAL_MS) {
            return;
        }

        if (message.equals(binding.lastProgressMessage) && !force) {
            return;
        }

        binding.lastProgressChatAtMs = now;
        binding.lastProgressMessage = message;
        binding.level().getServer().execute(() -> {
            var player = binding.level().getServer().getPlayerList().getPlayer(binding.ownerPlayerId);
            if (player != null) {
                player.sendSystemMessage(Component.literal("[TurtleQuest] " + turtleLabel(binding) + ": " + message));
            }
        });
    }

    private static String turtleLabel(TurtleBinding binding) {
        return binding.displayName == null || binding.displayName.isBlank()
                ? binding.turtleId()
                : binding.displayName;
    }

    private static String summarizeReceiptMessage(String message) {
        if (message.length() <= 140) {
            return message;
        }

        return message.substring(0, 137) + "...";
    }

    private static String requestJson(TurtleBinding turtle, ServerPlayer player, String prompt) {
        BlockPos pos = turtle.pos();
        return "{"
                + "\"turtleId\":\"" + json(turtle.turtleId()) + "\","
                + "\"turtleName\":\"" + json(turtle.displayName == null ? "" : turtle.displayName) + "\","
                + "\"worldId\":\"" + json(turtle.worldId()) + "\","
                + "\"playerId\":\"" + json(player.getUUID().toString()) + "\","
                + "\"message\":\"" + json(prompt) + "\","
                + "\"position\":{\"x\":" + pos.getX() + ",\"y\":" + pos.getY() + ",\"z\":" + pos.getZ() + "},"
                + "\"orientation\":\"" + json(turtle.facing().getSerializedName()) + "\""
                + "}";
    }

    private static String runtimeReplanJson() {
        return "{"
                + "\"mode\":\"" + json(RUNTIME_REPLAN_MODE) + "\","
                + "\"repairAttempts\":" + Math.max(0, RUNTIME_REPLAN_ATTEMPTS)
                + "}";
    }

    private static String snapshotJson(String snapshotId, TurtleBinding turtle, int sizeX, int sizeY, int sizeZ) {
        var origin = turtle.pos().offset(-(sizeX / 2), -(sizeY - 1), -(sizeZ / 2));
        var blocks = new ArrayList<String>(sizeX * sizeY * sizeZ);
        for (int y = 0; y < sizeY; y++) {
            for (int z = 0; z < sizeZ; z++) {
                for (int x = 0; x < sizeX; x++) {
                    var pos = origin.offset(x, y, z);
                    var blockId = BuiltInRegistries.BLOCK.getKey(turtle.level().getBlockState(pos).getBlock()).toString();
                    blocks.add("{"
                            + "\"x\":" + pos.getX() + ","
                            + "\"y\":" + pos.getY() + ","
                            + "\"z\":" + pos.getZ() + ","
                            + "\"block\":\"" + json(blockId) + "\""
                            + "}");
                }
            }
        }

        return "{"
                + "\"snapshotId\":\"" + json(snapshotId) + "\","
                + "\"worldId\":\"" + json(turtle.worldId()) + "\","
                + "\"turtleId\":\"" + json(turtle.turtleId()) + "\","
                + "\"origin\":{\"x\":" + origin.getX() + ",\"y\":" + origin.getY() + ",\"z\":" + origin.getZ() + "},"
                + "\"size\":{\"x\":" + sizeX + ",\"y\":" + sizeY + ",\"z\":" + sizeZ + "},"
                + "\"capturedAt\":\"" + json(Instant.now().toString()) + "\","
                + "\"turtlePosition\":{\"x\":" + turtle.pos().getX() + ",\"y\":" + turtle.pos().getY() + ",\"z\":" + turtle.pos().getZ() + "},"
                + "\"turtleOrientation\":\"" + json(turtle.facing().getSerializedName()) + "\","
                + "\"blocks\":[" + String.join(",", blocks) + "]"
                + "}";
    }

    private static String json(String value) {
        var builder = new StringBuilder(value.length() + 16);
        for (int index = 0; index < value.length(); index++) {
            char c = value.charAt(index);
            switch (c) {
                case '"' -> builder.append("\\\"");
                case '\\' -> builder.append("\\\\");
                case '\b' -> builder.append("\\b");
                case '\f' -> builder.append("\\f");
                case '\n' -> builder.append("\\n");
                case '\r' -> builder.append("\\r");
                case '\t' -> builder.append("\\t");
                default -> {
                    if (c < 0x20) {
                        builder.append(String.format("\\u%04x", (int) c));
                    } else {
                        builder.append(c);
                    }
                }
            }
        }

        return builder.toString();
    }

    private static String turtleIdentityKey(ServerLevel level, BlockPos pos) {
        return level.dimension().location() + ":" + positionKey(pos);
    }

    private static String sanitizeTurtleName(String name) {
        var cleaned = name == null ? "" : name.trim();
        if (cleaned.length() > 32) {
            cleaned = cleaned.substring(0, 32);
        }

        var builder = new StringBuilder(cleaned.length());
        for (var i = 0; i < cleaned.length(); i++) {
            var c = cleaned.charAt(i);
            if (Character.isLetterOrDigit(c) || c == '_' || c == '-' || c == ' ') {
                builder.append(c);
            }
        }

        return builder.toString().trim();
    }

    private static String rootMessage(Throwable error) {
        var current = error;
        while (current.getCause() != null) {
            current = current.getCause();
        }

        return current.getMessage() == null ? current.getClass().getSimpleName() : current.getMessage();
    }

    private static final class TurtleBinding {
        private final String turtleId;
        private final ServerLevel level;
        private final BlockPos startPos;
        private final String worldId;
        private final String identityKey;
        private final List<BlockPos> path = new ArrayList<>();
        private UUID ownerPlayerId;
        private String displayName;
        private long lastProgressChatAtMs;
        private String lastProgressMessage = "";
        private BlockPos pos;
        private Direction facing;
        private String currentBehaviorId = "";
        private int forwardMoves;
        private int backwardMoves;
        private int returnMoves;
        private int digDownCount;
        private int placeCount;
        private int placeDownCount;
        private int scanNearbyCount;
        private int approachMoves;
        private int rememberedTargetDigCount;
        private int verticalLogDigCount;
        private int tunnelLineSteps;
        private int tunnelLineBlocksRemoved;
        private int branchTunnelCount;
        private int branchMinePatternCount;
        private Integer lastScanRelativeX;
        private Integer lastScanRelativeY;
        private Integer lastScanRelativeZ;
        private String lastScanBlockId = "";
        private BlockPos lastHarvestBasePos;
        private final Set<String> harvestedTreeColumns = new HashSet<>();
        private final Set<String> ignoredScanTargets = new HashSet<>();

        TurtleBinding(String turtleId, ServerLevel level, BlockPos pos, Direction facing, String worldId, UUID ownerPlayerId, String identityKey, String displayName) {
            this.turtleId = turtleId;
            this.level = level;
            this.startPos = pos;
            this.pos = pos;
            this.facing = facing;
            this.worldId = worldId;
            this.ownerPlayerId = ownerPlayerId;
            this.identityKey = identityKey;
            this.displayName = displayName;
            this.path.add(pos);
        }

        String turtleId() {
            return turtleId;
        }

        ServerLevel level() {
            return level;
        }

        BlockPos startPos() {
            return startPos;
        }

        BlockPos pos() {
            return pos;
        }

        Direction facing() {
            return facing;
        }

        String worldId() {
            return worldId;
        }

        String identityKey() {
            return identityKey;
        }

        void refreshFrom(TurtleBlockEntity turtle) {
            pos = turtle.getAccess().getPosition();
            facing = turtle.getAccess().getDirection();
            if (displayName != null && !displayName.isBlank()) {
                TURTLE_NAMES.put(turtleIdentityKey(level, pos), displayName);
            }
        }
    }

    private record TurtleCandidate(BlockPos pos, BlockState state) {
    }

    private record ScannedBlock(BlockPos pos, String blockId, int distance, int qualityPenalty, int verticalPenalty) {
    }

    private record InventorySlots(int occupiedSlots, int freeSlots) {
    }

    private record DigTunnelResult(boolean success, int stepsCompleted, int blocksRemoved, String message, List<String> hazards) {
    }

    private static Optional<Direction> directStepDirection(BlockPos from, BlockPos to) {
        var dx = to.getX() - from.getX();
        var dz = to.getZ() - from.getZ();
        if (Math.abs(dx) >= Math.abs(dz) && dx != 0) {
            return Optional.of(dx > 0 ? Direction.EAST : Direction.WEST);
        }

        if (dz != 0) {
            return Optional.of(dz > 0 ? Direction.SOUTH : Direction.NORTH);
        }

        return Optional.empty();
    }

    private static InteractDirection interactDirection(String value) {
        return switch (value.toLowerCase()) {
            case "up", "above" -> InteractDirection.UP;
            case "down", "below" -> InteractDirection.DOWN;
            default -> InteractDirection.FORWARD;
        };
    }

    private static int findStorageSlot(net.minecraft.world.Container inventory, String storageKind) {
        var preferred = storageKind.toLowerCase().contains("chest") ? "minecraft:chest" : "minecraft:barrel";
        for (var slot = 0; slot < inventory.getContainerSize(); slot++) {
            var stack = inventory.getItem(slot);
            if (stack.isEmpty()) {
                continue;
            }

            var itemId = BuiltInRegistries.ITEM.getKey(stack.getItem()).toString();
            if (preferred.equals(itemId)) {
                return slot;
            }
        }

        var fallback = storageKind.toLowerCase().contains("chest") ? "minecraft:barrel" : "minecraft:chest";
        for (var slot = 0; slot < inventory.getContainerSize(); slot++) {
            var stack = inventory.getItem(slot);
            if (stack.isEmpty()) {
                continue;
            }

            var itemId = BuiltInRegistries.ITEM.getKey(stack.getItem()).toString();
            if (fallback.equals(itemId)) {
                return slot;
            }
        }

        return -1;
    }

    private static boolean isProtectedDepositItem(String itemId) {
        return itemId.contains("pickaxe")
                || itemId.contains("axe")
                || itemId.contains("shovel")
                || itemId.contains("sword")
                || itemId.contains("barrel")
                || itemId.contains("chest");
    }

    private record TurtleCommand(String runId, String commandId, String action, String rawJson) {
        static Optional<TurtleCommand> fromJson(String json) {
            var runId = JsonFields.string(json, "runId");
            var commandId = JsonFields.string(json, "commandId");
            var action = JsonFields.string(json, "action");
            if (runId.isEmpty() || commandId.isEmpty() || action.isEmpty()) {
                return Optional.empty();
            }

            return Optional.of(new TurtleCommand(runId.get(), commandId.get(), action.get(), json));
        }
    }

    private record TurtleReceipt(
            String runId,
            String turtleId,
            String commandId,
            String action,
            boolean success,
            BlockPos position,
            String orientation,
            String observedAt,
            String blockAhead,
            List<String> hazards,
            Map<String, Integer> inventoryDelta,
            String message) {
        String toJson() {
            return "{"
                    + "\"runId\":\"" + json(runId) + "\","
                    + "\"turtleId\":\"" + json(turtleId) + "\","
                    + "\"commandId\":\"" + json(commandId) + "\","
                    + "\"action\":\"" + json(action) + "\","
                    + "\"success\":" + success + ","
                    + "\"position\":{\"x\":" + position.getX() + ",\"y\":" + position.getY() + ",\"z\":" + position.getZ() + "},"
                    + "\"orientation\":\"" + json(orientation) + "\","
                    + "\"observedAt\":\"" + json(observedAt) + "\","
                    + "\"blockAhead\":" + (blockAhead == null ? "null" : "\"" + json(blockAhead) + "\"") + ","
                    + "\"hazards\":[" + hazardsJson() + "],"
                    + "\"inventoryDelta\":{" + inventoryDeltaJson() + "},"
                    + "\"message\":\"" + json(message) + "\""
                    + "}";
        }

        private String hazardsJson() {
            var values = new ArrayList<String>();
            for (String hazard : hazards) {
                values.add("\"" + json(hazard) + "\"");
            }

            return String.join(",", values);
        }

        private String inventoryDeltaJson() {
            var values = new ArrayList<String>();
            for (var entry : inventoryDelta.entrySet()) {
                values.add("\"" + json(entry.getKey()) + "\":" + entry.getValue());
            }

            return String.join(",", values);
        }
    }

    private static final class JsonFields {
        private static final Pattern STRING_FIELD = Pattern.compile("\"%s\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");

        static Optional<String> string(String json, String field) {
            var pattern = Pattern.compile(String.format(STRING_FIELD.pattern(), Pattern.quote(field)));
            var matcher = pattern.matcher(json);
            if (!matcher.find()) {
                return Optional.empty();
            }

            return Optional.of(unescape(matcher.group(1)));
        }

        static Optional<Integer> integer(String json, String field) {
            var pattern = Pattern.compile("\"" + Pattern.quote(field) + "\"\\s*:\\s*(-?\\d+)");
            var matcher = pattern.matcher(json);
            if (!matcher.find()) {
                return Optional.empty();
            }

            try {
                return Optional.of(Integer.parseInt(matcher.group(1)));
            } catch (NumberFormatException exception) {
                return Optional.empty();
            }
        }

        static Optional<Boolean> bool(String json, String field) {
            var pattern = Pattern.compile("\"" + Pattern.quote(field) + "\"\\s*:\\s*(true|false)");
            var matcher = pattern.matcher(json);
            if (!matcher.find()) {
                return Optional.empty();
            }

            return Optional.of(Boolean.parseBoolean(matcher.group(1)));
        }

        private static String unescape(String value) {
            return value
                    .replace("\\\"", "\"")
                    .replace("\\\\", "\\")
                    .replace("\\n", "\n")
                    .replace("\\r", "\r")
                    .replace("\\t", "\t");
        }
    }
}
