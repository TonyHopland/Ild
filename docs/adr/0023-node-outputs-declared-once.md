# ADR-0023: A node declares each of its outputs once, in config

"This node has an output called X" used to be stored in several places, differently per node type: a Human or PR node's `customEdges`, an AI node's `matchRules[].edgeName`, a Condition's `cases[].edgeName` and `defaultEdge`, a hardcoded list of reserved PR names in both the backend and the frontend, and the edge rows themselves, with success and failure never listed at all. The validator kept these copies in step with "must match both ways" rules, a mismatch rejected the whole save, and there was nowhere to hang a setting on an output. We decided that every node declares every output once, as an object in `config.outputs` (`{ "name": ... }` plus any settings), and everything else — rules, cases, the default, edge rows — only refers to an output by name. Success and failure are ordinary outputs, and the fixed outputs of each node type (success/failure, and a PR node's reserved outputs) come from one backend definition that saving fills in, so they cannot be removed; the editor reads them from the server rather than keeping its own list.

The change is a storage and model rework only: the engine routes over edge rows exactly as before, so no run changes route. Stored loops are converted by an idempotent startup migrator over every template version, old versions included, and documents by an upgrader from `ild-loop-template/v1` to `v2` applied wherever a document comes in (file import, the chat's loop document, the agent's loop edits, the seeder). Both run the same per-node conversion, so a document and a stored loop cannot be upgraded differently.

## Considered options

**Keep outputs implicit and add a per-output settings map beside them.** Rejected: it adds a fourth place that names outputs, and the both-ways rules would grow to cover it.

**Change the edge document shape to reference outputs by id.** Rejected: edges keep `{ edgeType, name }`, which already identifies an output uniquely within its node, and changing it would widen the format change for no gain.

**Auto-declare every name a rule or case mentions at save.** Rejected for the server: it would make the "references an undeclared output" check dead and hide typos. The editor still declares a name typed into a rule or case when its settings are saved, so authoring a rule stays one step.
