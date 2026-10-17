---
tool: change_signature
---
Adds an optional parameter to one ordinary method/local function.
Method groups, nameof and cref references remain unchanged. Supports Razor @code declarations
and markup calls.

Errors: NotSupported for virtual/override/abstract, interface members/implementations, partial
methods, params methods, constructors or generated-only declarations (names the generator);
Invalid for invalid parameter name/type/default; Ambiguous for multiple overloads (candidates).

Additional headers: resolvedMethod (pre-edit overload with parameter types), updatedCallSites.

## Parameters

- solutionId: Solution handle.
- methodId: Method name.
- parameterType: New parameter type.
- parameterName: New parameter's C# identifier.
- defaultValue: Required default expression, e.g. default, null, 0.
- callSiteArgument: Named argument expression for all call sites; omit to use the default.
- checkOnly: Preview only.
