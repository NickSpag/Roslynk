---
name: roslynk
description: Use Roslynk for semantic inspection and edits of *.cs, *.razor and *.cshtml solution files.
---

Roslynk provides semantic inspection, diagnostics, navigation, references, call graphs,
dead-code analysis and edits for .cs/.razor/.cshtml files. Prefer it over grep, source reads,
hand edits and dotnet build for semantic questions; use dotnet test for runtime behavior.
## Scope

Only solution-compiled files are visible. For missing files, use standard tools and re-check
later for project inclusion. Use standard file tools for diffs and line-range reads, including
locations from PR diffs, and for non-code files.

## Lifecycle

open_solution returns solutionId. On error=Indexing, retry shortly or poll get_solution_status.
The watcher folds edits, including yours. Never initiate reload_solution; suggest it after
branch switches, restore, SDK/build-property changes.

## Output protocol

Text results: key=value headers, blank line, body (outlines tab-indented). Without a blank line,
all lines are headers. status appears only when not Ready; booleans are Y/N.
Leaves: kind,name,loc[,extra]; multiple field values use pipes; comma-containing values use
single quotes. Output paths are solution-relative; projects omit .csproj.
Generated declarations use virtual paths marked generated=Y/generator=<type>; read via
get_symbol_body, not disk.

## Parameters

Paths accept absolute or solution-relative forms. documentPath accepts .cs/.razor/.cshtml
unless restricted by the tool. Source lines/columns are 1-based; selections start inclusive, end exclusive.
checkOnly=true previews without writing, returning would-change paths (apply_patch: headers only).
Omitted optional arguments use defaults.

## Name grammar

Use returned fully-qualified names verbatim (resolvedSymbol/fullName/candidates), never short
outline leaves. Methods/indexers accept overload parameter types: Ns.T.M(int, string),
Ns.T.this[int]; parameter names/defaults/nullability are ignored. Bare generic names resolve
unique arities; multiple arities return Ambiguous; same-named non-generics win.
Local functions nest under declaring members: Ns.T.M.local.

## Conditional coverage

Loaded frameworks plus variants toggling one uniformly defined/undefined C# symbol.
Branches requiring simultaneous toggles may be uncovered.

## Failures and freshness

Failures are header-only: error=<code>, errorMessage=..., optional candidates.
Stale: target changed since reading; recompute/retry. Re-query after relevant writes/user saves.

## Paging

Only SupportsPaging=Y tools page. startItem is 1-based; continue with startItem=<nextStartItem>,
keeping other arguments unchanged. Pages repeat enclosing containers. maxSize limits page characters;
0 uses the response budget. Omit maxResults for all results; otherwise it caps the result set,
count describes that set, and paging cannot recover excluded results.
Exceptions: find_dead_code caps the scan; get_diagnostics keeps full header counts.
partialItem=<n> means an oversized item is incomplete and its remainder unretrievable:
narrow the query; never claim complete coverage. Unpaged oversized results stop at a whole line
with outputTruncated=Y, fullOutputChars=<n>.

## Reads and writes

find_reads/find_writes target fields/properties or Member:parameter; Member names a method,
constructor, indexer or local function, optionally with overload parameter types.
Other target kinds return NotSupported.
Parameter resolvedSymbol: Member:parameter. Leaves: <memberKind>,<memberName>,
<loc>,<accessKind>. accessKind: read, assign (=), compound (+=, ??=, ...), increment
(++/--), ref (ref arguments, ref x, &x), out, init (declarations, object/with initializers,
attribute named arguments, constructor assignments to its own fields/properties).
compound/increment/ref appear in both tools. Counts overlap; do not sum.
nameof, documentation crefs and named call arguments are excluded.

## Batching

Batch two or more independent, already-nameable facts with multi_query; run dependent discovery
first, then batch follow-ups. Its description covers operations/continuations.

## Editing and diagnostics

Preview broad edits; validation failures write nothing. Semantic write results: applied,
tool-specific headers, blank line, changed paths; applied=N for previews.
Individual replacements are atomic; saving failures trigger attempted rollback, which may fail.
Semantic edits validate their original snapshot and return Stale for intervening target changes.
apply_patch's baseVersions is an optional extra guard.

Finish edits, then call get_diagnostics once (cached). Use apply_code_fix (id/line/column).
On Conflict with candidates, pass the chosen actionId to apply_code_action.
Do not hand-edit declarations solely to silence diagnostics unless asked.

## Razor

Positions refer to C# in Razor source (@code/@functions, @{ }, expressions).
Edits map generated C# to source: unmappable edits return NotSupported; mismatched source,
Conflict. Analyzers skip generated Razor; compiler/RZxxxx diagnostics and CS8019/IDE0005
unused-using fixes are supported. _Imports.razor/_ViewImports.cshtml usings are never removed.

## Tool reference

Per-tool contracts (descriptions and parameters) live in references/tools/<tool>.md:

- [apply_code_action](references/tools/apply_code_action.md)
- [apply_code_fix](references/tools/apply_code_fix.md)
- [apply_patch](references/tools/apply_patch.md)
- [change_signature](references/tools/change_signature.md)
- [extract_method](references/tools/extract_method.md)
- [find_dead_code](references/tools/find_dead_code.md)
- [find_dead_conditionals](references/tools/find_dead_conditionals.md)
- [find_definition](references/tools/find_definition.md)
- [find_implementations](references/tools/find_implementations.md)
- [find_reads](references/tools/find_reads.md)
- [find_references](references/tools/find_references.md)
- [find_writes](references/tools/find_writes.md)
- [get_callees](references/tools/get_callees.md)
- [get_callers](references/tools/get_callers.md)
- [get_code_actions](references/tools/get_code_actions.md)
- [get_diagnostics](references/tools/get_diagnostics.md)
- [get_expression_info](references/tools/get_expression_info.md)
- [get_members](references/tools/get_members.md)
- [get_solution_status](references/tools/get_solution_status.md)
- [get_symbol](references/tools/get_symbol.md)
- [get_symbol_body](references/tools/get_symbol_body.md)
- [get_type_hierarchy](references/tools/get_type_hierarchy.md)
- [multi_query](references/tools/multi_query.md)
- [open_solution](references/tools/open_solution.md)
- [reload_solution](references/tools/reload_solution.md)
- [remove_unused_usings](references/tools/remove_unused_usings.md)
- [rename_parameter](references/tools/rename_parameter.md)
- [rename_symbol](references/tools/rename_symbol.md)
- [search_symbols](references/tools/search_symbols.md)
