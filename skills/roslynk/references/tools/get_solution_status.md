---
tool: get_solution_status
---
Lists loaded solutions: <solutionId>,<status>,<loaded>/<total>.
loaded tracks Building progress; total='?' until known after initial loading.

Ready solutions add indented loadDiagnostic=<message> for analyzer/generator load failures,
project evaluation failures or missing SDKs, which may explain compiler errors.
Other states omit messages. Up to 20 per solution, then loadDiagnosticsTruncated=<n>.

## Parameters

- allLoadDiagnostics: Include all load diagnostics.
