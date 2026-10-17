---
tool: get_expression_info
---
Returns compiler facts at a source position. Member/method names report the whole access/invocation: return type and
bound overload.

Header-only fields:
- expr: expression text, truncated at 200 characters with '...'; exprKind, path, loc.
- type, convertedType: '?' marks nullable annotations.
- nullability: annotation/flowState (NotAnnotated/NotNull, Annotated/MaybeNull;
  None/None when analysis is disabled).
- conversion: e.g. 'implicit numeric', 'implicit user-defined <method>', 'identity'.
- constant: C# literal or 'null'.
- symbol, symbolKind: bound symbol; type/member names work in name-based tools;
  locals/parameters/range variables use bare names.
- instantiation: constructed generics/reduced extension methods.
- origin: source|metadata; source adds symbolPath/symbolLoc, metadata adds assembly.
- doc: XML summary when available.

Unavailable facts use '{Unavailable}': type for method groups/namespaces, constant for
non-constants, symbol for unbound expressions. Binding candidates add candidateReason
and pipe-delimited candidates.

Declared names (locals, parameters, fields, properties, methods, types, foreach/pattern variables)
add declaration=Y: expr=name, exprKind=declaration syntax, type=declared type (method return type),
nullability=declared annotation/initializer flow (None without initializer).

Whitespace/comments/keywords/punctuation or files outside the solution return NotFound.

## Parameters

- solutionId: Solution handle.
- filePath: Source file path.
- line: Expression/declaration line.
- column: Expression/declaration column.
