---
tool: find_writes
---
SupportsPaging=Y

Finds writes (assign/compound/increment/ref/out/init) using the shared access contract.
Body follows find_references with one location per leaf.

## Parameters

- solutionId: Solution handle.
- symbolName: OutlineDescriptions.AccessSymbolName
- maxResults: Location cap.
- startItem: First item (1-based).
- maxSize: Page character limit.
