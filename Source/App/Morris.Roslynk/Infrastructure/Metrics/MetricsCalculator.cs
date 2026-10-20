using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Metrics;

/// <summary>
/// Computes Microsoft-style code metrics from the loaded in-memory solution's own syntax and semantics -
/// never from a build, an emitted assembly or a reload from disk. Microsoft's metrics engine
/// (Microsoft.CodeAnalysis.Metrics) ships only as a console executable that measures assemblies on disk and
/// exposes no callable library API, so this is an independent implementation of the documented metrics
/// over Roslyn syntax trees:
/// <list type="bullet">
/// <item>Maintainability Index: MAX(0, (171 - 5.2*ln(Halstead volume) - 0.23*cyclomatic complexity - 16.2*ln(source lines)) * 100 / 171),
/// rounded; empty when the declaration contains no operator/operand token (an interface, an enum, an
/// abstract signature), because the volume is then zero.</item>
/// <item>Cyclomatic Complexity: 1 + decision points (if, loops, case/default labels, catch, conditional
/// expression, &amp;&amp;, ||, ??).</item>
/// <item>Class Coupling: distinct named types referenced by the declaration - identifiers bound to a
/// type, and members' declaring types - excluding the symbol's own type chain and C# keyword types.</item>
/// <item>Depth Of Inheritance: classes only, counting the class itself and its base classes up to
/// (not including) System.Object.</item>
/// <item>Source Lines: lines the declaration spans, summed over its parts.</item>
/// <item>Executable Lines: distinct lines carrying a statement or an expression-bodied body.</item>
/// </list>
/// Every figure is measured over the symbol's own declaration span, never by summing its children, so a
/// namespace or type reports what its whole declaration contains.
/// </summary>
public static class MetricsCalculator
{
	/// <summary>Tokens counted as Halstead operators: C# operators plus the keywords that shape control flow.</summary>
	private static readonly HashSet<SyntaxKind> OperatorKinds =
	[
		SyntaxKind.PlusToken,
		SyntaxKind.MinusToken,
		SyntaxKind.AsteriskToken,
		SyntaxKind.SlashToken,
		SyntaxKind.PercentToken,
		SyntaxKind.PlusPlusToken,
		SyntaxKind.MinusMinusToken,
		SyntaxKind.EqualsToken,
		SyntaxKind.PlusEqualsToken,
		SyntaxKind.MinusEqualsToken,
		SyntaxKind.AsteriskEqualsToken,
		SyntaxKind.SlashEqualsToken,
		SyntaxKind.PercentEqualsToken,
		SyntaxKind.AmpersandToken,
		SyntaxKind.AmpersandEqualsToken,
		SyntaxKind.BarToken,
		SyntaxKind.BarEqualsToken,
		SyntaxKind.CaretToken,
		SyntaxKind.CaretEqualsToken,
		SyntaxKind.LessThanToken,
		SyntaxKind.LessThanEqualsToken,
		SyntaxKind.GreaterThanToken,
		SyntaxKind.GreaterThanEqualsToken,
		SyntaxKind.EqualsEqualsToken,
		SyntaxKind.ExclamationEqualsToken,
		SyntaxKind.ExclamationToken,
		SyntaxKind.TildeToken,
		SyntaxKind.LessThanLessThanToken,
		SyntaxKind.LessThanLessThanEqualsToken,
		SyntaxKind.GreaterThanGreaterThanToken,
		SyntaxKind.GreaterThanGreaterThanEqualsToken,
		SyntaxKind.QuestionToken,
		SyntaxKind.QuestionQuestionToken,
		SyntaxKind.QuestionQuestionEqualsToken,
		SyntaxKind.ColonColonToken,
		SyntaxKind.DotToken,
		SyntaxKind.EqualsGreaterThanToken,
		SyntaxKind.IfKeyword,
		SyntaxKind.ElseKeyword,
		SyntaxKind.ForKeyword,
		SyntaxKind.ForEachKeyword,
		SyntaxKind.WhileKeyword,
		SyntaxKind.DoKeyword,
		SyntaxKind.SwitchKeyword,
		SyntaxKind.CaseKeyword,
		SyntaxKind.DefaultKeyword,
		SyntaxKind.BreakKeyword,
		SyntaxKind.ContinueKeyword,
		SyntaxKind.ReturnKeyword,
		SyntaxKind.ThrowKeyword,
		SyntaxKind.TryKeyword,
		SyntaxKind.CatchKeyword,
		SyntaxKind.FinallyKeyword,
		SyntaxKind.UsingKeyword,
		SyntaxKind.LockKeyword,
		SyntaxKind.NewKeyword,
		SyntaxKind.AwaitKeyword,
		SyntaxKind.YieldKeyword,
		SyntaxKind.IsKeyword,
		SyntaxKind.AsKeyword,
		SyntaxKind.InKeyword,
		SyntaxKind.OutKeyword,
		SyntaxKind.RefKeyword,
		SyntaxKind.ParamsKeyword,
		SyntaxKind.GotoKeyword,
		SyntaxKind.WhenKeyword,
	];

