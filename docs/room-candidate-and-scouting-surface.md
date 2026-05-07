# Room Candidate And Scouting Surface

Room planning should be route-attached, bounded, and evidence-first. The turtle should not carve a large room because an LLM imagined open space. It should propose a room candidate, validate it with bounded scans/rays, then execute a host-owned carve behavior only after the candidate is explicit.

## Surface Layers

```text
blueprint/template
  desired room type, dimensions, purpose, tags

room candidate
  proposed world-space bounding box, entry point, route attachment, direction

validation evidence
  bounded ray/bounds samples, obstruction counts, hazards, overlap checks

room record
  accepted room id, tags, bounds, entry points, connected routes, validation state
```

Agentica should operate mostly on room candidates and room records. The host owns scans, bounds checks, carving, and receipts.

## Candidate Shape

```json
{
  "roomId": "room-candidate-001",
  "blueprintId": "turtlequest.blueprint.dire_room",
  "purpose": "storage_room",
  "anchor": { "x": 0, "y": 64, "z": 0 },
  "direction": "east",
  "entryPoint": { "x": 1, "y": 64, "z": 0 },
  "bounds": {
    "min": { "x": 1, "y": 64, "z": -4 },
    "max": { "x": 9, "y": 72, "z": 4 }
  },
  "size": { "width": 9, "length": 9, "height": 9 },
  "attachedRouteId": "route-main-001",
  "status": "proposed_only"
}
```

This is not execution. It is a planning artifact.

## Candidate Generation

The first candidate generator should be deterministic:

```text
propose_room_box(blueprintId, anchor=current, direction=facing|left|right|back)
```

Inputs:

```text
blueprintId
anchorPosition
direction
entryWidth
routeId
chunkAlignmentPreference
```

Receipt/artifact:

```text
roomCandidateId
blueprintId
purpose
entryPoint
bounds
size
attachedRouteId
alignmentNotes
status=proposed_only
```

## Bounds And Ray Validation

Before carving, the turtle can validate a room candidate with bounded sampling:

```text
validate_room_box(candidateId, sampleMode=faces|shell|full|ray_grid)
```

Evidence:

```text
sampledCells
airCells
solidCells
fluidCells
unmineableCells
hazardCells
overlapWithKnownRoutes
overlapWithKnownRooms
estimatedDigCount
inventoryPressureEstimate
```

This is where bounds/ray tests belong. They let the turtle act like a scout without mutating the world.

## Scout Mapping

Eventually the same scan surface can map existing player bases or caves:

```text
scout_room_candidate(radius, method=ray_grid)
infer_room_bounds()
record_room(status=observed_existing)
connect_route()
```

The initial result should be conservative:

```text
observed volume
possible room bounds
entrances
open air ratio
hazards
confidence
```

The turtle should not name or claim a room as authoritative until a later validator confirms the bounds.

## Routing Link

Rooms should attach to known routes, not float in memory.

Required fields:

```text
attachedRouteId
entryWaypointId
entryPoint
entryFacing
parentRoomId
connectedRouteIds
```

This lets future turtles use the same mining roads, stairs, and room entrances the player can use.

## First Definition Of Done

Room candidate v0 is done when Agentica can request:

```text
propose a 9x9x9 storage room to the right of this tunnel
```

and TurtleQuest returns:

```text
room candidate id
bounding box
entry point
attached route id if known
status proposed_only
no block mutation
```

Only after that should we build `validate_room_box`, then staged carving.
