---
tool: open_solution
---
Opens a .sln/.slnx asynchronously; repeated opens reuse the instance.
Headers: solutionId, status, projects, loadDiagnostics. Body: <projectPath>,<documentCount> per project.
While loading: status=Building, empty body. Missing solution: NotFound; failed load: Faulted.

## Parameters

- solutionPath: Absolute path to the .sln or .slnx file to open.
