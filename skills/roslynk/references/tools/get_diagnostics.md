---
tool: get_diagnostics
---
SupportsPaging=Y

Returns compiler/analyzer diagnostics.

errors/warnings/infos/hidden count all content-filtered severities regardless of include flags.
The body lists flagged severities; summaryOnly summarizes all if none are flagged, otherwise
only flagged ones. Details: project > folder > file > severity; leaves: <id>,<line:col>,<message>.
Summary: <id>,<count>,<project>,<path>,<line:col>,<message> per id, ordered by count descending then id.

projectName/filePath/ids filter after compilation, reusing cached results; active filters echo
filter=<parameter>:<value>. Empty filtered results do not prove the solution clean.
Unknown project/file returns NotFound with candidates. Known matches without diagnostics return zeros.
Private fixer-trigger IDs are omitted; fix the public rule instead (e.g. IDE0005).

## Parameters

- solutionId: Solution handle.
- includeErrors: List errors.
- includeWarnings: List warnings.
- includeInfo: List info diagnostics.
- includeHidden: List hidden diagnostics.
- includeAnalyzers: Run analyzers; false is faster, compiler-only.
- projectName: Project filter: outline name with optional extension, case-insensitive; all loaded target frameworks.
- filePath: File filter (case-insensitive), including diagnostics mapped to Razor source.
- ids: Diagnostic ids (case-insensitive), e.g. ["CS0246"]; null/empty matches all.
- truncated: Body-entry cap (ids with summaryOnly). Capped bodies report count=<listed>, truncated=Y.
- summaryOnly: Summarize by diagnostic id.
- startItem: First item (1-based).
- maxSize: Page character limit.
