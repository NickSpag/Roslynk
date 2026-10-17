---
tool: rename_symbol
---
Renames a symbol and references across partial classes, code-behind and loaded frameworks under
shared conditional coverage. Strings/comments are unchanged. Razor supports @code, markup
expressions and component attributes.

Generated-only targets return NotSupported naming the generator; edit its input/generator.
Partial members/types with handwritten declarations are renamable; generators follow their names.

Additional header: resolvedSymbol (pre-rename name).
Errors: Invalid for invalid identifiers; Ambiguous with match candidates or fuzzy suggestions
when no match; Stale naming the target changed on disk/in the model since computation.
Intervening edits to other files are preserved.

## Parameters

- solutionId: Solution handle.
- symbolName: Symbol name.
- newName: New C# identifier.
- checkOnly: Preview only. (default: false)
