---
tool: apply_code_fix
---
Fixes the diagnostic containing the supplied position, using its id and location from get_diagnostics.

Multiple fixes return Conflict with candidate=<actionId>,Fix,<diagnosticId> <title>:
choose by title and pass actionId to apply_code_action; do not repeat apply_code_fix.

Errors: NotFound for an unloaded document or no diagnostic at the position; Invalid for
line/column below 1; NotSupported for no registered fix; Conflict for no changes (without candidates).
CS8019/IDE0005 fixes remove the @using on that line.

Additional header: action.

## Parameters

- solutionId: Solution handle.
- documentPath: Source file path.
- diagnosticId: Diagnostic id, e.g. CS0219/IDE0005.
- line: Diagnostic line from get_diagnostics.
- column: Diagnostic column from get_diagnostics.
- checkOnly: Preview only.
