# Receipt Schema

Every turtle command returns a receipt. Receipts are the only source of execution truth for Agentica.

Required fields:

- `runId`
- `turtleId`
- `commandId`
- `action`
- `success`
- `position`
- `orientation`
- `observedAt`

Optional fields:

- `blockAhead`
- `hazards`
- `inventoryDelta`
- `message`

Behavior runs add one layer above primitive receipts:

- `behaviorRunId`
- `behaviorId`
- `arguments`
- `commandBudget`

The first behavior id is `turtlequest.dig_line_return`. It expands into primitive command receipts, but completion is still evidence-gated by the host. A run is complete only when `completion.artifactKind` is `turtlequest.objective_completed` and `completion.success` is `true`.
