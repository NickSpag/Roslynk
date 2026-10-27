using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// What a project's evaluation depended on, as far as the cache can tell: files whose content counts
/// (<see cref="Files"/>), paths whose existence counts (<see cref="Probes"/>: a <c>Directory.Build.props</c> added
/// above the project changes what it imports), and environment variables, which MSBuild reads as properties. A
/// project whose inputs cannot all be named has an <see cref="Uncertain"/> reason and is not cached.
/// </summary>
internal sealed record BuildInputs(
	IReadOnlyCollection<string> Files,
	IReadOnlyCollection<string> Probes,
	IReadOnlyCollection<string> EnvironmentVariables,
	string? Uncertain);

/// <summary>
/// Names a project's build inputs from its files, without MSBuild: the project file, every
/// <c>Directory.Build.props</c>/<c>.targets</c>, <c>Directory.Packages.props</c> and <c>global.json</c> above it,
/// the restore output its evaluation imported (<c>project.assets.json</c> and NuGet's generated
/// <c>.nuget.g.props</c>/<c>.targets</c>, which import the packages' own build files, fixed per package version),
/// and what those files <c>&lt;Import&gt;</c> by a path that can be worked out from the text. The SDK's own imports
/// are covered by the SDK selection the cache records. Environment variables are those the named files mention as
/// <c>$(Name)</c>, plus <see cref="AlwaysRecordedEnvironment"/>.
/// <para>
/// It cannot see what the SDK's or a package's targets read from the environment beyond that list, nor an import
/// whose path depends on a property it cannot resolve; the latter makes the project uncertain.
/// </para>
/// </summary>
internal static partial class NamedBuildInputs
{
	/// <summary>Files MSBuild, the SDK and NuGet look for in each folder above a project.</summary>
	private static readonly string[] AncestorFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"];

	/// <summary>Environment variables that steer MSBuild, SDK selection or restore, and common properties that may be set from the environment.</summary>
	internal static readonly string[] AlwaysRecordedEnvironment =
	[
		"Configuration", "Platform", "TargetFramework", "TargetFrameworks", "RuntimeIdentifier", "LangVersion", "Nullable",
		"DefineConstants", "TreatWarningsAsErrors", "WarningsAsErrors", "NoWarn",
		"MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBuildUserExtensionsPath", "MSBUILD_EXE_PATH",
		"DOTNET_ROOT", "DOTNET_HOST_PATH", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER",
		"NUGET_PACKAGES",
		"CustomBeforeMicrosoftCommonProps", "CustomAfterMicrosoftCommonProps",
		"CustomBeforeMicrosoftCommonTargets", "CustomAfterMicrosoftCommonTargets",
	];

	public static BuildInputs Of(CapturedArguments captured)
	{
		var files = new SortedSet<string>(StringComparer.Ordinal);
		var probes = new SortedSet<string>(StringComparer.Ordinal);
		var environment = new SortedSet<string>(AlwaysRecordedEnvironment, StringComparer.Ordinal);
		var pending = new Queue<string>();

		void Add(string path, bool parse)
		{
			probes.Add(path);
			if (File.Exists(path) && files.Add(path) && parse)
				pending.Enqueue(path);
		}

		Add(captured.ProjectFile, parse: true);
		for (DirectoryInfo? directory = new FileInfo(captured.ProjectFile).Directory; directory is not null; directory = directory.Parent)
		{
			foreach (string name in AncestorFiles)
				Add(Path.Combine(directory.FullName, name), parse: name != "global.json");
		}

		// Restore's output: the assets file, and the imports NuGet generates, which name the packages' build files by
		// version, so a changed package changes them.
		if (captured.ProjectAssetsFile is string assets)
			Add(assets, parse: false);
		if (captured.ProjectExtensionsPath is string extensions)
		{
			string projectFileName = Path.GetFileName(captured.ProjectFile);
			Add(Path.Combine(extensions, $"{projectFileName}.nuget.g.props"), parse: false);
			Add(Path.Combine(extensions, $"{projectFileName}.nuget.g.targets"), parse: false);
		}

		while (pending.TryDequeue(out string? file))
		{
			XDocument document;
			string text;
			try
			{
				text = File.ReadAllText(file);
				document = XDocument.Parse(text);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
			{
				return new BuildInputs(files, probes, environment, $"{Path.GetFileName(file)} could not be read ({exception.Message})");
			}

			foreach (Match match in PropertyReference().Matches(text))
				environment.Add(match.Groups["name"].Value);

			foreach (XElement import in document.Descendants().Where(element => element.Name.LocalName == "Import"))
			{
				// An SDK's own files come with the SDK, recorded as the SDK selection.
				if (import.Attribute("Sdk") is not null)
					continue;

				string project = (string?)import.Attribute("Project") ?? "";
				switch (Resolve(project, file, captured.ProjectFile))
				{
					case Resolved.Covered:
						break;
					case Resolved.Path { Value: string path }:
						Add(path, parse: true);
						break;
					default:
						return new BuildInputs(files, probes, environment, $"{Path.GetFileName(file)} imports '{project}', whose path depends on more than its text");
				}
			}
		}

		return new BuildInputs(files, probes, environment, null);
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

		string thisDirectory = Path.GetDirectoryName(importingFile)! + Path.DirectorySeparatorChar;
		string projectDirectory = Path.GetDirectoryName(projectFile)!;
		string path = project
			.Replace("$(MSBuildThisFileDirectory)", thisDirectory, StringComparison.OrdinalIgnoreCase)
			.Replace("$(MSBuildProjectDirectory)", projectDirectory, StringComparison.OrdinalIgnoreCase)
			.Trim();

		if (path.Length == 0 || path.Contains("$(", StringComparison.Ordinal) || path.Contains("@(", StringComparison.Ordinal)
			|| path.Contains("%(", StringComparison.Ordinal) || path.IndexOfAny(['*', '?', ';']) >= 0)
			return new Resolved.Unknown();

		return new Resolved.Path(Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(importingFile)!));
	}

	[GeneratedRegex(@"\$\((?<name>[A-Za-z_][A-Za-z0-9_\-]*)\)")]
	private static partial Regex PropertyReference();

	[GeneratedRegex(@"^\s*\$\(\[MSBuild\]::GetPathOfFileAbove\(\s*'(?<name>[^']+)'\s*(,\s*'[^']*'\s*)?\)\)\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex PathOfFileAbove();
}
