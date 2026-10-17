---
tool: get_symbol_body
---
SupportsPaging=Y

Returns verbatim declarations of methods, constructors, properties, indexers, events, fields,
local functions or types. Razor returns generated C#.
Single declaration: project/path/loc headers, then source.
Partial types/methods: parts=<n>, then blocks headed part=<n>,project=...,path=...,loc=...,lines=<count>.
Includes generated implementations (GeneratedRegex, LoggerMessage, LibraryImport).

Ambiguous names/overloads return candidates. Namespaces/metadata-only symbols: NotSupported.
Metadata spellings of source types (Ns.Box`1, N.Outer+Inner) return source.

Paging: startItem counts returned lines (including requested leading trivia), not file lines.
Past-end startItem returns Invalid with count.
Paging requires one part; multiple parts without part= returns Invalid.
Oversized parts omit text with omitted=Y, truncated=Y, omittedParts=<k>; fetch with part=<n>, then page.

## Parameters

- solutionId: Solution handle.
- symbolName: Symbol name.
- includeLeadingTrivia: Include leading comments, XML docs and directives. (default: false)
- startItem: First returned-text line (1-based). (default: 1)
- maxSize: Page character limit. (default: 0)
- part: Declaration part (1-based); 0 returns all, omitting oversized parts. (default: 0)
