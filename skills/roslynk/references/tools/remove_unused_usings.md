---
tool: remove_unused_usings
---
Removes unnecessary using directives (CS8019), solution-wide or in documentPath, including
Razor @using lines. Additional header: removedCount.
No changes: applied=N, removedCount=0; safe to rerun.
NotFound: unloaded source file.

## Parameters

- solutionId: Solution handle.
- documentPath: Source file to clean; omit for the whole solution. (default: null)
- checkOnly: Preview only. (default: false)
