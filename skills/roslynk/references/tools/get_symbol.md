---
tool: get_symbol
---
Returns a symbol's declaration without implementation. Source results: project, path and loc
headers, then the verbatim declaration before its opening brace or '=>'.
Without source: source=metadata, kind, signature and assembly headers.

## Parameters

- solutionId: Solution handle.
- symbolName: Symbol name.