	/// <summary>
	/// Measures <paramref name="symbol"/> over every hand-written declaration part it has. A part declared by
	/// a source generator is skipped: its text has no file on disk, so it is not the code a caller can act on.
	/// </summary>
	public static async Task<MetricSet> ComputeAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
	{
		var accumulator = new Accumulator(Excluded(symbol));

		foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
		{
			SyntaxNode declaration = await reference.GetSyntaxAsync(cancellationToken);
			if (GeneratedSource.IsGenerated(solution, declaration.SyntaxTree))
				continue;

			accumulator.AddSyntax(declaration);

			if (solution.GetDocument(declaration.SyntaxTree) is not Document document)
				continue;
			if (await document.GetSemanticModelAsync(cancellationToken) is not SemanticModel model)
				continue;

			accumulator.AddCoupling(model, declaration, cancellationToken);
		}

		return accumulator.ToMetricSet(symbol);
	}

	/// <summary>
	/// True for the symbols maintainability index, cyclomatic complexity and executable lines apply to: namespaces, types that can hold bodies, and members that
	/// can have one. A field, an enum member or a delegate declaration is not measured for them.
	/// </summary>
	private static bool CarriesCode(ISymbol symbol) =>
		symbol is INamespaceSymbol
		|| symbol is INamedTypeSymbol { TypeKind: not (TypeKind.Enum or TypeKind.Delegate) }
		|| symbol is IMethodSymbol
		|| symbol is IPropertySymbol
		|| symbol is IEventSymbol;

	/// <summary>The symbol itself (for a type) and its containing types, which never count as coupling.</summary>
	private static HashSet<ISymbol> Excluded(ISymbol symbol)
	{
		var excluded = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
		if (symbol is INamedTypeSymbol type)
			excluded.Add(type);
		for (INamedTypeSymbol? containing = symbol.ContainingType; containing is not null; containing = containing.ContainingType)
			excluded.Add(containing);

		return excluded;
	}

	private static int DepthOfInheritance(INamedTypeSymbol type)
	{
		int depth = 1;
		for (INamedTypeSymbol? current = type.BaseType; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
			depth++;

		return depth;
	}

	private static bool IsOperand(SyntaxToken token) =>
		token.Kind() is SyntaxKind.IdentifierToken
			or SyntaxKind.NumericLiteralToken
			or SyntaxKind.StringLiteralToken
			or SyntaxKind.Utf8StringLiteralToken
			or SyntaxKind.CharacterLiteralToken
			or SyntaxKind.TrueKeyword
			or SyntaxKind.FalseKeyword
			or SyntaxKind.NullKeyword;

	private static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key)
		where TKey : notnull =>
		counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;

	/// <summary>Accumulates one symbol's syntax and semantic facts, then derives the reported metrics once.</summary>
	private sealed class Accumulator
	{
		private readonly HashSet<ISymbol> Excluded;
		private readonly Dictionary<SyntaxKind, int> OperatorCounts = [];
		private readonly Dictionary<string, int> OperandCounts = new(StringComparer.Ordinal);
		private readonly HashSet<ITypeSymbol> Coupled = new(SymbolEqualityComparer.Default);
		private readonly HashSet<int> ExecutableLines = [];
		private int Decisions;
		private int SourceLines;

