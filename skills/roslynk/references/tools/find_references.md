---
tool: find_references
---
SupportsPaging=Y

Finds semantic references, including partial classes/code-behind.
Body: project > folder > file > namespace > type > member. Shared declarations appear once;
type locations appear only when the declaration references the target; collapsed references
use pipe-delimited loc lists.

Combine with get_callers/get_callees/find_implementations/get_type_hierarchy via multi_query for
usage/impact analysis.

## Parameters

- solutionId: Solution handle.
- symbolName: Symbol name.
- maxResults: Reference-location cap.
- startItem: First item (1-based).
- maxSize: Page character limit.
