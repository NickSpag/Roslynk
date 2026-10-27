using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Builds a solution from captured compiler arguments, with no design-time build:
/// <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/> runs one per project to learn what the arguments
/// already hold (the sources, references, analyzers and options the compiler is given). What its load adds to them
/// comes from <see cref="CachedProject"/>, recorded from that load: project references in place of references to
/// other projects' outputs, the output paths, the default namespace, the documentation mode, and the analyzer
/// references it could not resolve.
/// </summary>
internal static partial class CompilerCommandLineLoad
{
	public static Solution Build(AdhocWorkspace workspace, string solutionPath, IReadOnlyList<CachedProject> projects)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(projects);

		var infos = projects
			.Select(project => (Cached: project, Info: CreateProjectInfo(project.Name, project.FilePath, Usable(project), workspace)))
			.ToArray();
		var ids = new Dictionary<(string FilePath, string Name), ProjectId>();
		foreach ((CachedProject cached, ProjectInfo info) in infos)
			ids.TryAdd((cached.FilePath, cached.Name), info.Id);

		return workspace.AddSolution(SolutionInfo.Create(
			SolutionId.CreateNewId(),
			VersionStamp.Create(),
			solutionPath,
			infos.Select(pair => AsMSBuildDescribedIt(pair.Info, pair.Cached, ids))));
	}

	/// <summary>The project the arguments describe, as the compiler sees it.</summary>
	public static ProjectInfo CreateProjectInfo(string name, string projectFile, string[] arguments, Workspace workspace) =>
		CommandLineProject.CreateProjectInfo(name, LanguageNames.CSharp, arguments, Path.GetDirectoryName(projectFile)!, workspace)
			.WithFilePath(projectFile);

	/// <summary>The paths the arguments' <c>/reference:</c> options name (<c>/reference:alias=path</c> when aliased).</summary>
	public static IEnumerable<(string Argument, string Path)> References(string[] arguments, string projectFile) =>
		OptionPaths(arguments, projectFile, ReferenceOption(), aliased: true);

	/// <summary>The paths the arguments' <c>/analyzer:</c> options name.</summary>
	public static IEnumerable<(string Argument, string Path)> Analyzers(string[] arguments, string projectFile) =>
		OptionPaths(arguments, projectFile, AnalyzerOption(), aliased: false);

	private static IEnumerable<(string Argument, string Path)> OptionPaths(string[] arguments, string projectFile, Regex option, bool aliased)
	{
		string projectDirectory = Path.GetDirectoryName(projectFile)!;
		foreach (string argument in arguments)
		{
			if (option.Match(argument) is not { Success: true } match)
				continue;

			string value = match.Groups["value"].Value;
			int equals = aliased ? value.IndexOf('=') : -1;
			string path = (equals > 0 ? value[(equals + 1)..] : value).Trim('"');
			yield return (argument, FileStamp.NormalizePath(Path.Combine(projectDirectory, path)));
		}
	}

	/// <summary>
	/// The arguments less those <see cref="CommandLineProject"/> would fail to resolve: references to other
	/// projects' outputs, which MSBuildWorkspace makes project references, and analyzers that were missing at load,
	/// which it keeps as unresolved references. Neither need exist.
	/// </summary>
	private static string[] Usable(CachedProject project)
	{
		var replaced = project.ReplacedReferences.ToHashSet(StringComparer.Ordinal);
		var unresolved = project.UnresolvedAnalyzers.ToHashSet(StringComparer.Ordinal);
		var dropped = References(project.Arguments, project.FilePath).Where(reference => replaced.Contains(reference.Path))
			.Concat(Analyzers(project.Arguments, project.FilePath).Where(analyzer => unresolved.Contains(analyzer.Path)))
			.Select(option => option.Argument)
			.ToHashSet(StringComparer.Ordinal);
		return [.. project.Arguments.Where(argument => !dropped.Contains(argument))];
	}

	[GeneratedRegex(@"^[/-](reference|r):(?<value>.+)$", RegexOptions.IgnoreCase)]
	private static partial Regex ReferenceOption();

	[GeneratedRegex(@"^[/-](analyzer|a):(?<value>.+)$", RegexOptions.IgnoreCase)]
	private static partial Regex AnalyzerOption();

	private static ProjectInfo AsMSBuildDescribedIt(ProjectInfo info, CachedProject cached, Dictionary<(string FilePath, string Name), ProjectId> ids)
	{
		info = info
			.WithOutputFilePath(cached.OutputFilePath)
			.WithOutputRefFilePath(cached.OutputRefFilePath)
			.WithCompilationOutputInfo(info.CompilationOutputInfo
				.WithAssemblyPath(cached.CompilationOutputAssemblyPath)
				.WithGeneratedFilesOutputDirectory(cached.GeneratedFilesOutputDirectory))
			.WithDefaultNamespace(cached.DefaultNamespace)
			.WithAssemblyName(cached.AssemblyName)
			.WithDocuments(info.Documents.Select(document =>
				document.FilePath is string path && cached.LinkedDocumentFolders.TryGetValue(FileStamp.NormalizePath(path), out string[]? folders)
					? document.WithFolders(folders)
					: document))
			.WithAnalyzerReferences([.. info.AnalyzerReferences, .. cached.UnresolvedAnalyzers.Select(path => new UnresolvedAnalyzerReference(path))])
			.WithProjectReferences(cached.ProjectReferences
				.Where(reference => ids.ContainsKey((reference.FilePath, reference.Name)))
				.Select(reference => new ProjectReference(ids[(reference.FilePath, reference.Name)], [.. reference.Aliases], reference.EmbedInteropTypes)));

		if (info.ParseOptions is ParseOptions parseOptions)
			info = info.WithParseOptions(parseOptions.WithDocumentationMode(cached.DocumentationMode));

		return info;
	}

	/// <summary>The project files a .sln or .slnx lists, as full paths.</summary>
	internal static IReadOnlyList<string> SolutionProjectFiles(string solutionPath)
	{
		string solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
		IEnumerable<string> paths = string.Equals(Path.GetExtension(solutionPath), ".slnx", StringComparison.OrdinalIgnoreCase)
			? XDocument.Load(solutionPath).Descendants("Project")
				.Select(project => (string?)project.Attribute("Path"))
				.OfType<string>()
			: SlnProject().Matches(File.ReadAllText(solutionPath))
				.Select(match => match.Groups["path"].Value)
				.Where(path => Path.HasExtension(path));

		return paths
			.Select(path => Path.GetFullPath(Path.Combine(solutionDirectory, path.Replace('\\', Path.DirectorySeparatorChar))))
			.Distinct(StringComparer.Ordinal)
			.ToArray();
	}

	[GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*"",\s*""(?<path>[^""]+)""", RegexOptions.Multiline)]
	private static partial Regex SlnProject();
}
