using System.Globalization;

namespace Morris.Roslynk.Infrastructure.Metrics;

/// <summary>The metric a condition tests, named exactly as the output's column header names it.</summary>
public enum MetricKind
{
	MI,
	CC,
	Cpl,
	InhDep,
	SL,
	EL,
}

public enum MetricComparison
{
	LessThan,
	LessThanOrEqual,
	GreaterThan,
	GreaterThanOrEqual,
	Equal,
}

/// <summary>
/// One 'CC>10'-style condition. It matches only when the metric applies to the symbol: a condition on a
/// metric the symbol does not carry (a field's CC, a struct's InhDep) never matches, rather than treating
/// the absent value as zero.
/// </summary>
public sealed record MetricCondition(MetricKind Kind, MetricComparison Comparison, int Value)
{
	public bool Matches(MetricSet metrics)
	{
		if (metrics.ValueOf(Kind) is not int actual)
			return false;

		return Comparison switch
		{
			MetricComparison.LessThan => actual < Value,
			MetricComparison.LessThanOrEqual => actual <= Value,
			MetricComparison.GreaterThan => actual > Value,
			MetricComparison.GreaterThanOrEqual => actual >= Value,
			MetricComparison.Equal => actual == Value,
			_ => false,
		};
	}
}

/// <summary>
/// Parses the metric filter: comma-separated conditions, all ANDed together, e.g. <c>CC&gt;10,MI&lt;65</c>.
/// There is no OR and no grouping - one condition per comma - and an empty or omitted filter matches every
/// symbol. An unrecognized metric name, operator or value is a caller error (ArgumentException), which the
/// tool maps to <c>error=Invalid</c>.
/// </summary>
public static class MetricFilter
{
	private static readonly (string Symbol, MetricComparison Comparison)[] Comparisons =
	[
		(">=", MetricComparison.GreaterThanOrEqual),
		("<=", MetricComparison.LessThanOrEqual),
		("==", MetricComparison.Equal),
		(">", MetricComparison.GreaterThan),
		("<", MetricComparison.LessThan),
		("=", MetricComparison.Equal),
	];

	public static IReadOnlyList<MetricCondition> Parse(string? filter)
	{
		var conditions = new List<MetricCondition>();
		if (string.IsNullOrWhiteSpace(filter))
			return conditions;

		foreach (string part in filter.Split(','))
		{
			string text = part.Trim();
			if (text.Length == 0)
				continue;

			conditions.Add(ParseCondition(text));
		}

		return conditions;
	}

	private static MetricCondition ParseCondition(string text)
	{
		int index = -1;
		MetricComparison comparison = MetricComparison.Equal;
		int length = 0;
		foreach ((string symbol, MetricComparison candidate) in Comparisons)
		{
			int found = text.IndexOf(symbol, StringComparison.Ordinal);
			if (found > 0)
			{
				index = found;
				comparison = candidate;
				length = symbol.Length;
				break;
			}
		}

		if (index < 0)
			throw new ArgumentException($"Metric condition '{text}' has no comparison. Write e.g. 'CC>10' or 'MI<65'.");

		string name = text[..index].Trim();
		string valueText = text[(index + length)..].Trim();

		if (name.Length == 0 || !char.IsLetter(name[0])
			|| !Enum.TryParse(name, ignoreCase: true, out MetricKind kind)
			|| !Enum.IsDefined(kind))
		{
			throw new ArgumentException($"Unknown metric '{name}' in '{text}'. Metrics: MI, CC, Cpl, InhDep, SL, EL.");
		}

		if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
			throw new ArgumentException($"Metric condition '{text}' needs an integer value.");

		return new MetricCondition(kind, comparison, value);
	}
}
