---
tool: get_callees
---
SupportsPaging=Y

Lists methods, constructors, property/event accessors and operators called by a member.
Property reads/writes name their accessors (compound/increment reports both); e += h names
the add accessor. A property includes its accessors; a field includes its initializer;
constructors include initializers they run. Includes user-defined operators/conversions,
using's Dispose, delegate method groups, nested lambdas and local functions. Abstract/interface
declarations and auto-properties without code call nothing.

Implicit foreach (GetEnumerator, MoveNext, Current) and await (GetAwaiter, IsCompleted, GetResult)
calls are excluded; get_callers includes them. Explicit calls remain included.

Solution callees nest project > folder > file > namespace > type; metadata callees appear
under <external:AssemblyName> with empty loc. Leaves: <memberKind>,<memberName>,<loc>, plus
parameterTypes only for overloads declared in the callee's type (parameterless overload: '()'). Constructed generics collapse to their definitions; extension
methods include their 'this' parameter.

## Parameters

- solutionId: Solution handle.
- memberName: Member name.
- excludeExternal: Exclude metadata callees (BCL/NuGet). (default: false)
- startItem: First item (1-based). (default: 1)
- maxSize: Page character limit. (default: 0)
