using System.ComponentModel;
using System.Globalization;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Metrics;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Metrics.GetCodeMetrics;

[McpServerToolType]
public sealed class GetCodeMetricsTool
{
	public const string GetCodeMetricsName = "get_code_metrics";

	/// <summary>Marks an ancestor line kept only for context: it did not match the scope's filters.</summary>
	private const string ContextMarker = "~";

	private const string Columns = "Kind,Name,Maintainability Index,Cyclomatic Complexity,Class Coupling,Depth Of Inheritance,Source Lines,Executable Lines,Parameter Types";

	private const string GlobalNamespaceName = "<global>";

	private static readonly HashSet<string> KnownKinds = new(StringComparer.OrdinalIgnoreCase)
	{
		"namespace",
		"class",
		"struct",
		"interface",
		"enum",
		"delegate",
		"method",
		"property",
		"field",
		"event",
		"localfunction",
	};

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ResponseBudget Budget;

	public GetCodeMetricsTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description($"""
		SupportsPaging=Y

		Reports code metrics computed from the loaded in-memory solution - no build, no reload, no emitted
		assembly. Metrics reflect the loaded configuration; inactive #if branches are not measured.

		Body: project > namespace > nested namespace > type > nested type > member > local function.
		Leaves: {Columns}. An empty field means the metric does not apply to that symbol (maintainability index, cyclomatic complexity and executable
		lines when the declaration has no code; depth of inheritance outside classes). Every figure is
		measured over the symbol's own declaration span, never a sum of its children.

		Parameter types appear only to disambiguate overloads declared in the symbol's own type, as a
		pipe-delimited list; () marks the parameterless overload. They are empty on every other line.

		A line prefixed with '~' (contextMarker) is an ancestor kept only for context: it did not match the
		filters, and its metric fields are still its own span.

		scope: a namespace/type/member name (fully qualified or a uniquely matching dotted suffix);
		projectName: one project; kinds: symbol-kind filter; omit all for the whole solution.
		filter: comma-separated conditions, all ANDed, e.g. 'Cyclomatic Complexity>10,Maintainability Index<65'
		(metric names as in the columns header, spaces and case optional; operators >, >=, <, <=, =).
		Filters apply to matching symbols only and are applied before paging; ancestors are never counted.
		""")]
	[McpServerTool(
		Name = GetCodeMetricsName,
		Title = "Measure code metrics",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> GetCodeMetrics(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Namespace, type or member to measure (fully qualified, or a uniquely matching dotted suffix); omit for the whole solution.")] string? scope = null,
		[Description("Limit to one project by name, e.g. Morris.Roslynk; omit for every project.")] string? projectName = null,
		[Description("Symbol kinds to report, e.g. ['method','class']; omit for every kind.")] string[]? kinds = null,
		[Description("Metric conditions, comma-separated and ANDed, e.g. 'Cyclomatic Complexity>10,Maintainability Index<65'. Metrics: Maintainability Index, Cyclomatic Complexity, Class Coupling, Depth Of Inheritance, Source Lines, Executable Lines (spaces and case optional); operators: >, >=, <, <=, =.")] string? filter = null,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		[Description("Previous snapshot id; mismatch returns Stale before measuring.")] string? expectSnapshot = null,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		SolutionModel model = await instance.ReadModelAsync();
		return await GetCodeMetricsCoreAsync(model, instance, scope, projectName, kinds, filter, startItem, maxSize, Budget, expectSnapshot, CancellationToken.None);
	}

	internal async Task<string> GetCodeMetricsCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string? scope = null,
		string? projectName = null,
		string[]? kinds = null,
		string? filter = null,
		int startItem = 1,
		int maxSize = 0,
		ResponseBudget? budget = null,
		string? expectSnapshot = null,
		CancellationToken token = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing(instance.LoadedProjects));

		if (expectSnapshot is not null)
		{
			if (!Guid.TryParse(expectSnapshot, out Guid expected))
				return Failure(Error.Invalid($"'expectSnapshot' must be a snapshot=<id> from a previous get_code_metrics response, got '{expectSnapshot}'."));

			if (expected != model.Id)
			{
				return Failure(Error.Stale(
					$"The solution moved on since the snapshot you expected (expected {expected.ToString("N")}, current {model.Id.ToString("N")}). Re-run rather than stitching two generations."))
					+ $"snapshot={model.Id.ToString("N")}\n";
			}
		}

		Solution solution = model.Solution;

		IReadOnlyList<MetricCondition> conditions;
		try
		{
			conditions = MetricFilter.Parse(filter);
		}
		catch (ArgumentException exception)
		{
			return Failure(Error.Invalid(exception.Message));
		}

		HashSet<string>? kindFilter = null;
		if (kinds is { Length: > 0 })
		{
			kindFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string kind in kinds)
			{
				string trimmed = kind.Trim();
				if (!KnownKinds.Contains(trimmed))
				{
					return Failure(Error.Invalid(
						$"Unknown symbol kind '{kind}'. Valid kinds: {string.Join(", ", KnownKinds.OrderBy(name => name, StringComparer.Ordinal))}."));
				}

				kindFilter.Add(trimmed);
			}
		}

		ScopeResolution selection = await ResolveScopeAsync(solution, projectName, scope, token);
		if (selection.Failure is Error failure)
			return Failure(failure);

		var rows = new List<MetricsRow>();

		void Add(ISymbol symbol, MetricSet metrics, int depth, bool matched, string parameterTypes)
		{
			string line = Line(symbol, metrics, parameterTypes);
			rows.Add(new MetricsRow(depth, matched ? line : ContextMarker + line, matched));
		}

		bool IsMatch(ISymbol symbol, MetricSet metrics)
		{
			if (kindFilter is not null && !kindFilter.Contains(SymbolKindText.Of(symbol)))
				return false;

			foreach (MetricCondition condition in conditions)
			{
				if (!condition.Matches(metrics))
					return false;
			}

			return true;
		}

		ISymbol? scopeSymbol = selection.Symbol;
		INamedTypeSymbol? targetType = scopeSymbol switch
		{
			INamedTypeSymbol type => type,
			{ } member => member.ContainingType,
			_ => null,
		};
		ISymbol? targetMember = scopeSymbol is INamedTypeSymbol ? null : scopeSymbol;
		if (targetMember is IMethodSymbol { MethodKind: MethodKind.LocalFunction } localFunction)
			targetMember = LocalFunctions.NamedContainer(localFunction);
		ISymbol? targetLocal = scopeSymbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } ? scopeSymbol : null;

		ProjectId? scopeProjectId = scopeSymbol?.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree is SyntaxTree scopeTree
			? solution.GetDocument(scopeTree)?.Project.Id
			: null;

		var seenProjectNames = new HashSet<string>(StringComparer.Ordinal);

		async Task WalkLocalFunctionsAsync(ISymbol container, int depth)
		{
			IReadOnlyList<IMethodSymbol> locals = (await LocalFunctions.FindAllInAsync(solution, container, token))
				.OrderBy(local => local.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0)
				.ToList();

			foreach (IMethodSymbol local in locals)
			{
				if (targetLocal is not null
					&& !SymbolEqualityComparer.Default.Equals(local, targetLocal)
					&& !SymbolEqualityComparer.Default.Equals(container, targetLocal))
				{
					continue;
				}

				MetricSet metrics = await MetricsCalculator.ComputeAsync(local, solution, token);
				bool overloaded = locals.Count(candidate => string.Equals(candidate.Name, local.Name, StringComparison.Ordinal)) > 1;
				Add(local, metrics, depth, IsMatch(local, metrics), ParameterField(local, overloaded));
				await WalkLocalFunctionsAsync(local, depth + 1);
			}
		}

		async Task WalkMembersAsync(INamedTypeSymbol type, int depth)
		{
			foreach (ISymbol member in OrderedMembers(type))
			{
				if (targetMember is not null && !SymbolEqualityComparer.Default.Equals(member, targetMember))
					continue;

				MetricSet metrics = await MetricsCalculator.ComputeAsync(member, solution, token);
				Add(member, metrics, depth, IsMatch(member, metrics), ParameterField(member, IsOverloaded(member, type)));
				await WalkLocalFunctionsAsync(member, depth + 1);
			}
		}

		async Task WalkTypeAsync(INamedTypeSymbol type, int depth)
		{
			Relation relation = TypeRelation(type, targetType);
			if (relation == Relation.Skip)
				return;

			MetricSet metrics = await MetricsCalculator.ComputeAsync(type, solution, token);
			Add(type, metrics, depth, IsMatch(type, metrics), "");

			foreach (INamedTypeSymbol nested in OrderedTypes(type.GetTypeMembers()))
				await WalkTypeAsync(nested, depth + 1);

			if (relation != Relation.Path)
				await WalkMembersAsync(type, depth + 1);
		}

		async Task WalkTypesAsync(INamespaceSymbol @namespace, int depth)
		{
			foreach (INamedTypeSymbol type in OrderedTypes(@namespace.GetTypeMembers()))
				await WalkTypeAsync(type, depth);
		}

		async Task WalkNamespacesAsync(INamespaceSymbol parent, int depth, INamespaceSymbol? targetNamespace)
		{
			foreach (INamespaceSymbol child in parent.GetNamespaceMembers().OrderBy(child => child.Name, StringComparer.Ordinal))
			{
				Relation relation = NamespaceRelation(child, targetNamespace);
				if (relation == Relation.Skip)
					continue;

				MetricSet metrics = await MetricsCalculator.ComputeAsync(child, solution, token);
				Add(child, metrics, depth, IsMatch(child, metrics), "");

				await WalkNamespacesAsync(child, depth + 1, targetNamespace);
				if (relation != Relation.Path)
					await WalkTypesAsync(child, depth + 1);
			}
		}

		foreach (Project project in solution.Projects
			.OrderBy(project => ProjectName.Of(project), StringComparer.Ordinal)
			.ThenBy(project => project.Name, StringComparer.Ordinal))
		{
			string? projectDisplayName = ProjectName.Of(project);
			if (selection.ProjectName is string selectedProject
				&& !string.Equals(projectDisplayName, selectedProject, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			// A multi-targeted project loads once per framework under one project name; the first instance
			// measures the same source, so the report carries it once.
			if (projectDisplayName is not null && !seenProjectNames.Add(projectDisplayName))
				continue;

			if (scopeProjectId is ProjectId required && !required.Equals(project.Id))
				continue;

			if (await project.GetCompilationAsync(token) is not Compilation compilation)
				continue;

			INamespaceSymbol global = compilation.Assembly.GlobalNamespace;

			INamespaceSymbol? targetNamespace = null;
			if (selection.NamespaceName is string namespaceName)
			{
				targetNamespace = FindNamespace(global, namespaceName);
				if (targetNamespace is null)
					continue;
			}
			else if (scopeSymbol is { } symbol)
			{
				targetNamespace = symbol.ContainingNamespace;
			}

			rows.Add(new MetricsRow(0, ContextMarker + (projectDisplayName ?? project.Name), false));

			await WalkNamespacesAsync(global, 1, targetNamespace);

			if ((targetNamespace is null || targetNamespace.IsGlobalNamespace) && global.GetTypeMembers().Length > 0)
			{
				rows.Add(new MetricsRow(1, ContextMarker + GlobalLine(), false));
				await WalkTypesAsync(global, 2);
			}
		}

		// Keep only matching rows and the ancestors that lead to them: a filter narrows the report to the
		// matches, with just enough hierarchy above each one to place it. Without a filter every row matches,
		// so the whole scope renders.
		var kept = new bool[rows.Count];
		var open = new Stack<int>();
		for (int index = 0; index < rows.Count; index++)
		{
			MetricsRow row = rows[index];
			while (open.Count > 0 && rows[open.Peek()].Depth >= row.Depth)
				open.Pop();

			if (row.Matched)
			{
				kept[index] = true;
				foreach (int ancestor in open)
					kept[ancestor] = true;
			}

			open.Push(index);
		}

		rows = rows.Where((row, index) => kept[index]).ToList();

		var items = new List<OutlineItem>();
		var chain = new List<string>();
		foreach (MetricsRow row in rows)
		{
			// The chain is truncated to the row's own depth before the row's context is captured, so a
			// sibling never inherits the previous sibling's children as its ancestors.
			while (chain.Count > row.Depth)
				chain.RemoveAt(chain.Count - 1);

			if (row.Matched)
				items.Add(OutlineItem.Single(row.Text, chain.ToArray()));

			chain.Add(row.Text);
		}

		PagedRender render = ItemPaging.Render(
			model.Status,
			builder => builder
				.Header("columns", Columns)
				.Header("contextMarker", ContextMarker)
				.Header("snapshot", model.Id.ToString("N")),
			builder =>
			{
				foreach (MetricsRow row in rows)
					builder.Line(row.Depth, row.Text);
			},
			items,
			startItem,
			maxSize,
			budget);

		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => Failure(Error.Invalid(invalid.Message)),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}

	/// <summary>
	/// Resolves the requested scope: a project name, then a namespace/type/member name. A symbol name is
	/// resolved through the shared resolver first (so a type or member wins over a namespace that shares the
	/// dotted name); a namespace is matched by fully-qualified name or by a dotted suffix that identifies one.
	/// </summary>
	private async Task<ScopeResolution> ResolveScopeAsync(Solution solution, string? projectName, string? scope, CancellationToken token)
	{
		string? selectedProject = null;
		if (!string.IsNullOrWhiteSpace(projectName))
		{
			List<string> names = solution.Projects
				.Select(ProjectName.Of)
				.OfType<string>()
				.Distinct(StringComparer.Ordinal)
				.OrderBy(name => name, StringComparer.Ordinal)
				.ToList();

			selectedProject = names.FirstOrDefault(name => string.Equals(name, projectName, StringComparison.OrdinalIgnoreCase));
			if (selectedProject is null)
				return new ScopeResolution(null, null, null, Error.NotFound($"No project named '{projectName}'.", names));
		}

		if (string.IsNullOrWhiteSpace(scope))
			return new ScopeResolution(selectedProject, null, null, null);

		IReadOnlyList<ISymbol> matches = (await SymbolResolver.FindByFullyQualifiedNameAsync(solution, scope, token))
			.Where(symbol => symbol.Locations.Any(location => location.IsInSource))
			.Where(IsMeasurable)
			.ToList();

		if (matches.Count > 1)
			return new ScopeResolution(selectedProject, null, null, SymbolAmbiguity.Ambiguous(scope, matches));

		if (matches.Count == 1)
			return new ScopeResolution(selectedProject, null, matches[0], null);

		List<string> namespaces = await NamespaceNamesAsync(solution, token);
		string? exact = namespaces.FirstOrDefault(name => string.Equals(name, scope, StringComparison.Ordinal));
		if (exact is not null)
			return new ScopeResolution(selectedProject, exact, null, null);

		List<string> suffixed = namespaces.Where(name => name.EndsWith("." + scope, StringComparison.Ordinal)).ToList();
		if (suffixed.Count == 1)
			return new ScopeResolution(selectedProject, suffixed[0], null, null);
		if (suffixed.Count > 1)
			return new ScopeResolution(selectedProject, null, null, Error.Ambiguous($"'{scope}' matched {suffixed.Count} namespaces.", suffixed));

		return new ScopeResolution(selectedProject, null, null, Error.NotFound($"No namespace, type or member matched '{scope}'."));
	}

	private static bool IsMeasurable(ISymbol symbol) =>
		symbol is INamedTypeSymbol
		|| symbol is IMethodSymbol { MethodKind: not (MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove) }
		|| symbol is IPropertySymbol or IFieldSymbol or IEventSymbol;

	private static async Task<List<string>> NamespaceNamesAsync(Solution solution, CancellationToken token)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (Project project in solution.Projects)
		{
			if (await project.GetCompilationAsync(token) is not Compilation compilation)
				continue;

			CollectNamespaces(compilation.Assembly.GlobalNamespace, names);
		}

		return names.OrderBy(name => name, StringComparer.Ordinal).ToList();
	}

	private static void CollectNamespaces(INamespaceSymbol parent, HashSet<string> names)
	{
		foreach (INamespaceSymbol child in parent.GetNamespaceMembers())
		{
			names.Add(child.ToDisplayString());
			CollectNamespaces(child, names);
		}
	}

	private static INamespaceSymbol? FindNamespace(INamespaceSymbol global, string name)
	{
		INamespaceSymbol? current = global;
		foreach (string segment in name.Split('.'))
		{
			current = current.GetNamespaceMembers().FirstOrDefault(candidate => string.Equals(candidate.Name, segment, StringComparison.Ordinal));
			if (current is null)
				return null;
		}

		return current;
	}

	private static Relation NamespaceRelation(INamespaceSymbol candidate, INamespaceSymbol? target)
	{
		if (target is null)
			return Relation.Subtree;
		if (SymbolEqualityComparer.Default.Equals(candidate, target))
			return Relation.Target;

		for (INamespaceSymbol? containing = target.ContainingNamespace; containing is not null; containing = containing.ContainingNamespace)
		{
			if (SymbolEqualityComparer.Default.Equals(candidate, containing))
				return Relation.Path;
		}

		for (INamespaceSymbol? containing = candidate.ContainingNamespace; containing is not null; containing = containing.ContainingNamespace)
		{
			if (SymbolEqualityComparer.Default.Equals(containing, target))
				return Relation.Subtree;
		}

		return Relation.Skip;
	}

	private static Relation TypeRelation(INamedTypeSymbol type, INamedTypeSymbol? target)
	{
		if (target is null)
			return Relation.Subtree;
		if (SymbolEqualityComparer.Default.Equals(type, target))
			return Relation.Target;

		for (INamedTypeSymbol? containing = target.ContainingType; containing is not null; containing = containing.ContainingType)
		{
			if (SymbolEqualityComparer.Default.Equals(type, containing))
				return Relation.Path;
		}

		for (INamedTypeSymbol? containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
		{
			if (SymbolEqualityComparer.Default.Equals(containing, target))
				return Relation.Subtree;
		}

		return Relation.Skip;
	}

	private static IEnumerable<INamedTypeSymbol> OrderedTypes(IEnumerable<INamedTypeSymbol> types) =>
		types
			.OrderBy(type => type.Name, StringComparer.Ordinal)
			.ThenBy(FirstLine);

	private static IEnumerable<ISymbol> OrderedMembers(INamedTypeSymbol type) =>
		type.GetMembers()
			.Where(member => !member.IsImplicitlyDeclared)
			.Where(member => member is not INamedTypeSymbol)
			.OrderBy(FirstLine)
			.ThenBy(member => member.Name, StringComparer.Ordinal);

	private static int FirstLine(ISymbol symbol) =>
		symbol.Locations.FirstOrDefault(location => location.IsInSource)?.GetLineSpan().StartLinePosition.Line ?? int.MaxValue;

	/// <summary>
	/// True when the symbol's own type declares more than one method of its name, so its parameter list is
	/// needed to tell the overloads apart. Inherited overloads do not count.
	/// </summary>
	private static bool IsOverloaded(ISymbol member, INamedTypeSymbol type) =>
		member is IMethodSymbol method
		&& type.GetMembers(method.Name)
			.OfType<IMethodSymbol>()
			.Count(candidate => !candidate.IsImplicitlyDeclared && candidate.MethodKind != MethodKind.LocalFunction) > 1;

	/// <summary>The final column: pipe-delimited parameter types only for an overloaded method, <c>()</c> for a
	/// parameterless overload, and empty everywhere else.</summary>
	private static string ParameterField(ISymbol member, bool overloaded)
	{
		if (!overloaded || member is not IMethodSymbol method)
			return "";

		return method.Parameters.Length == 0 ? "()" : ParameterTypes.Of(method);
	}

	private static string Line(ISymbol symbol, MetricSet metrics, string parameterTypes) =>
		string.Join(
			",",
			SymbolKindText.Of(symbol),
			OutlineBuilder.Field(symbol.Name),
			Format(metrics.MaintainabilityIndex),
			Format(metrics.CyclomaticComplexity),
			Format(metrics.ClassCoupling),
			Format(metrics.DepthOfInheritance),
			metrics.SourceLines.ToString(CultureInfo.InvariantCulture),
			Format(metrics.ExecutableLines),
			parameterTypes);

	/// <summary>A namespace declared outside any namespace has no declaration of its own to measure.</summary>
	private static string GlobalLine() =>
		string.Join(",", "namespace", GlobalNamespaceName, "", "", "", "", "", "", "");

	private static string Format(int? value) =>
		value?.ToString(CultureInfo.InvariantCulture) ?? "";

	/// <summary>Where a walked namespace or type sits relative to the requested scope.</summary>
	private enum Relation
	{
		Skip,
		Path,
		Target,
		Subtree,
	}

	private sealed record MetricsRow(int Depth, string Text, bool Matched);

	private sealed record ScopeResolution(string? ProjectName, string? NamespaceName, ISymbol? Symbol, Error? Failure);
}
