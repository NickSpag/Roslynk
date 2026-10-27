namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Keeps the compiler arguments of the design-time build MSBuildWorkspace already runs. Roslynk passes its own
/// targets file as the global property <c>CustomAfterMicrosoftCommonTargets</c>, so every project imports it with
/// nothing added to the solution; its target runs after <c>CoreCompile</c>, which a design-time build runs with
/// <c>SkipCompilerExecution=true</c>, and writes <c>@(CscCommandLineArgs)</c> for each project and target framework
/// to a folder of its own. Nothing in it can fail the build: the writes continue on error and their warning is a
/// message.
/// </summary>
internal sealed class CompilerArgumentCapture : IDisposable
{
	public const string TargetsProperty = "CustomAfterMicrosoftCommonTargets";

	public const string DirectoryProperty = "RoslynkCompilerArgumentsDirectory";

	/// <summary>
	/// Four <c>key=value</c> lines (the project, its target framework, its <c>project.assets.json</c> and the
	/// folder NuGet's generated imports are in), then one compiler argument per line.
	/// </summary>
	internal const string Targets = """
		<Project>
		  <!--
		    Roslynk passes this file as CustomAfterMicrosoftCommonTargets when it loads a solution, to keep the compiler
		    arguments of the design-time build it runs anyway. Nothing here may fail that build.
		  -->
		  <PropertyGroup>
		    <MSBuildWarningsAsMessages>$(MSBuildWarningsAsMessages);MSB3491</MSBuildWarningsAsMessages>
		  </PropertyGroup>
		  <Target Name="RoslynkCaptureCompilerArguments" AfterTargets="CoreCompile"
		          Condition="'$(RoslynkCompilerArgumentsDirectory)' != '' and '@(CscCommandLineArgs)' != ''">
		    <PropertyGroup>
		      <_RoslynkCaptureFile>$(RoslynkCompilerArgumentsDirectory)/$([System.Guid]::NewGuid().ToString('N')).rsp</_RoslynkCaptureFile>
		    </PropertyGroup>
		    <WriteLinesToFile File="$(_RoslynkCaptureFile)" Overwrite="true" ContinueOnError="true"
		                      Lines="project=$([MSBuild]::Escape('$(MSBuildProjectFullPath)'));framework=$(TargetFramework);assets=$([MSBuild]::Escape('$(ProjectAssetsFile)'));extensions=$([MSBuild]::Escape('$(MSBuildProjectExtensionsPath)'))" />
		    <WriteLinesToFile File="$(_RoslynkCaptureFile)" ContinueOnError="true" Lines="@(CscCommandLineArgs)" />
		  </Target>
		</Project>
		""";

	private const int HeaderLines = 4;

	private readonly string Folder;

	private CompilerArgumentCapture(string folder, string targetsFile)
	{
		Folder = folder;
		GlobalProperties = new Dictionary<string, string>
		{
			[TargetsProperty] = targetsFile,
			[DirectoryProperty] = folder,
		};
	}

	/// <summary>The global properties to create the MSBuildWorkspace with.</summary>
	public IDictionary<string, string> GlobalProperties { get; }

	/// <summary>A capture into a new folder under <paramref name="root"/>, with the targets file written there once.</summary>
	public static CompilerArgumentCapture Begin(string root)
	{
		string targetsFile = Path.Combine(root, $"Roslynk.CaptureCompilerArguments.{StableHash.Of(Targets)[..12]}.targets");
		if (!File.Exists(targetsFile))
			AtomicWrite(targetsFile, Targets);

		string folder = Path.Combine(root, "captures", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		return new CompilerArgumentCapture(folder, targetsFile);
	}

	/// <summary>
	/// What the load's design-time builds wrote, one entry per project and target framework. A file the target
	/// could not finish (no arguments after its header) is left out, so its project has no capture.
	/// </summary>
	public IReadOnlyList<CapturedArguments> Read()
	{
		var captured = new List<CapturedArguments>();
		foreach (string file in Directory.EnumerateFiles(Folder, "*.rsp"))
		{
			string[] lines = File.ReadAllLines(file);
			if (lines.Length <= HeaderLines)
				continue;

			var header = lines[..HeaderLines]
				.Select(line => line.Split('=', 2))
				.Where(parts => parts.Length == 2)
				.ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
			if (!header.TryGetValue("project", out string? project) || project.Length == 0)
				continue;

			captured.Add(new CapturedArguments(
				Path.GetFullPath(project),
				header.GetValueOrDefault("framework") ?? "",
				header.GetValueOrDefault("assets") is { Length: > 0 } assets ? Path.GetFullPath(assets, Path.GetDirectoryName(project)!) : null,
				header.GetValueOrDefault("extensions") is { Length: > 0 } extensions ? Path.GetFullPath(extensions, Path.GetDirectoryName(project)!) : null,
				lines[HeaderLines..]));
		}

		return captured;
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(Folder, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Left for the cache's sweep of old capture folders.
		}
	}

	/// <summary>Writes beside the target and moves into place, so a concurrent reader never sees half a file.</summary>
	internal static void AtomicWrite(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string temp = $"{path}.{Guid.NewGuid():N}.tmp";
		File.WriteAllText(temp, text);
		File.Move(temp, path, overwrite: true);
	}
}

/// <summary>
/// The compiler arguments one design-time build of a project handed the compiler, with the restore files its
/// evaluation imported.
/// </summary>
internal sealed record CapturedArguments(
	string ProjectFile,
	string TargetFramework,
	string? ProjectAssetsFile,
	string? ProjectExtensionsPath,
	string[] Arguments);
