---
tool: find_dead_conditionals
---
SupportsPaging=Y

Finds #if/#elif/#else branches absent from every checked configuration: each project's symbols
and those symbols without DEBUG, across loaded frameworks. Results are possible dead code;
missing workloads or CI-only defines can cause false positives.

Header: deadConditionals=<n>. Body grouped by file; leaves: <line:col>,<directive>,<condition>.
directive=if|elif|else; condition is the raw expression or '(else)'.
Use find_dead_code for unreferenced symbols.

## Parameters

- solutionId: Solution handle.
- startItem: First item (1-based).
- maxSize: Page character limit.
