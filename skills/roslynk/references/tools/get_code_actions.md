---
tool: get_code_actions
---
Lists fixes/refactorings at a source position or selection. Fixes use compiler/analyzer
diagnostics at the span; refactorings use the span itself.

Returns up to 50 <actionId>,<kind>,<diagnosticId> <title> lines; diagnosticId='-' for refactorings.
Unknown documentPath returns NotFound.
Razor offers compiler fixes/refactorings; positions outside C# return NotSupported.

## Parameters

- solutionId: Solution handle.
- documentPath: Source file path.
- line: Position line.
- column: Position column.
- endLine: Selection end line.
- endColumn: Selection end column.
