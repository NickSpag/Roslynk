---
tool: find_definition
---
Resolves the symbol at a source position. Returns fullName (including method/indexer parameter
types) and kind; source symbols add project, path and loc, metadata symbols add assembly.
No binding in any projection returns NotFound.

## Parameters

- solutionId: Solution handle.
- filePath: Usage's .cs file path.
- line: Usage line.
- column: Usage column.
