---
tool: get_callers
---
SupportsPaging=Y

Finds callers, including implicit calls: foreach/await enumeration and awaiter members
(GetEnumerator/MoveNext/Current/Dispose, GetAwaiter/IsCompleted/GetResult, async equivalents),
using/await using Dispose/DisposeAsync, deconstruction/positional patterns, collection Add,
operators/conversions (+=/++/implicit/conditional true/false), query methods, interpolated-string
handlers, implicit base(), and Length/Count for ^/.. . See get_callees for its narrower coverage.

Body: project > folder > file > namespace > containing type; leaves name callers and declaration locations.

## Parameters

- solutionId: Solution handle.
- methodName: Method name.
- startItem: First item (1-based). (default: 1)
- maxSize: Page character limit. (default: 0)
