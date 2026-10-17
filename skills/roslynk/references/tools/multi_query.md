---
tool: multi_query
---
Runs independent read-only operations against one snapshot, returning results in request order.
Operations cannot reference each other's output. Batch only queries needed for the question.

operations.tool uses the schema enum; arguments use that tool's single-call names/defaults.
Unknown/misspelled arguments are rejected.

All operations run. budget=<n> is the per-operation allowance; whole results are selected
smallest-first until the response budget fills. Slots are complete or absent. Missing results
report retry=<index>:<size> in request order; size is result cost/budget, rounded up to 0.1.
- size >= 1.0: resend alone.
- Sizes totaling <= 1.0: fit one continuation batch.
- Sizes below 0.1: about ten per continuation batch is safe.

Resend retry operations with original arguments from your request copy; frames contain only
index/tool. Pass snapshot=<id> as expectSnapshot; changed generations return Stale with both ids:
rerun the whole batch. Ratios are snapshot-specific; reruns remeasure/reselect.
Only slot-body nextStartItem drives paging; retry means an omitted result, not a page.

## Parameters

- solutionId: Solution handle.
- operations: Read-only tools and arguments, in request order.
- expectSnapshot: Previous snapshot id; mismatch returns Stale before execution. (default: null)