		public Accumulator(HashSet<ISymbol> excluded) => Excluded = excluded;

		public void AddSyntax(SyntaxNode declaration)
		{
			FileLinePositionSpan span = declaration.GetLocation().GetLineSpan();
			SourceLines += span.EndLinePosition.Line - span.StartLinePosition.Line + 1;

			foreach (SyntaxToken token in declaration.DescendantTokens())
			{
				if (OperatorKinds.Contains(token.Kind()))
					Increment(OperatorCounts, token.Kind());
				else if (IsOperand(token))
					Increment(OperandCounts, token.Text);
			}

			foreach (SyntaxNode node in declaration.DescendantNodesAndSelf())
			{
				switch (node)
				{
					case IfStatementSyntax:
					case WhileStatementSyntax:
					case DoStatementSyntax:
					case ForStatementSyntax:
					case ForEachStatementSyntax:
					case ForEachVariableStatementSyntax:
					case CaseSwitchLabelSyntax:
					case CasePatternSwitchLabelSyntax:
					case DefaultSwitchLabelSyntax:
					case CatchClauseSyntax:
					case ConditionalExpressionSyntax:
						Decisions++;
						break;
				}

				if (node is BinaryExpressionSyntax binary
					&& binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression)
				{
					Decisions++;
				}

				if (node is StatementSyntax and not BlockSyntax)
					ExecutableLines.Add(node.GetLocation().GetLineSpan().StartLinePosition.Line);

				if (node is ArrowExpressionClauseSyntax arrow)
					ExecutableLines.Add(arrow.Expression.GetLocation().GetLineSpan().StartLinePosition.Line);
			}
		}

		public void AddCoupling(SemanticModel model, SyntaxNode declaration, CancellationToken cancellationToken)
		{
			int visited = 0;
			foreach (SyntaxNode node in declaration.DescendantNodesAndSelf())
			{
				if (node is not (IdentifierNameSyntax or GenericNameSyntax))
					continue;

				if ((++visited & 0xFF) == 0)
					cancellationToken.ThrowIfCancellationRequested();

				AddType(model.GetSymbolInfo(node, cancellationToken).Symbol);
			}
		}

		public MetricSet ToMetricSet(ISymbol symbol)
		{
			int operators = OperatorCounts.Values.Sum();
			int operands = OperandCounts.Values.Sum();
			int distinct = OperatorCounts.Count + OperandCounts.Count;

			// A field, enum member or delegate carries no executable code of its own, so maintainability index, cyclomatic complexity and executable lines do not
			// apply to it even though its declaration has operator and operand tokens.
			bool carriesCode = CarriesCode(symbol);
			bool hasCode = carriesCode && (operators + operands > 0 || ExecutableLines.Count > 0);

			int? maintainability = null;
			if (carriesCode && operators + operands > 0 && distinct > 0 && SourceLines > 0)
			{
				double volume = (operators + operands) * Math.Log2(distinct);
				if (volume > 0)
				{
					double index = (171 - (5.2 * Math.Log(volume)) - (0.23 * (1 + Decisions)) - (16.2 * Math.Log(SourceLines))) * 100 / 171;
					maintainability = (int)Math.Round(Math.Clamp(index, 0, 100), MidpointRounding.AwayFromZero);
				}
			}

			return new MetricSet(
				maintainability,
				hasCode ? 1 + Decisions : null,
				Coupled.Count,
				symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } type ? DepthOfInheritance(type) : null,
				SourceLines,
				hasCode ? ExecutableLines.Count : null);
		}

		private void AddType(ISymbol? symbol)
		{
			INamedTypeSymbol? type = symbol switch
			{
				INamedTypeSymbol named => named,
				IMethodSymbol method => method.ContainingType,
				IPropertySymbol property => property.ContainingType,
				IFieldSymbol field => field.ContainingType,
				IEventSymbol @event => @event.ContainingType,
				_ => null,
			};

			if (type is null || type.SpecialType != SpecialType.None)
				return;

			INamedTypeSymbol definition = type.OriginalDefinition;
			if (Excluded.Contains(definition))
				return;

			Coupled.Add(definition);
		}
	}
}
