using System.Reflection;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Tools;

namespace Morris.Roslynk.Tests.Server;

public class ToolDocsTests
{
	[Fact]
	public void WhenReferenceFilesAreChecked_ThenEveryPublishedToolIsDocumented()
	{
		// The reference files are the single home of tool descriptions: a published tool without one
		// would reach callers with no contract.
		foreach (string toolName in PublishedTools().Keys)
			Assert.True(
				ToolDocs.ByTool.ContainsKey(toolName),
				$"Tool '{toolName}' has no skills/roslynk/references/tools file.");
	}

	[Fact]
	public void WhenReferenceFilesAreChecked_ThenNoFileDocumentsAnUnpublishedTool()
	{
		// A leftover file keeps publishing a description for a tool that no longer exists.
		IReadOnlyDictionary<string, MethodInfo> published = PublishedTools();
		foreach (string toolName in ToolDocs.ByTool.Keys)
			Assert.True(
				published.ContainsKey(toolName),
				$"'skills/roslynk/references/tools/{toolName}.md' documents no published tool.");
	}

	[Fact]
	public void WhenServerInstructionsAreChecked_ThenTheSharedGuidanceIsEmbedded()
	{
		Assert.StartsWith("Roslynk provides", ToolDocs.ServerInstructions, StringComparison.Ordinal);
		Assert.Contains("## Lifecycle", ToolDocs.ServerInstructions, StringComparison.Ordinal);
		Assert.Contains("## Name grammar", ToolDocs.ServerInstructions, StringComparison.Ordinal);
	}

	private static IReadOnlyDictionary<string, MethodInfo> PublishedTools()
	{
		Dictionary<string, MethodInfo> tools = new(StringComparer.Ordinal);
		foreach (Type type in typeof(ServicesRegistration).Assembly.GetTypes())
		{
			if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
				continue;

			foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
				if (method.GetCustomAttribute<McpServerToolAttribute>() is { Name: string name })
					tools.Add(name, method);
		}

		return tools;
	}
}
