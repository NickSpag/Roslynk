---
tool: search_symbols
---
SupportsPaging=Y

Searches source symbol names, including generated declarations
and local functions nested under their declaring member/outer local functions.
Body: project > folder > file > namespace > type > member. Members nest under parent types;
type locations appear only when the type itself matches.

## Parameters

- solutionId: Solution handle.
- query: Case-insensitive name substring.
- maxResults: Result cap. (default: null)
- startItem: First item (1-based). (default: 1)
- maxSize: Page character limit. (default: 0)
