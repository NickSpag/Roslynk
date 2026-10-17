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
- includeErrors: List errors. (default: false)
- includeWarnings: List warnings. (default: false)
- includeInfo: List info diagnostics. (default: false)
- includeHidden: List hidden diagnostics. (default: false)
- includeAnalyzers: Run analyzers; false is faster, compiler-only. (default: true)
- projectName: Project filter: outline name with optional extension, case-insensitive; all loaded target frameworks. (default: null)
- filePath: File filter (case-insensitive), including diagnostics mapped to Razor source. (default: null)
- ids: Diagnostic ids (case-insensitive), e.g. ["CS0246"]; null/empty matches all. (default: null)
- truncated: Body-entry cap (ids with summaryOnly). Capped bodies report count=<listed>, truncated=Y. (default: null)
- summaryOnly: Summarize by diagnostic id. (default: false)
- startItem: First item (1-based). (default: 1)
- maxSize: Page character limit. (default: 0)
