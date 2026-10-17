---
tool: extract_method
---
Extracts contiguous C# statements or one expression into a method/local function, replacing the
selection with a call. Roslyn data-flow determines parameters, ref/out, return value, static/async
and call-site code. Surrounding whitespace is ignored.

Additional headers: method (final name), symbolName (usable in name-based tools, e.g.
N.T.M(int).NewMethod(string) for a local function), kind (Method|LocalFunction), signature
(declaration header), call (statement containing the call).

Errors:
- NotSupported: extraction unavailable, behavior-change warnings, new compile errors (listed),
  or generated .g.cs targets (including Razor output).
- Conflict: methodName would bind a call to another member.
- NotFound: unloaded source file.
- Invalid: empty/out-of-range selection or invalid methodName.

Razor selections outside C# return NotSupported; inserting a method outside @code may be unmappable.

## Parameters

- solutionId: Solution handle.
- documentPath: Source file path.
- startLine: Selection start line.
- startColumn: Selection start column.
- endLine: Selection end line.
- endColumn: Selection end column.
- methodName: Extracted method name; omit to keep Roslyn's chosen name. (default: null)
- asLocalFunction: Extract as a local function. (default: false)
- checkOnly: Preview code and changed paths without writing. (default: false)
