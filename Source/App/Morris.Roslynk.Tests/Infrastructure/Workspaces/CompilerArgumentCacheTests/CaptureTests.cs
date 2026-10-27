using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.CompilerArgumentCacheTests;

public class CaptureTests
{
	[Fact]
	public async Task WhenTheSolutionSetsCustomAfterMicrosoftCommonTargets_ThenItsTargetsStillRunAndNothingIsCached()
	{
		string solutionPath = TestSolutions.CreateScratchLinkedSolution();
		string directory = Path.GetDirectoryName(solutionPath)!;
		File.WriteAllText(
			Path.Combine(directory, "Own.targets"),
			"<Project><PropertyGroup><DefineConstants>$(DefineConstants);FROM_OWN_TARGETS</DefineConstants></PropertyGroup></Project>");
		File.WriteAllText(
			Path.Combine(directory, "Directory.Build.props"),
			"<Project><PropertyGroup><CustomAfterMicrosoftCommonTargets>$(MSBuildThisFileDirectory)Own.targets</CustomAfterMicrosoftCommonTargets></PropertyGroup></Project>");
		CompilerArgumentCache cache = TestCaches.New();

		using SolutionWorkspace first = await SolutionWorkspace.LoadAsync(solutionPath, cache);
		Assert.EndsWith("Directory.Build.props sets CustomAfterMicrosoftCommonTargets", cache.LastStoreReason, StringComparison.Ordinal);
		using SolutionWorkspace second = await SolutionWorkspace.LoadAsync(solutionPath, cache);

		Assert.False(second.LoadedFromCache);
		foreach (Project project in second.Solution.Projects)
			Assert.Contains("FROM_OWN_TARGETS", ((CSharpParseOptions)project.ParseOptions!).PreprocessorSymbolNames);
	}

	[Fact]
	public async Task WhenTheSolutionLoadsFromTheCache_ThenInactiveBranchesAreStillProjected()
	{
		string solutionPath = TestSolutions.CreateScratchConditionalSolution();
		using (var first = new InstanceRegistry())
		{
			RoslynInstance loaded = await first.GetOrAddAsync(solutionPath);
			Assert.False(loaded.Workspace?.LoadedFromCache);
		}

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		Assert.True(instance.Workspace?.LoadedFromCache, CompilerArgumentCache.Default.LastLoadReason);

		IReadOnlyList<Projection> projections = await new ProjectionService().BuildAsync(instance.Workspace!.Solution);
		Assert.Contains(projections, projection => projection.Solution.Projects.All(project => !((CSharpParseOptions)project.ParseOptions!).PreprocessorSymbolNames.Contains("DEBUG")));

		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());
		string result = await subject.FindReferences(solutionPath, "ConditionalLib.Target.Ping");

		// The #if DEBUG call is active and the #else call is disabled text; the !DEBUG projection reports both.
		Assert.Contains("method,Run,", result);
		Assert.Equal(2, result.Split('\n').Single(line => line.TrimStart('\t').StartsWith("method,Run,", StringComparison.Ordinal)).Split(',')[2].Split('|').Length);
	}
}
