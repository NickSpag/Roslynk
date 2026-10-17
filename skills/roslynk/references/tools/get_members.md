---
tool: get_members
---
SupportsPaging=Y

Lists methods/properties/fields/events/nested types grouped by declaring file ('<metadata>' for
referenced assemblies). Leaves: <memberKind>,<name>,<loc>, plus pipe-delimited parameter types
for methods with parameters. Metadata loc is empty. Local functions nest under their member
in declaration order, one level per nesting.

Includes all accessibilities. Filter with nameFilter/include*; use get_symbol_body for bodies.

## Parameters

- solutionId: Solution handle.
- typeName: Type name.
- includeInherited: Include inherited members. (default: false)
- nameFilter: Case-insensitive name filter: trailing '*' means prefix; otherwise substring. Null matches all. (default: null)
- includeMethods: Include methods. (default: true)
- includeFields: Include fields. (default: true)
- includeProperties: Include properties. (default: true)
- includeEvents: Include events. (default: true)
- includeNestedTypes: Include nested types. (default: true)
- startItem: First item (1-based). (default: 1)
- maxSize: Page character limit. (default: 0)
