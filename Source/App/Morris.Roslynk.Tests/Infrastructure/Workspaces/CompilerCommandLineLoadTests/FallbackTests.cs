using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.CompilerCommandLineLoadTests;

/// <summary>
/// A kept command line is used only while it still describes its project; otherwise the solution loads through
/// MSBuild.
/// </summary>
public class FallbackTests
{
	[Fact]
	public async Task WhenEveryProjectKeptItsCommandLine_ThenLoadAsyncUsesThem()
	{
		string solutionPath = TestSolutions.CreateScratchLinkedSolution();

		using SolutionWorkspace subject = await SolutionWorkspace.LoadAsync(solutionPath);

		Assert.True(subject.LoadedFromCompilerCommandLines);
		Project app = Assert.Single(subject.Solution.Projects, p => p.Name == "App");
		ProjectReference reference = Assert.Single(app.ProjectReferences);
		Assert.Equal("Lib(net10.0)", subject.Solution.GetProject(reference.ProjectId)?.Name);
	}

	[Fact]
	public async Task WhenNoProjectKeptItsCommandLine_ThenLoadAsyncLoadsThroughMSBuild()
	{
		using SolutionWorkspace subject = await SolutionWorkspace.LoadAsync(TestSolutions.CreateScratchSimpleSolution());

		Assert.False(subject.LoadedFromCompilerCommandLines);
		Assert.Single(subject.Solution.Projects);
	}

	[Fact]
	public void WhenOneProjectHasNoCommandLine_ThenNothingIsLoadedFromThem()
	{
		string solutionPath = TestSolutions.CreateScratchLinkedSolution();
		foreach (string file in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(solutionPath)!, "App", "obj"), "*" + CompilerCommandLineLoad.FileSuffix, SearchOption.AllDirectories))
			File.Delete(file);

		AssertFallsBack(solutionPath, "App.csproj has no kept compiler command line");
	}

	[Fact]
	public void WhenAProjectCompilesNothing_ThenItIsLeftOutRatherThanStoppingTheLoad()
	{
		// A NoTargets project runs custom targets (a package install, say) and never reaches the compiler.
		string solutionPath = TestSolutions.CreateScratchLinkedSolution();
		string directory = Path.GetDirectoryName(solutionPath)!;
		Directory.CreateDirectory(Path.Combine(directory, "Tools"));
		File.WriteAllText(Path.Combine(directory, "Tools", "Tools.csproj"), "<Project Sdk=\"Microsoft.Build.NoTargets/3.7.0\">\n</Project>\n");
		File.WriteAllText(solutionPath, File.ReadAllText(solutionPath).Replace("</Solution>", "  <Project Path=\"Tools/Tools.csproj\" />\n</Solution>"));

		using var workspace = new AdhocWorkspace();
		Solution? solution = CompilerCommandLineLoad.TryLoad(workspace, solutionPath, out string reason);

		Assert.True(solution is not null, reason);
		Assert.Equal(["App", "Lib(net10.0)", "Lib(net8.0)"], solution.Projects.Select(p => p.Name).Order(StringComparer.Ordinal));
	}

	[Fact]
	public void WhenAProjectFileChangesAfterTheBuild_ThenNothingIsLoadedFromThem()
	{
		string solutionPath = TestSolutions.CreateScratchLinkedSolution();
		Touch(Path.Combine(Path.GetDirectoryName(solutionPath)!, "Lib", "Lib.csproj"));

		AssertFallsBack(solutionPath, "Lib.csproj changed after its command line was kept");
	}

	[Fact]
	public void WhenAProjectIsCompiledAgainWithoutKeepingItsCommandLine_ThenNothingIsLoadedFromThem()
	{
		// A build without the target recompiles (an option changed, say) and leaves the old command line behind.
		string solutionPath = TestSolutions.CreateScratchLinkedSolution();
		Touch(Path.Combine(Path.GetDirectoryName(solutionPath)!, "App", "obj", "Debug", "net10.0", "App.dll"));

		AssertFallsBack(solutionPath, "App.csproj was compiled after its command line was kept");
	}

	[Fact]
	public void WhenASourceFileIsAddedAfterTheBuild_ThenNothingIsLoadedFromThem()
	{
		string solutionPath = TestSolutions.CreateScratchLinkedSolution();
		string added = Path.Combine(Path.GetDirectoryName(solutionPath)!, "Lib", "Farewell.cs");
		File.WriteAllText(added, "namespace Linked.Library;\n\npublic static class Farewell;\n");
		Touch(added);

		AssertFallsBack(solutionPath, "Farewell.cs was added to Lib.csproj after its command line was kept");
	}

	[Fact]
	public void WhenAnEditedSourceFileIsNewerThanTheBuild_ThenTheCommandLinesAreStillUsed()
	{
		// An edit changes a document's text, which the workspace reads from disk; it does not change the project.
		string solutionPath = TestSolutions.CreateScratchLinkedSolution();
		Touch(Path.Combine(Path.GetDirectoryName(solutionPath)!, "Lib", "Greeter.cs"));

		using var workspace = new AdhocWorkspace();
		Assert.NotNull(CompilerCommandLineLoad.TryLoad(workspace, solutionPath, out string reason));
		Assert.Equal("", reason);
	}

	[Fact]
	public void WhenAListedSourceFileIsDeleted_ThenNothingIsLoadedFromThem()
	{
		string solutionPath = TestSolutions.CreateScratchLinkedSolution();
		File.Delete(Path.Combine(Path.GetDirectoryName(solutionPath)!, "Lib", "Greeter.cs"));

		AssertFallsBack(solutionPath, "Greeter.cs in Lib.csproj was deleted after its command line was kept");
	}

	[Fact]
	public void WhenTheSolutionIsASlnFile_ThenItsProjectFilesAreListed()
	{
		string directory = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		string solutionPath = Path.Combine(directory, "Classic.sln");
		File.WriteAllText(solutionPath, """
			Microsoft Visual Studio Solution File, Format Version 12.00
			Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
			EndProject
			Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{22222222-2222-2222-2222-222222222222}"
			EndProject
			Project("{F2A71F9B-5D33-465A-A702-920D77279786}") = "Tool", "tools\Tool\Tool.fsproj", "{33333333-3333-3333-3333-333333333333}"
			EndProject
			""");

		IReadOnlyList<string> projects = CompilerCommandLineLoad.SolutionProjectFiles(solutionPath);

		Assert.Equal(
			[Path.Combine(directory, "src", "App", "App.csproj"), Path.Combine(directory, "tools", "Tool", "Tool.fsproj")],
			projects);
	}

	private static void AssertFallsBack(string solutionPath, string expectedReason)
	{
		using var workspace = new AdhocWorkspace();

		Solution? solution = CompilerCommandLineLoad.TryLoad(workspace, solutionPath, out string reason);

		Assert.Null(solution);
		Assert.Equal(expectedReason, reason);
	}

	/// <summary>Moves a file's time past everything the build wrote.</summary>
	private static void Touch(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
}
