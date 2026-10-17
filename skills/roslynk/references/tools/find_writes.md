---
tool: find_writes
---
SupportsPaging=Y

Finds writes (assign/compound/increment/ref/out/init) using the shared access contract.
Body follows find_references with one location per leaf.

## Parameters

- solutionId: Solution handle.
- symbolName: OutlineDescriptions.AccessSymbolName
- maxResults: Location cap. (default: null)
- startItem: First item (1-based). (default: 1)
- maxSize: Page character limit. (default: 0)
