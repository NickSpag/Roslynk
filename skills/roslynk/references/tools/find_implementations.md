---
tool: find_implementations
---
SupportsPaging=Y

Finds implementations/overrides of interfaces, interface members or abstract members.
Returns resolvedSymbol (including method/indexer parameter types).
Body: project > folder > file > namespace; leaves: <typeKind>,<typeName>,<loc>.

## Parameters

- solutionId: Solution handle.
- symbolName: Interface/abstract symbol name.
- startItem: First item (1-based).
- maxSize: Page character limit.
