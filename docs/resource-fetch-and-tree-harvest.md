# Resource Fetch And Tree Harvest

Resource fetch is the next high-value behavior lane because it joins perception, pathing, inventory, storage, and repeatable work loops.

The first target is tree harvesting because logs are visible, above-ground, locally scannable, and useful for later building/crafting missions.

## Behavior Ladder

```text
turtlequest.find_tree
  scanNearby(radius, tag=minecraft:logs)
  report nearest candidate evidence

turtlequest.navigate_to_resource
  choose candidate from scan evidence
  face/move toward target with bounded detours
  stop adjacent to target or emit blocked receipt

turtlequest.harvest_tree
  find_tree
  navigate_to_resource
  fell vertical log column
  collect reachable drops
  report inventory delta

turtlequest.fetch_resource
  choose known resource behavior
  gather until threshold or budget
  return/deposit/report
```

## Current Slice

`find_tree` is intentionally evidence-only. It should not move, dig, or harvest. The receipt gives the planner a bounded local world fragment:

```text
scanNearby radius=12; query=minecraft:logs; matches=4; nearest=minecraft:oak_log@3,0,-5#d8
```

This lets us test whether the bridge, Agentica planner host, Java executor, and trace files agree on reality before we add movement toward a target.

`harvest_tree` now has a full-trunk slice:

```text
scanNearby(logs)
moveTowardRelative(source=lastScanNearest, stopAdjacent=true, budget=12)
digRememberedTarget(source=lastScanNearest, expectedTag=minecraft:logs)
fellRememberedTree(maxHeight=12, expectedTag=minecraft:logs)
getInventory()
returnHome(mode=breadcrumbs)
emitStatus(stage=full_tree_felled_returned)
completeObjective(stage=full_tree_felled_returned)
```

The executor remembers the nearest scan match inside the turtle binding. That keeps the bridge plan static while still letting runtime receipts decide the actual target.

For repeated harvests, Agentica can emit the same scan/approach/fell/return cycle multiple times. The Java executor keeps run-local harvest memory so later scans avoid stale targets:

```text
harvestedTreeColumns: x,z columns successfully felled
ignoredScanTargets: exact log positions that changed or failed as non-tree targets
scanNearby staleExcluded=N
scanNearby elevatedRejected=N
scanNearby baseCandidates=N
```

After `fellRememberedTree` succeeds, the harvested tree column is excluded from later log scans. If a remembered target changed before digging or produced no vertical logs, that exact target is also ignored. This is execution-side state, not planner authority; Agentica still chooses how many harvest cycles to request.

`fellRememberedTree` must finish back at the trunk base height before it reports success. The turtle may climb while cutting vertical logs, but it descends back to the base as part of the same command so later `scanNearby`, movement, or `returnHome` steps do not start in the canopy.

During `turtlequest.harvest_tree`, `scanNearby` also uses a target-quality filter:

```text
reject log targets above or below the turtle's current Y level
prefer likely tree bases where below is not log-like and above is log-like
annotate sampled candidates with #base when they look like trunk bases
```

This matches the current movement primitive: `moveTowardRelative` and `digRememberedTarget` can safely handle same-level adjacent targets, while vertical terrain pathing is a later slice.

## Recovery Slice

`turtlequest.recover_turtle` is a small host-owned safety behavior for live test cleanup. It is not a mission behavior; it exists so a failed run can be recovered without breaking and replacing the turtle.

```text
startBehavior(turtlequest.recover_turtle)
recoverToGround(maxDown=32, digSoftBelow=true)
returnHome(mode=breadcrumbs) optional
emitStatus(stage=recovered_to_ground)
completeObjective(stage=recovered_to_ground)
```

`recoverToGround` descends until the block below is solid support. If the turtle is sitting in tree canopy, it may clear soft leaf-like blocks below before moving down. It must not dig logs, dirt, stone, ores, or built structures as part of recovery.

For a manually stuck turtle after restarting the game, use:

```text
/tq ask nearest recover the stuck turtle to ground
```

If the recovery command is issued inside the same run that still has breadcrumbs, Agentica may also include `returnHome`. If the game was restarted and the current elevated position becomes the new binding start, descent is still useful but `returnHome` cannot infer the old home position. For that reason, recovery plans mark `returnHome` as optional; the executor skips it successfully when no breadcrumb path exists.

Runtime replans for `turtlequest.harvest_tree` are now recovery-aware at the planner-guidance layer. If movement, felling, descent, or return fails, the Agentica planner is instructed to prefer `turtlequest.behavior.recover_turtle` before attempting more harvest work. The current conservative continuation is:

```text
recoverToGround(maxDown=32, digSoftBelow=true)
returnHome(mode=breadcrumbs, optional=true)
emitStatus(stage=harvest_recovered_or_stopped)
completeObjective()
```

This deliberately stops with evidence instead of continuing to scan/fell from a bad elevation. Resuming the remaining tree count after recovery needs explicit progress accounting and is a later slice.

## Pathfinding V0

The first pathing slice should be small:

```text
face(direction)
move_toward_relative(dx, dz, budget)
move_toward_relative(source=lastScanNearest, stopAdjacent=true, budget)
try one-block sidestep when blocked
stop and replan when detours exceed budget
```

It should not attempt global pathfinding. Minecraft remains authoritative; failed movement receipts become the replan boundary. The first implementation is a bounded direct approach; sidestep detours come next.

## Treecapitator Shape

Tree harvesting should become a deterministic behavior, not raw LLM micromanagement:

```text
1. scanNearby logs
2. pick nearest trunk candidate
3. move adjacent to trunk
4. repeat:
   inspect target log
   dig target log
   moveUp or target next log position
5. stop when no log continues within bounded height
6. optionally return to base/deposit
7. emit inventory and world-diff evidence
```

Definition of done for the first harvest:

```text
nearest log found
turtle reaches an adjacent cell
one remembered log dig receipt succeeds
fellRememberedTree cuts bounded vertical logs
inventory delta includes log-like items or explicit no-drop evidence
returnHome reaches the starting position
completion report contains scan, path, dig, felling, inventory, return, and final position receipts
```
