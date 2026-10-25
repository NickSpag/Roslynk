using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.CompilerCommandLineLoadTests;

/// <summary>
/// A solution built from kept compiler command lines describes each project as MSBuildWorkspace's design-time
/// build does: the same projects, files, references, analyzers and options.
/// </summary>
public class SameAsMSBuildTests
{
	[Theory]
	[InlineData("SimpleSolution", "SimpleSolution.slnx")]
	[InlineData("GeneratorSolution", "GeneratorSolution.slnx")]
	[InlineData("RazorSolution", "RazorSolution.slnx")]
	[InlineData("CshtmlSolution", "CshtmlSolution.slnx")]
	[InlineData("CodeStyleSolution", "CodeStyleSolution.slnx")]
	[InlineData("ConditionalSolution", "ConditionalSolution.slnx")]
	public async Task WhenEveryProjectOfAFixtureKeptItsCommandLine_ThenTheSolutionMatchesMSBuildsLoad(string fixture, string solutionFile) =>
		await AssertSameAsMSBuildAsync(TestSolutions.CreateScratchBuiltWithCompilerCommandLines(fixture, solutionFile));

	[Fact]
	public async Task WhenAProjectReferencesAMultiTargetedOne_ThenTheReferenceAndEachFrameworkMatchMSBuildsLoad() =>
		await AssertSameAsMSBuildAsync(TestSolutions.CreateScratchLinkedSolution());

	private static async Task AssertSameAsMSBuildAsync(string solutionPath)
	{
		using var adhoc = new AdhocWorkspace();
		Solution? fromCommandLines = CompilerCommandLineLoad.TryLoad(adhoc, solutionPath, out string reason);
		Assert.True(fromCommandLines is not null, reason);

		MsBuildRegistrar.EnsureRegistered();
		using MSBuildWorkspace msbuild = MSBuildWorkspace.Create();
		Solution fromMSBuild = await msbuild.OpenSolutionAsync(solutionPath);

		string[] expected = Describe(fromMSBuild);
		string[] actual = Describe(fromCommandLines);
		string[] differences = [.. expected.Except(actual).Select(l => $"only MSBuild:       {l}"), .. actual.Except(expected).Select(l => $"only command lines: {l}")];
		Assert.True(differences.Length == 0, string.Join('\n', differences));
	}

	/// <summary>Every project, one line per fact, in a stable order, so a difference reads as a diff.</summary>
	private static string[] Describe(Solution solution)
	{
		var lines = new List<string>();
		foreach (Project project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp).OrderBy(p => p.Name, StringComparer.Ordinal))
		{
			string p = project.Name;
			lines.Add($"{p} assembly {project.AssemblyName}");
			lines.Add($"{p} file {project.FilePath}");
			lines.Add($"{p} output {project.OutputFilePath}");
			lines.Add($"{p} ref output {project.OutputRefFilePath}");
			lines.Add($"{p} compilation output {project.CompilationOutputInfo.AssemblyPath}");
			lines.Add($"{p} generated files {project.CompilationOutputInfo.GeneratedFilesOutputDirectory}");
			lines.Add($"{p} default namespace {project.DefaultNamespace}");
			lines.AddRange(project.Documents.Select(d => $"{p} document {d.FilePath}"));
			lines.AddRange(project.AdditionalDocuments.Select(d => $"{p} additional {d.FilePath}"));
			lines.AddRange(project.AnalyzerConfigDocuments.Select(d => $"{p} analyzer config {d.FilePath}"));
			lines.AddRange(project.MetadataReferences.OfType<PortableExecutableReference>().Select(r => $"{p} reference {r.FilePath} {string.Join(',', r.Properties.Aliases)}"));
			lines.AddRange(project.ProjectReferences.Select(r => $"{p} project reference {solution.GetProject(r.ProjectId)?.Name} {string.Join(',', r.Aliases)}"));
			lines.AddRange(project.AnalyzerReferences.Select(r => $"{p} analyzer {r.FullPath}"));
			if (project.ParseOptions is CSharpParseOptions parse)
			{
				lines.Add($"{p} language version {parse.SpecifiedLanguageVersion}");
				lines.AddRange(parse.PreprocessorSymbolNames.Select(s => $"{p} define {s}"));
				lines.AddRange(parse.Features.Select(f => $"{p} feature {f.Key}={f.Value}"));
				lines.Add($"{p} documentation mode {parse.DocumentationMode}");
			}

			if (project.CompilationOptions is CSharpCompilationOptions compilation)
			{
				lines.Add($"{p} output kind {compilation.OutputKind}");
				lines.Add($"{p} nullable {compilation.NullableContextOptions}");
				lines.Add($"{p} unsafe {compilation.AllowUnsafe}");
				lines.Add($"{p} platform {compilation.Platform}");
				lines.Add($"{p} checked {compilation.CheckOverflow}");
				lines.Add($"{p} warning level {compilation.WarningLevel}");
				lines.Add($"{p} general diagnostics {compilation.GeneralDiagnosticOption}");
				lines.AddRange(compilation.SpecificDiagnosticOptions.Select(o => $"{p} diagnostic {o.Key}={o.Value}"));
			}
		}

		lines.Sort(StringComparer.Ordinal);
		return [.. lines];
	}
}
