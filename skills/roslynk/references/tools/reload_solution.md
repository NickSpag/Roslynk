---
tool: reload_solution
---
Re-evaluates an open solution from disk using MSBuild. Additional reload reasons: missed watcher
events (network/WSL/container filesystems), global.json changes or retrying a Faulted load.

Returns immediately: solutionId, status, projects. Previous snapshot serves reads with
status=Building until replacement is ready. Files are unchanged; failed reloads return Faulted.

## Parameters

- solutionId: Solution handle.
