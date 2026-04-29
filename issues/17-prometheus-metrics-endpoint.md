## Parent

PRD.md

## Status

**READY**

## What to build

Add a Prometheus metrics endpoint at `/metrics` that exposes loop run counts, node execution times, LLM API latency and token usage, and system health metrics. The endpoint outputs in Prometheus text format and is excluded from auth middleware.

Metrics include:

- `ild_loop_runs_total` (by status: completed, failed, cancelled)
- `ild_node_execution_duration_seconds` histogram (by node type)
- `ild_llm_api_latency_seconds` histogram
- `ild_llm_tokens_total` (by type: prompt, completion)
- `ild_workitems_total` (by status)
- `ild_db_connection_healthy` gauge
- `ild_disk_space_bytes` gauge

## Acceptance criteria

- [ ] `/metrics` endpoint returns Prometheus text format
- [ ] Endpoint is excluded from auth middleware (accessible without token)
- [ ] `ild_loop_runs_total` counter increments on LoopRun completion/failure/cancellation
- [ ] `ild_node_execution_duration_seconds` histogram records per-node execution time
- [ ] `ild_llm_api_latency_seconds` histogram records LLM call latency
- [ ] `ild_llm_tokens_total` counter tracks prompt and completion tokens
- [ ] `ild_workitems_total` gauge reflects current WorkItem count by status
- [ ] Health metrics: DB connectivity gauge and disk space gauge
- [ ] Backend tests cover: metrics endpoint returns valid Prometheus format, counters increment correctly, gauges reflect state
- [ ] `vp check` and `vp test` pass

## Blocked by

None - can start immediately
