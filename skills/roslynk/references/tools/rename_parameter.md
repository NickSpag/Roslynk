---
tool: rename_parameter
---
Renames one method/local function/constructor/indexer overload's parameter, updating declaration,
body uses, named call arguments and <paramref> docs. Strings/comments/unrelated text are unchanged.

Cascades through overrides, interfaces/implementations and partial-method parts when a source
parameter at the same position has the old name. Other names, metadata and generated-only
related declarations remain unchanged, listed in unchangedRelated.

Additional headers: resolvedMethod (pre-rename member), parameter (old name), renamedMembers
(declaration count including target), unchangedRelated (semicolon-separated; omitted if empty).

Errors:
- Invalid: invalid identifier or newName=parameterName.
- Ambiguous methodId: overload candidates; no match gives fuzzy suggestions.
- NotFound: absent parameterName; lists parameters.
- Conflict: newName clashes with a parameter, local, local function, lambda/query variable or
  type parameter in a renamed declaration.
- NotSupported: unsupported target kind, positional record parameter, metadata-only or
  generated-only target (names generator).

## Parameters

- solutionId: Solution handle.
- methodId: Declaring method/local function/constructor/indexer name.
- parameterName: Current parameter name.
- newName: New C# identifier.
- checkOnly: Preview only. (default: false)
