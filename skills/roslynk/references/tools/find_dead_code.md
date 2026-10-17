---
tool: find_dead_code
---
SupportsPaging=Y

Finds unreferenced types/methods/properties/fields/events, conservatively excluding interface
implementations, virtual/override chains, tests, generated and DI/reflection-activated members.
Excludes public APIs unless includePublic. find_dead_conditionals finds unreachable #if branches.

Body: project > folder > file > namespace > type; leaves:
<memberKind>,<memberName>,<loc>,<confidence>,<reason>, confidence=High|Medium.
Dead types have their own leaf/location. loc spans the declaration for apply_patch removal.
Unused overloads have separate locations, without parameter lists: get_symbol_body offers
candidates. The host decides removals. Scan is per-symbol; narrow large solutions with scope.

maxResults caps the scan: count is candidates found; truncated=Y may mean the cap stopped scanning.
Omit maxResults for a complete scan.

## Parameters

- solutionId: Solution handle.
- scope: Name prefix, e.g. MyApp.Services; omit for the whole solution. (default: null)
- includePublic: Include unreferenced public/protected members (the API surface). (default: false)
- maxResults: Scan cap; omit for a complete scan. (default: null)
- startItem: First item (1-based). (default: 1)
- maxSize: Page character limit. (default: 0)
