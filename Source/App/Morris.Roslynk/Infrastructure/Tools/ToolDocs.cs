using System.Collections.Frozen;
using System.Reflection;
using System.Text;

namespace Morris.Roslynk.Infrastructure.Tools;

/// <summary>
/// The AI-facing contracts published alongside the tools. Each tool's description and the shared server
/// instructions are authored in skills/roslynk (one markdown file per tool plus instructions.md) and
/// embedded into this assembly at build time, so the skill files are the single home of that text:
/// source code carries no copy, and the published MCP metadata cannot drift from what a skill reader
/// or a CLI sees. RoslynkTool publishes <see cref="ForTool"/>; a published tool without a reference
/// file is forbidden by the ToolDocsTests drift check.
/// </summary>
public static class ToolDocs
{
	private const string ToolResourcePrefix = "roslynk.docs.tools.";
	private const string InstructionsResourceName = "roslynk.docs.instructions";

	/// <summary>Tool name to published description, keyed by each file's frontmatter name.</summary>
	public static FrozenDictionary<string, string> ByTool { get; } = LoadTools();

	/// <summary>The description published for <paramref name="toolName"/>, or null when undocumented.</summary>
	public static string? ForTool(string toolName) =>
		ByTool.TryGetValue(toolName, out string? description) ? description : null;

	/// <summary>Shared guidance published as the MCP server instructions (instructions.md).</summary>
	public static string ServerInstructions { get; } =
		Normalize(LoadText(InstructionsResourceName)).TrimEnd('\n');

	private static FrozenDictionary<string, string> LoadTools()
	{
		Dictionary<string, string> byTool = new(StringComparer.Ordinal);
		foreach (string resourceName in typeof(ToolDocs).Assembly.GetManifestResourceNames())
		{
			if (!resourceName.StartsWith(ToolResourcePrefix, StringComparison.Ordinal))
				continue;

			string fileName = resourceName[ToolResourcePrefix.Length..];
			(string toolName, string body) = ParseFrontmatter(Normalize(LoadText(resourceName)), resourceName);
			if (!string.Equals(toolName, fileName, StringComparison.Ordinal))
				throw new InvalidOperationException(
					$"'{resourceName}' frontmatter declares 'tool: {toolName}' but the file is named '{fileName}'.");

			// Add, not index: a duplicated tool name fails composition instead of silently overriding.
			byTool.Add(toolName, body);
		}

		return byTool.ToFrozenDictionary(StringComparer.Ordinal);
	}

	/// <summary>
	/// Parses the '--- / tool: &lt;name&gt; / --- / body' shape; the body after the closing delimiter is the
	/// published description verbatim.
	/// </summary>
	private static (string ToolName, string Body) ParseFrontmatter(string text, string resourceName)
	{
		string[] lines = text.Split('\n');
		if (lines.Length < 4 || lines[0] != "---" || !lines[1].StartsWith("tool: ", StringComparison.Ordinal) || lines[2] != "---")
			throw new InvalidOperationException(
				$"'{resourceName}' must start with '---', a 'tool: <name>' line and a closing '---'.");

		string body = string.Join('\n', lines[3..]);
		if (body.EndsWith('\n'))
			body = body[..^1];
		if (string.IsNullOrWhiteSpace(body))
			throw new InvalidOperationException($"'{resourceName}' has no description after its frontmatter.");

		return (lines[1]["tool: ".Length..].Trim(), body);
	}

	private static string LoadText(string resourceName)
	{
		using Stream stream = typeof(ToolDocs).Assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException(
				$"Embedded resource '{resourceName}' is missing; check the EmbeddedResource globs in " +
				"Morris.Roslynk.csproj against skills/roslynk.");
		using var reader = new StreamReader(stream, Encoding.UTF8);
		return reader.ReadToEnd();
	}

	private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
