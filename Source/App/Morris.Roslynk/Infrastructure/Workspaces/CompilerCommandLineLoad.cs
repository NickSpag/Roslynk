using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Builds a solution from the compiler command lines a build kept beside each project's outputs, with no
/// design-time build. <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/> runs a design-time build of
/// every project to learn what this file already holds: the sources, references, analyzers and options the
/// compiler was given.
/// <para>
/// A build keeps it with a target after <c>CoreCompile</c> that writes <c>@(CscCommandLineArgs)</c> (with
/// <c>ProvideCommandLineArgs=true</c>), one argument per line, to
/// <c>$(IntermediateOutputPath)$(MSBuildProjectFile).csc.rsp</c>: <c>obj/Debug/&lt;framework&gt;/&lt;project
/// file&gt;.csc.rsp</c>. Only the Debug configuration is read, as that is what MSBuildWorkspace loads when no
/// configuration is given.
/// </para>
/// <para>
/// All or nothing: the solution is built this way only when every C# project in it has a current file for each of
/// its target frameworks; otherwise <see cref="TryLoad"/> returns null with the first reason, and the caller loads
/// through MSBuildWorkspace. A file is current when it is not older than the assembly compiled beside it (every
/// compile rewrites both), the project file, the <c>Directory.*.props</c>/<c>.targets</c> above it and its
/// <c>project.assets.json</c>, and when no C# file in the project's folder that it does not list is newer than it
/// (a file added since the build). A project whose solution entry is not a C# project file (VB, F#) also falls
/// back; JavaScript and other projects MSBuildWorkspace cannot open are left out, as it leaves them out, and so are
/// projects whose SDK compiles nothing (NoTargets, Traversal).
/// </para>
/// </summary>
internal static partial class CompilerCommandLineLoad
{
	public const string FileSuffix = ".csc.rsp";

	private const string Configuration = "Debug";

	private static readonly string[] UnsupportedProjectExtensions = [".vbproj", ".fsproj"];

	private static readonly string[] IgnoredFolders = ["bin", "obj", "node_modules"];

	/// <summary>
	/// The solution built from the kept command lines in <paramref name="workspace"/>, or null with the reason
	/// the caller should load through MSBuild.
	/// </summary>
	public static Solution? TryLoad(AdhocWorkspace workspace, string solutionPath, out string reason)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(solutionPath);

