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
- includeInherited: Include inherited members.
- nameFilter: Case-insensitive name filter: trailing '*' means prefix; otherwise substring. Null matches all.
- includeMethods: Include methods.
- includeFields: Include fields.
- includeProperties: Include properties.
- includeEvents: Include events.
- includeNestedTypes: Include nested types.
- startItem: First item (1-based).
- maxSize: Page character limit.
