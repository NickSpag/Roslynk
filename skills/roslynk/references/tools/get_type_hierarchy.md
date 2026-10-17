---
tool: get_type_hierarchy
---
SupportsPaging=Y

Returns base types (including metadata), implemented interfaces and known derived types.
Header: resolvedType. Body sections: base, interfaces, derived (empty sections omitted).
Entries: <typeKind>,<fully-qualified name>; typeKind=class|struct|interface|enum|delegate.

## Parameters

- solutionId: Solution handle.
- typeName: Type name.
- startItem: First item (1-based). (default: 1)
- maxSize: Page character limit. (default: 0)