		IReadOnlyList<string> projectFiles;
		try
		{
			projectFiles = SolutionProjectFiles(solutionPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
		{
			reason = $"the solution file could not be read ({exception.Message})";
			return null;
		}

		var commandLines = new List<(string ProjectFile, string Framework, string CommandLine, bool MultiTargeted)>();
		foreach (string projectFile in projectFiles)
		{
			string extension = Path.GetExtension(projectFile);
			if (UnsupportedProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
			{
				reason = $"{Path.GetFileName(projectFile)} is not a C# project";
				return null;
			}

			if (!string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) || CompilesNothing(projectFile))
				continue;

			string configurationFolder = Path.Combine(Path.GetDirectoryName(projectFile)!, "obj", Configuration);
			string fileName = Path.GetFileName(projectFile) + FileSuffix;
			string[] found = Directory.Exists(configurationFolder)
				? Directory.GetDirectories(configurationFolder)
					.Select(framework => Path.Combine(framework, fileName))
					.Where(File.Exists)
					.Order(StringComparer.Ordinal)
					.ToArray()
				: [];
			if (found.Length == 0)
			{
				reason = $"{Path.GetFileName(projectFile)} has no kept compiler command line";
				return null;
			}

			foreach (string commandLine in found)
				commandLines.Add((projectFile, Path.GetFileName(Path.GetDirectoryName(commandLine)!), commandLine, found.Length > 1));
		}

		if (commandLines.Count == 0)
		{
			reason = "the solution has no C# projects";
			return null;
		}

		var infos = new List<ProjectInfo>();
		foreach ((string projectFile, string framework, string commandLine, bool multiTargeted) in commandLines)
		{
			string projectDirectory = Path.GetDirectoryName(projectFile)!;
			string[] arguments = File.ReadAllLines(commandLine);
			string name = Path.GetFileNameWithoutExtension(projectFile);
			ProjectInfo info = CommandLineProject.CreateProjectInfo(
				multiTargeted ? $"{name}({framework})" : name, LanguageNames.CSharp, arguments, projectDirectory, workspace);

			if (Stale(info, projectFile, commandLine) is string staleness)
			{
				reason = staleness;
				return null;
			}

			infos.Add(info.WithFilePath(projectFile));
		}

		reason = "";
		return workspace.AddSolution(SolutionInfo.Create(
			SolutionId.CreateNewId(), VersionStamp.Create(), solutionPath, Link(infos).Select(AsMSBuildDescribesIt)));
	}

	/// <summary>
	/// Turns each reference to another project's output into a project reference, as MSBuildWorkspace does, so
	/// symbols resolve to that project's source. References are matched by full path against the outputs each
	/// project's command line names: its assembly, the reference assembly (<c>/refout:</c>, under <c>refint/</c>)
	/// and the copy projects compile against (<c>ref/</c>).
	/// </summary>
	private static List<ProjectInfo> Link(List<ProjectInfo> infos)
	{
		var producers = new Dictionary<string, ProjectId>(StringComparer.Ordinal);
		foreach (ProjectInfo info in infos)
		{
			foreach (string output in Outputs(info))
				producers.TryAdd(output, info.Id);
		}

		var linked = new List<ProjectInfo>(infos.Count);
		foreach (ProjectInfo info in infos)
		{
			var projectReferences = new List<ProjectReference>();
			var metadataReferences = new List<MetadataReference>();
			foreach (MetadataReference reference in info.MetadataReferences)
			{
				if (reference is PortableExecutableReference { FilePath: string path }
					&& producers.TryGetValue(Path.GetFullPath(path), out ProjectId? producer)
					&& producer != info.Id)
				{
					if (!projectReferences.Any(existing => existing.ProjectId == producer))
						projectReferences.Add(new ProjectReference(producer, reference.Properties.Aliases, reference.Properties.EmbedInteropTypes));
				}
				else
				{
					metadataReferences.Add(reference);
				}
			}

			linked.Add(info.WithProjectReferences(projectReferences).WithMetadataReferences(metadataReferences));
		}

		return linked;
	}

	private static IEnumerable<string> Outputs(ProjectInfo info)
	{
		if (info.OutputFilePath is string assembly)
		{
			string full = Path.GetFullPath(assembly);
			yield return full;
			yield return Path.Combine(Path.GetDirectoryName(full)!, "ref", Path.GetFileName(full));
			if (BinCopy(info, full) is string copy)
				yield return copy;
		}

		if (info.OutputRefFilePath is string referenceAssembly)
			yield return Path.GetFullPath(referenceAssembly);
	}

	/// <summary>
	/// The project as MSBuildWorkspace describes it, where its design-time build knows more than the compiler is
	/// told: the output path is the copy in bin/ (the target path; the command line names obj/), the reference
	/// assembly is the copy under ref/ (the command line names refint/), the default namespace is the project's
	/// RootNamespace (from the SDK's generated .editorconfig, which the compiler reads), and documentation comments
	/// are parsed even without a documentation file.
	/// </summary>
	private static ProjectInfo AsMSBuildDescribesIt(ProjectInfo info)
	{
		if (info.OutputFilePath is string assembly && BinCopy(info, Path.GetFullPath(assembly)) is string copy)
			info = info.WithOutputFilePath(copy);

		if (info.OutputRefFilePath is string referenceAssembly)
		{
			string full = Path.GetFullPath(referenceAssembly);
			string directory = Path.GetDirectoryName(full)!;
			string compiledAgainst = Path.Combine(Path.GetDirectoryName(directory)!, "ref", Path.GetFileName(full));
			if (string.Equals(Path.GetFileName(directory), "refint", StringComparison.OrdinalIgnoreCase) && File.Exists(compiledAgainst))
				info = info.WithOutputRefFilePath(compiledAgainst);
		}

		if (RootNamespace(info) is string rootNamespace)
			info = info.WithDefaultNamespace(rootNamespace);

		if (info.ParseOptions is { DocumentationMode: DocumentationMode.None } parseOptions)
			info = info.WithParseOptions(parseOptions.WithDocumentationMode(DocumentationMode.Parse));

		return info;
	}

	/// <summary>
	/// The copy of an assembly the build put in bin/: obj/&lt;configuration&gt;/&lt;framework&gt;/ under the
	/// project's folder mirrored under bin/, when it exists.
	/// </summary>
	private static string? BinCopy(ProjectInfo info, string assembly)
	{
		if (info.FilePath is not string projectFile)
			return null;

		string projectDirectory = Path.GetDirectoryName(projectFile)!;
		string relative = Path.GetRelativePath(Path.Combine(projectDirectory, "obj"), assembly);
		if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
			return null;

		string copy = Path.Combine(projectDirectory, "bin", relative);
		return File.Exists(copy) ? copy : null;
	}

	/// <summary>The project's RootNamespace, as the SDK writes it into the .editorconfig it generates for the compiler.</summary>
	private static string? RootNamespace(ProjectInfo info)
	{
		foreach (DocumentInfo config in info.AnalyzerConfigDocuments)
		{
			if (config.FilePath is not string path
				|| !path.EndsWith(".GeneratedMSBuildEditorConfig.editorconfig", StringComparison.OrdinalIgnoreCase)
				|| !File.Exists(path))
				continue;

			foreach (string line in File.ReadLines(path))
			{
				int equals = line.IndexOf('=');
				if (equals > 0 && line[..equals].Trim() == "build_property.RootNamespace")
					return line[(equals + 1)..].Trim();
			}
		}

		return null;
	}

	/// <summary>Why the command line no longer describes the project, or null when it does.</summary>
	private static string? Stale(ProjectInfo info, string projectFile, string commandLine)
	{
		DateTime written = File.GetLastWriteTimeUtc(commandLine);
		string name = Path.GetFileName(projectFile);

		if (info.OutputFilePath is not string assembly || !File.Exists(assembly))
			return $"{name} has not been compiled since its command line was kept";

		if (File.GetLastWriteTimeUtc(assembly) > written)
			return $"{name} was compiled after its command line was kept";

		string projectDirectory = Path.GetDirectoryName(projectFile)!;
		foreach (string input in BuildInputs(projectFile))
		{
			if (File.GetLastWriteTimeUtc(input) > written)
				return input == projectFile
					? $"{name} changed after its command line was kept"
					: $"{Path.GetFileName(input)} changed after {name}'s command line was kept";
		}

		var listed = info.Documents
			.Select(document => document.FilePath)
			.OfType<string>()
			.Select(Path.GetFullPath)
			.ToHashSet(StringComparer.Ordinal);
		foreach (string listedSource in listed)
		{
			if (!File.Exists(listedSource))
				return $"{Path.GetFileName(listedSource)} in {name} was deleted after its command line was kept";
		}

		foreach (string source in SourcesOnDisk(projectDirectory))
		{
			if (!listed.Contains(source) && File.GetLastWriteTimeUtc(source) > written)
				return $"{Path.GetFileName(source)} was added to {name} after its command line was kept";
		}

		return null;
	}

	/// <summary>The project file, the Directory.*.props/.targets MSBuild imports above it, and its restore output.</summary>
	private static IEnumerable<string> BuildInputs(string projectFile)
	{
		yield return projectFile;

		string assets = Path.Combine(Path.GetDirectoryName(projectFile)!, "obj", "project.assets.json");
		if (File.Exists(assets))
			yield return assets;

		for (DirectoryInfo? directory = new FileInfo(projectFile).Directory; directory is not null; directory = directory.Parent)
		{
			foreach (string name in (string[])["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"])
			{
				string candidate = Path.Combine(directory.FullName, name);
				if (File.Exists(candidate))
					yield return candidate;
			}
		}
	}

	/// <summary>The C# files under a project's folder, less build output and the folders the SDK's default globs leave out.</summary>
	private static IEnumerable<string> SourcesOnDisk(string projectDirectory)
	{
		var pending = new Stack<string>([projectDirectory]);
		while (pending.Count > 0)
		{
			string directory = pending.Pop();
			foreach (string file in Directory.EnumerateFiles(directory, "*.cs"))
				yield return Path.GetFullPath(file);

			foreach (string child in Directory.EnumerateDirectories(directory))
			{
				string name = Path.GetFileName(child);
				if (name.StartsWith('.') || IgnoredFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
					continue;
				pending.Push(child);
			}
		}
	}

	/// <summary>
	/// Whether the project's SDK compiles nothing (<c>Microsoft.Build.NoTargets</c>, which runs custom targets, and
	/// <c>Microsoft.Build.Traversal</c>, which builds other projects), so it has no command line and no C# to load.
	/// </summary>
	private static bool CompilesNothing(string projectFile)
	{
		if (!File.Exists(projectFile))
			return false;

		XElement root = XDocument.Load(projectFile).Root!;
		IEnumerable<string> sdks = ((string?)root.Attribute("Sdk") ?? "").Split(';')
			.Concat(root.Elements("Sdk").Select(sdk => (string?)sdk.Attribute("Name") ?? ""));
		return sdks.Any(sdk =>
			sdk.Trim().StartsWith("Microsoft.Build.NoTargets", StringComparison.OrdinalIgnoreCase)
			|| sdk.Trim().StartsWith("Microsoft.Build.Traversal", StringComparison.OrdinalIgnoreCase));
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
