namespace Morris.Roslynk.Infrastructure.Metrics;

/// <summary>
/// The code metrics computed for one symbol from the loaded in-memory solution. A metric that does not
/// apply to the symbol (or cannot be derived from its declaration) is null, so a renderer can leave that
/// column empty rather than invent a value. <see cref="SourceLines"/> always applies to a source symbol.
/// </summary>
public sealed record MetricSet(
	int? MaintainabilityIndex,
	int? CyclomaticComplexity,
	int? ClassCoupling,
	int? DepthOfInheritance,
	int SourceLines,
	int? ExecutableLines)
{
	/// <summary>The value a <see cref="MetricKind"/> names, or null when it does not apply.</summary>
	public int? ValueOf(MetricKind kind) =>
		kind switch
		{
			MetricKind.MI => MaintainabilityIndex,
			MetricKind.CC => CyclomaticComplexity,
			MetricKind.Cpl => ClassCoupling,
			MetricKind.InhDep => DepthOfInheritance,
			MetricKind.SL => SourceLines,
			MetricKind.EL => ExecutableLines,
			_ => null,
		};
}
