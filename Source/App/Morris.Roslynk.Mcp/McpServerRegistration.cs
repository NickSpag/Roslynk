using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Mcp.Observability;

namespace Morris.Roslynk.Mcp;

/// <summary>
/// Server identity and shared library guidance returned to clients during MCP initialization.
/// </summary>
internal static class McpServerRegistration
{
	public static void Configure(McpServerOptions options)
	{
		options.ServerInfo = new Implementation
		{
			Name = "Roslynk",
			Title = "Roslynk; C# semantic intelligence",
			Version = typeof(McpServerRegistration).Assembly.GetName().Version?.ToString() ?? "1.0.0"
		};

		// Shared guidance has one home here; tool attributes carry only their own contracts.
		// Local builds keep the full guidance inline so tests and local harnesses need no network fetch.
		// Released builds treat skills/roslynk (SKILL.md + references/tools/<tool>.md) as the single home
		// and point the harness at the version-tagged copy on GitHub instead.
		options.ServerInstructions =
#if DEBUG
			DebugInstructions;
#else
			ReleasedInstructions;
#endif

		options.NameTracesAfterTools();
	}

	/// <summary>Debug-build instructions: the full shared guidance inline (mirrors skills/roslynk).</summary>
	private static readonly string DebugInstructions =
		$$"""
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

			## Skill

			Per-tool contracts (descriptions and parameters) are not published in the schema. Install the
			roslynk skill matching this server version from
			https://github.com/mrpmorris/Roslynk/tree/{{SkillTag()}}/skills/roslynk
			(SKILL.md, plus one references/tools/<tool>.md per tool).
		""";

	/// <summary>
	/// Released-build instructions: the skills/roslynk folder in the repo is the single home of the
	/// shared guidance and the per-tool contracts, so the harness is told to install the skill copy
	/// matching this server's release tag. Exposed internal so tests can assert it from a Debug build.
	/// </summary>
	internal static string ReleasedInstructions =>
		$$"""
		*.cs, *.razor, *.cshtml semantic intelligence and editing/refactoring via Roslyn.

		Per-tool contracts and shared conventions are not published in the tool schema. Install the
		roslynk skill matching this server version from
		https://github.com/mrpmorris/Roslynk/tree/{{SkillTag()}}/skills/roslynk
		(SKILL.md, plus one references/tools/<tool>.md per tool).
		""";

	/// <summary>
	/// The GitHub release tag this build ships from: the package version without its prerelease suffix
	/// ('0.0.1-local' locally; CI sets the numeric release tag version), matching the tag URLs under
	/// https://github.com/mrpmorris/Roslynk/tree/&lt;tag&gt;/skills/roslynk.
	/// </summary>
	private static string SkillTag()
	{
		string version = typeof(McpServerRegistration).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? "0.0.0";
		int suffix = version.IndexOfAny(['-', '+']);
		return suffix >= 0 ? version[..suffix] : version;
	}
}
