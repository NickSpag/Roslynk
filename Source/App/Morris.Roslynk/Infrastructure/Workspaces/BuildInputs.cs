using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// What a solution's load depended on: files whose content counts (<see cref="Files"/>), paths whose existence
/// counts (<see cref="Probes"/>: a <c>Directory.Build.props</c> added above a project changes what it imports), and
/// environment variables, which MSBuild reads as properties. When the load may depend on something these cannot
/// capture, <see cref="Uncertain"/> says what, and the load is not cached.
/// </summary>
internal sealed partial record BuildInputs(
	IReadOnlyCollection<string> Files,
	IReadOnlyCollection<string> Probes,
	IReadOnlyCollection<string> EnvironmentVariables,
	string? Uncertain)
{
	/// <summary>Files MSBuild, the SDK and NuGet look for in each folder above a project, read only where they exist.</summary>
	private static readonly string[] AncestorFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"];

	/// <summary>Environment variables that steer MSBuild, SDK selection or restore without a build file naming them.</summary>
	private static readonly string[] AlwaysRecordedEnvironment =
	[
		"MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBuildUserExtensionsPath", "MSBUILD_EXE_PATH",
		"DOTNET_ROOT", "DOTNET_HOST_PATH", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER",
		"NUGET_PACKAGES",
	];

	/// <summary>The names each SDK folder's build files mention, scanned once per process.</summary>
	private static readonly ConcurrentDictionary<string, string[]> SdkNames = new(StringComparer.Ordinal);

	/// <summary>
	/// The inputs of a load, named from the files rather than from MSBuild, which evaluates in its own process:
	/// <list type="bullet">
	/// <item>the solution file and each project file;</item>
	/// <item>every <c>Directory.Build.props</c>/<c>.targets</c>, <c>Directory.Packages.props</c> and <c>global.json</c>
	/// above a project, and whether one exists where there is none;</item>
	/// <item>what those files <c>&lt;Import&gt;</c> by a path that can be worked out from the text;</item>
	/// <item>restore's output: <c>project.assets.json</c> and NuGet's <c>.nuget.g.props</c>/<c>.targets</c> beside it,
	/// which import the packages' build files by version, so a changed package changes them;</item>
	/// <item>every environment variable named as <c>$(Name)</c> or <c>GetEnvironmentVariable('Name')</c> in those files,
	/// in the packages' build files they import, or in the SDK's own (<paramref name="sdkFolders"/>), plus
	/// <see cref="AlwaysRecordedEnvironment"/>. A variable is recorded whether it is set or not, so setting one counts.</item>
	/// </list>
	/// The load is uncertain when one of the solution's own files imports a path that depends on more than its text,
	/// or reads files (<c>ReadLinesFromFile</c>, <c>XmlPeek</c>, <c>JsonPeek</c>, <c>System.IO.File</c>), which nothing
	/// records. The SDK's own imports are covered by the SDK selection the cache records, and packages' by their
	/// version.
	/// </summary>
	public static BuildInputs Of(string solutionPath, IEnumerable<CapturedArguments> captures, IEnumerable<string> sdkFolders)
	{
		var files = new SortedSet<string>(StringComparer.Ordinal) { FileStamp.NormalizePath(solutionPath) };
		var probes = new SortedSet<string>(StringComparer.Ordinal);
		var environment = new SortedSet<string>(AlwaysRecordedEnvironment, StringComparer.Ordinal);
		var packageFiles = new SortedSet<string>(StringComparer.Ordinal);
		var pending = new Queue<(string File, string ProjectFile)>();

		void Add(string path, string projectFile, bool parse)
		{
			probes.Add(path);
			if (File.Exists(path) && files.Add(path) && parse)
				pending.Enqueue((path, projectFile));
		}

		foreach (string probe in Ancestors(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!, "global.json"))
			Add(probe, solutionPath, parse: false);

		foreach (CapturedArguments captured in captures.DistinctBy(c => c.ProjectFile, StringComparer.Ordinal))
		{
			string projectFile = captured.ProjectFile;
			Add(projectFile, projectFile, parse: true);
			foreach (string name in AncestorFiles)
			{
				foreach (string probe in Ancestors(Path.GetDirectoryName(projectFile)!, name))
					Add(probe, projectFile, parse: name != "global.json");
			}

			string restoreFolder = captured.ProjectAssetsFile is string assets
				? Path.GetDirectoryName(assets)!
				: Path.Combine(Path.GetDirectoryName(projectFile)!, "obj");
			Add(captured.ProjectAssetsFile ?? Path.Combine(restoreFolder, "project.assets.json"), projectFile, parse: false);
			foreach (string extension in (string[])[".nuget.g.props", ".nuget.g.targets"])
			{
				string generated = Path.Combine(restoreFolder, Path.GetFileName(projectFile) + extension);
				Add(generated, projectFile, parse: false);
				foreach (string package in PackageImports(generated))
					packageFiles.Add(package);
			}
		}

		while (pending.TryDequeue(out (string File, string ProjectFile) next))
		{
			if (Parse(next.File) is not (XDocument document, string text))
				return new BuildInputs(files, probes, environment, $"{next.File} could not be read");

			if (ReadsFiles(text) is string how)
				return new BuildInputs(files, probes, environment, $"{next.File} reads files ({how}), which the cache cannot track");

			environment.UnionWith(Names(text));
			foreach (string project in Imports(document))
			{
				switch (Resolve(project, next.File, next.ProjectFile))
				{
					case Resolved.Covered:
						break;
					case Resolved.Path { Value: string path }:
						Add(path, next.ProjectFile, parse: true);
						break;
					default:
						return new BuildInputs(files, probes, environment, $"{next.File} imports '{project}', whose path depends on more than its text");
				}
			}
		}

		// Packages' build files are fixed per version, which the restore output records; only the variables they read
		// are taken from them, following their own imports where the path can be worked out.
		var scanned = new HashSet<string>(StringComparer.Ordinal);
		var packagePending = new Queue<string>(packageFiles);
		while (packagePending.TryDequeue(out string? packageFile))
		{
			if (!scanned.Add(packageFile) || Parse(packageFile) is not (XDocument document, string text))
				continue;

			environment.UnionWith(Names(text));
			foreach (string project in Imports(document))
			{
				if (Resolve(project, packageFile, packageFile) is Resolved.Path { Value: string path } && File.Exists(path))
					packagePending.Enqueue(path);
			}
		}

		foreach (string sdkFolder in sdkFolders)
			environment.UnionWith(SdkNames.GetOrAdd(sdkFolder, ScanFolder));

		return new BuildInputs(files, probes, environment, null);
	}

	/// <summary>The path of a file with this name in the folder and in every folder above it.</summary>
	private static IEnumerable<string> Ancestors(string folder, string name)
	{
		for (DirectoryInfo? directory = new(folder); directory is not null; directory = directory.Parent)
			yield return Path.Combine(directory.FullName, name);
	}

	private static (XDocument Document, string Text)? Parse(string file)
	{
		try
		{
			string text = File.ReadAllText(file);
			return (XDocument.Parse(text), text);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
		{
			return null;
		}
	}

	/// <summary>The <c>Project</c> of each <c>Import</c> that is not an SDK's own; an SDK's files come with the SDK.</summary>
	private static IEnumerable<string> Imports(XDocument document) =>
		document.Descendants()
			.Where(element => element.Name.LocalName == "Import" && element.Attribute("Sdk") is null)
			.Select(element => (string?)element.Attribute("Project") ?? "");

	/// <summary>The package build files NuGet's generated import file imports, with <c>$(NuGetPackageRoot)</c> resolved.</summary>
	private static IEnumerable<string> PackageImports(string generated)
	{
		if (Parse(generated) is not (XDocument document, _))
			return [];

		string? root = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "NuGetPackageRoot")?.Value;
		if (string.IsNullOrEmpty(root))
			return [];

		return Imports(document)
			.Where(project => project.StartsWith("$(NuGetPackageRoot)", StringComparison.OrdinalIgnoreCase))
			.Select(project => Path.GetFullPath(Path.Combine(root, project["$(NuGetPackageRoot)".Length..].Replace('\\', Path.DirectorySeparatorChar))))
			.Where(File.Exists);
	}

	/// <summary>The names of the SDK's build files, which can read any environment variable as a property.</summary>
	private static string[] ScanFolder(string folder)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		if (!Directory.Exists(folder))
			return [];

		foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
		{
			if (Path.GetExtension(file).ToLowerInvariant() is not (".props" or ".targets" or ".tasks" or ".proj"))
				continue;

			try
			{
				names.UnionWith(Names(File.ReadAllText(file)));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}

		return [.. names];
	}

	private static IEnumerable<string> Names(string text) =>
		PropertyReference().Matches(text).Select(match => match.Groups["name"].Value)
			.Concat(EnvironmentRead().Matches(text).Select(match => match.Groups["name"].Value));

	private static string? ReadsFiles(string text)
	{
		foreach (string marker in (string[])["ReadLinesFromFile", "XmlPeek", "JsonPeek", "System.IO.File]::"])
		{
			if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
				return marker.TrimEnd(']', ':');
		}

		return null;
	}

	private abstract record Resolved
	{
		public sealed record Path(string Value) : Resolved;

		public sealed record Covered : Resolved;

		public sealed record Unknown : Resolved;
	}

	/// <summary>
	/// The file an <c>Import</c> names, worked out from <c>$(MSBuildThisFileDirectory)</c>,
	/// <c>$(MSBuildProjectDirectory)</c> and relative paths. <c>GetPathOfFileAbove</c> for one of the files recorded
	/// in every folder above the project is already covered; anything else with a property, item or wildcard left in
	/// it is unknown.
	/// </summary>
	private static Resolved Resolve(string project, string importingFile, string projectFile)
	{
		Match above = PathOfFileAbove().Match(project);
		if (above.Success)
			return AncestorFiles.Contains(above.Groups["name"].Value, StringComparer.OrdinalIgnoreCase) ? new Resolved.Covered() : new Resolved.Unknown();

		string thisDirectory = System.IO.Path.GetDirectoryName(importingFile)! + System.IO.Path.DirectorySeparatorChar;
		string projectDirectory = System.IO.Path.GetDirectoryName(projectFile)!;
		string path = project
			.Replace("$(MSBuildThisFileDirectory)", thisDirectory, StringComparison.OrdinalIgnoreCase)
			.Replace("$(MSBuildProjectDirectory)", projectDirectory, StringComparison.OrdinalIgnoreCase)
			.Trim();

		if (path.Length == 0 || path.Contains("$(", StringComparison.Ordinal) || path.Contains("@(", StringComparison.Ordinal)
			|| path.Contains("%(", StringComparison.Ordinal) || path.IndexOfAny(['*', '?', ';']) >= 0)
			return new Resolved.Unknown();

		return new Resolved.Path(System.IO.Path.GetFullPath(path.Replace('\\', System.IO.Path.DirectorySeparatorChar), System.IO.Path.GetDirectoryName(importingFile)!));
	}

	[GeneratedRegex(@"\$\((?<name>[A-Za-z_][A-Za-z0-9_\-]*)\)")]
	private static partial Regex PropertyReference();

	[GeneratedRegex(@"GetEnvironmentVariable\(\s*['""](?<name>[^'""]+)['""]")]
	private static partial Regex EnvironmentRead();

	[GeneratedRegex(@"^\s*\$\(\[MSBuild\]::GetPathOfFileAbove\(\s*'(?<name>[^']+)'\s*(,\s*'[^']*'\s*)?\)\)\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex PathOfFileAbove();
}
