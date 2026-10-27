using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// The compiler arguments of each project of a solution, captured during an MSBuildWorkspace load
/// (<see cref="CompilerArgumentCapture"/>) and reused by later loads of the same solution while nothing they were
/// worked out from has changed, so those loads need no design-time build. One entry per solution path, holding
/// one capture per project and target framework, with the facts MSBuildWorkspace adds to what the compiler is told
/// (project references, output paths, default namespace), the load's diagnostics, and what the entry was worked
/// out from:
/// <list type="bullet">
/// <item>the content (write time and length) of the solution file and of each project's <see cref="BuildInputs"/>
/// files, including <c>project.assets.json</c>;</item>
/// <item>the existence of every file the projects list (sources, references, analyzers, analyzer configs) and of each
/// <c>Directory.Build.*</c>, <c>Directory.Packages.props</c> and <c>global.json</c> that would be imported if it were
/// added;</item>
/// <item>which source files exist in each project's folder, so a file added or deleted under a default glob counts;</item>
/// <item>the environment variables the build files read, and the installed SDKs.</item>
/// </list>
/// An ordinary source edit changes none of these, so it keeps the entry; the workspace reads source text from disk.
/// Anything that changes one, a missing entry, or an entry from another Roslynk version, loads through MSBuild,
/// which captures again and replaces the entry. A load is stored only when the solution rebuilt from it describes
/// exactly as MSBuildWorkspace's did (<see cref="ProjectFacts"/>), and never when a build input changed during the
/// load.
/// </summary>
internal sealed class CompilerArgumentCache
{
	/// <summary>Raise when what an entry holds, or how it is read, changes; older entries are then never used.</summary>
	public const int Format = 1;

	private static readonly TimeSpan UnusedEntryLifetime = TimeSpan.FromDays(30);

	private static readonly TimeSpan AbandonedCaptureLifetime = TimeSpan.FromDays(1);

	private static readonly string RoslynkVersion =
		typeof(CompilerArgumentCache).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

	/// <summary>The cache every load uses: under the user's local application data, or the temp folder without one.</summary>
	internal static CompilerArgumentCache Default { get; set; } = new(DefaultRoot());

	public CompilerArgumentCache(string root)
	{
		Root = root;
	}

	public string Root { get; }

	/// <summary>Why the last load through this cache did not use it; empty when it did.</summary>
	internal string LastLoadReason { get; private set; } = "";

	/// <summary>Why the last load through MSBuild did not fill this cache; empty when it did.</summary>
	internal string LastStoreReason { get; private set; } = "";

	private static string DefaultRoot()
	{
		string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
		return Path.Combine(string.IsNullOrEmpty(local) ? Path.GetTempPath() : local, "Roslynk", "CompilerArguments");
	}

	private string EntryPath(string solutionPath) =>
		Path.Combine(Root, "solutions", $"{StableHash.Of($"{Format}\n{Path.GetFullPath(solutionPath)}")[..32]}.json");

	/// <summary>
	/// The solution rebuilt in <paramref name="workspace"/> from its entry, or null with the reason it must load
	/// through MSBuild.
	/// </summary>
	public CachedLoad? TryLoad(AdhocWorkspace workspace, string solutionPath, out string reason)
	{
		CachedLoad? loaded = Load(workspace, solutionPath, out reason);
		LastLoadReason = reason;
		return loaded;
	}

	private CachedLoad? Load(AdhocWorkspace workspace, string solutionPath, out string reason)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(solutionPath);

		string entryPath = EntryPath(solutionPath);
		CacheEntry? entry;
		try
		{
			if (!File.Exists(entryPath))
			{
				reason = "no compiler arguments were captured for this solution";
				return null;
			}

			entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(entryPath), JsonOptions);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			reason = $"the cached compiler arguments could not be read ({exception.Message})";
			return null;
		}

		if (entry is null || entry.Format != Format || entry.RoslynkVersion != RoslynkVersion
			|| !string.Equals(entry.SolutionPath, Path.GetFullPath(solutionPath), StringComparison.Ordinal))
		{
			reason = "the cached compiler arguments are from another version of Roslynk";
			return null;
		}

		if (Changed(entry) is string change)
		{
			reason = change;
			return null;
		}

		Solution solution = CompilerCommandLineLoad.Build(workspace, solutionPath, entry.Projects);
		Touch(entryPath);
		reason = "";
		return new CachedLoad(solution, entry.LoadDiagnostics, entry.LoadWarnings);
	}

	/// <summary>What changed since the entry was captured, or null when nothing did.</summary>
	private static string? Changed(CacheEntry entry)
	{
		if (SdkSelection() != entry.Sdk)
			return "the installed .NET SDKs changed";

		foreach ((string name, string? value) in entry.Environment)
		{
			if (Environment.GetEnvironmentVariable(name) != value)
				return $"the environment variable {name} changed";
		}

		foreach ((string path, string stamp) in entry.Stamps)
		{
			if (FileStamp.Of(path) != stamp)
				return $"{path} changed";
		}

		foreach ((string path, bool existed) in entry.Exists)
		{
			if (File.Exists(path) != existed)
				return existed ? $"{path} was deleted" : $"{path} was added";
		}

		var memberships = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (CachedProject project in entry.Projects)
		{
			string directory = Path.GetDirectoryName(project.FilePath)!;
			string key = $"{directory}|{string.Join(',', project.MembershipExtensions)}";
			if (!memberships.TryGetValue(key, out string? membership))
			{
				membership = Membership(directory, project.MembershipExtensions, DateTime.MaxValue, out _);
				memberships[key] = membership;
			}

			if (membership != project.Membership)
				return $"a source file was added to or deleted from {Path.GetFileName(project.FilePath)}'s folder";
		}

		return null;
	}

	/// <summary>
	/// A capture for the load about to run, or null with the reason when Roslynk must not supply its targets:
	/// <c>CustomAfterMicrosoftCommonTargets</c> as a global property would replace a value the solution's projects or
	/// the environment set, and so change even the load itself. Only the projects the solution lists and the
	/// <c>Directory.Build.*</c> above them are checked; a package that sets it is not seen.
	/// </summary>
	public CompilerArgumentCapture? BeginCapture(string solutionPath, out string reason)
	{
		CompilerArgumentCapture? capture = Capture(solutionPath, out reason);
		LastStoreReason = reason;
		return capture;
	}

	private CompilerArgumentCapture? Capture(string solutionPath, out string reason)
	{
		ArgumentNullException.ThrowIfNull(solutionPath);
		try
		{
			if (Environment.GetEnvironmentVariable(CompilerArgumentCapture.TargetsProperty) is { Length: > 0 })
			{
				reason = $"the environment sets {CompilerArgumentCapture.TargetsProperty}";
				return null;
			}

			if (SetsCustomAfterTargets(solutionPath) is string file)
			{
				reason = $"{file} sets {CompilerArgumentCapture.TargetsProperty}";
				return null;
			}

			Sweep();
			reason = "";
			return CompilerArgumentCapture.Begin(Root);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
		{
			reason = $"compiler arguments cannot be captured ({exception.Message})";
			return null;
		}
	}

	private static string? SetsCustomAfterTargets(string solutionPath)
	{
		var checkedFiles = new HashSet<string>(StringComparer.Ordinal);
		foreach (string projectFile in CompilerCommandLineLoad.SolutionProjectFiles(solutionPath))
		{
			var candidates = new List<string> { projectFile };
			for (DirectoryInfo? directory = new FileInfo(projectFile).Directory; directory is not null; directory = directory.Parent)
			{
				candidates.Add(Path.Combine(directory.FullName, "Directory.Build.props"));
				candidates.Add(Path.Combine(directory.FullName, "Directory.Build.targets"));
			}

			foreach (string candidate in candidates)
			{
				if (checkedFiles.Add(candidate) && File.Exists(candidate)
					&& File.ReadAllText(candidate).Contains($"<{CompilerArgumentCapture.TargetsProperty}", StringComparison.OrdinalIgnoreCase))
					return candidate;
			}
		}

		return null;
	}

	/// <summary>
	/// Stores what <paramref name="capture"/> holds for <paramref name="loaded"/>, the solution MSBuildWorkspace
	/// loaded with it, replacing any earlier entry. Returns the reason when nothing was stored.
	/// </summary>
	public string? Store(
		CompilerArgumentCapture capture,
		string solutionPath,
		Solution loaded,
		IReadOnlyList<string> loadDiagnostics,
		IReadOnlyList<string> loadWarnings,
		DateTime loadStartedUtc)
	{
		string? notStored;
		try
		{
			notStored = StoreEntry(capture, solutionPath, loaded, loadDiagnostics, loadWarnings, loadStartedUtc);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// The cache only ever saves time; the load stands without it.
			notStored = $"the compiler arguments could not be stored ({exception})";
		}

		LastStoreReason = notStored ?? "";
		return notStored;
	}

	private string? StoreEntry(
		CompilerArgumentCapture capture,
		string solutionPath,
		Solution loaded,
		IReadOnlyList<string> loadDiagnostics,
		IReadOnlyList<string> loadWarnings,
		DateTime loadStartedUtc)
	{
		ArgumentNullException.ThrowIfNull(capture);
		ArgumentNullException.ThrowIfNull(loaded);

		if (loaded.Projects.FirstOrDefault(project => project.Language != LanguageNames.CSharp) is Project other)
			return $"{other.Name} is not a C# project";

		ILookup<string, CapturedArguments> capturesByProject = capture.Read().ToLookup(c => c.ProjectFile, StringComparer.Ordinal);
		var stamps = new SortedDictionary<string, string>(StringComparer.Ordinal);
		var exists = new SortedDictionary<string, bool>(StringComparer.Ordinal);
		var environment = new SortedDictionary<string, string?>(StringComparer.Ordinal);
		var projects = new List<CachedProject>();
		var inputsByProject = new Dictionary<string, BuildInputs>(StringComparer.Ordinal);

		string fullSolutionPath = Path.GetFullPath(solutionPath);
		if (FileStamp.Of(fullSolutionPath) is not string solutionStamp)
			return "the solution file could not be read";
		stamps[fullSolutionPath] = solutionStamp;

		foreach (IGrouping<string, Project> sameFile in loaded.Projects.GroupBy(project => FileStamp.NormalizePath(project.FilePath ?? ""), StringComparer.Ordinal))
		{
			CapturedArguments[] captures = capturesByProject[sameFile.Key]
				.DistinctBy(c => c.TargetFramework, StringComparer.Ordinal)
				.ToArray();
			if (captures.Length != sameFile.Count())
				return $"{Path.GetFileName(sameFile.Key)} has {captures.Length} captured compiler command lines for {sameFile.Count()} loaded projects";

			foreach (Project project in sameFile)
			{
				CapturedArguments? captured = captures.Length == 1
					? captures[0]
					: captures.FirstOrDefault(c => string.Equals(c.TargetFramework, ProjectFramework.Of(project), StringComparison.OrdinalIgnoreCase));
				if (captured is null)
					return $"{project.Name}'s target framework has no captured compiler command line";

				if (!inputsByProject.TryGetValue(captured.ProjectFile + "|" + captured.TargetFramework, out BuildInputs? inputs))
				{
					inputs = NamedBuildInputs.Of(captured);
					inputsByProject[captured.ProjectFile + "|" + captured.TargetFramework] = inputs;
				}

				if (inputs.Uncertain is string uncertain)
					return uncertain;

				foreach (string file in inputs.Files)
				{
					if (FileStamp.Of(file) is not string stamp || File.GetLastWriteTimeUtc(file) >= loadStartedUtc)
						return $"{file} changed while the solution loaded";
					stamps[file] = stamp;
				}

				foreach (string probe in inputs.Probes)
					exists[probe] = File.Exists(probe);
				foreach (string name in inputs.EnvironmentVariables)
					environment[name] = Environment.GetEnvironmentVariable(name);

				CachedProject cached = Describe(project, captured, loaded, loadStartedUtc, out string? notCached);
				if (notCached is not null)
					return notCached;

				foreach (string listed in Listed(project))
					exists[listed] = File.Exists(listed);
				projects.Add(cached);
			}
		}

		// Stored only when the arguments rebuild what MSBuildWorkspace loaded, fact for fact.
		using (var check = new AdhocWorkspace())
		{
			Solution rebuilt = CompilerCommandLineLoad.Build(check, solutionPath, projects);
			string[] differences = ProjectFacts.Differences(ProjectFacts.Describe(loaded), "MSBuild", ProjectFacts.Describe(rebuilt), "captured");
			if (differences.Length > 0)
				return $"the captured compiler arguments do not rebuild the loaded solution ({differences[0]})";
		}

		var entry = new CacheEntry(
			Format,
			RoslynkVersion,
			fullSolutionPath,
			SdkSelection(),
			stamps,
			exists,
			environment,
			[.. projects],
			[.. loadDiagnostics],
			[.. loadWarnings]);
		CompilerArgumentCapture.AtomicWrite(EntryPath(solutionPath), JsonSerializer.Serialize(entry, JsonOptions));
		return null;
	}

	/// <summary>
	/// The project as the cache keeps it: its captured arguments, and what MSBuildWorkspace's load adds to them.
	/// <paramref name="notCached"/> says why it cannot be kept.
	/// </summary>
	private static CachedProject Describe(Project project, CapturedArguments captured, Solution loaded, DateTime loadStartedUtc, out string? notCached)
	{
		// The arguments name each referenced project's output; MSBuildWorkspace makes those project references.
		var loadedReferences = project.MetadataReferences.OfType<PortableExecutableReference>()
			.Select(reference => reference.FilePath).OfType<string>().Select(FileStamp.NormalizePath)
			.ToHashSet(StringComparer.Ordinal);
		string[] replaced = CompilerCommandLineLoad.References(captured.Arguments, captured.ProjectFile)
			.Select(reference => reference.Path)
			.Where(path => !loadedReferences.Contains(path))
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		CachedProjectReference[] projectReferences = project.ProjectReferences
			.Select(reference => loaded.GetProject(reference.ProjectId))
			.Zip(project.ProjectReferences)
			.Where(pair => pair.First is not null)
			.Select(pair => new CachedProjectReference(FileStamp.NormalizePath(pair.First!.FilePath ?? ""), pair.First!.Name, [.. pair.Second.Aliases], pair.Second.EmbedInteropTypes))
			.ToArray();

		string projectDirectory = Path.GetDirectoryName(captured.ProjectFile)!;
		string[] extensions = project.AdditionalDocuments
			.Select(document => document.FilePath)
			.OfType<string>()
			.Where(path => IsUnder(path, projectDirectory))
			.Select(Path.GetExtension)
			.OfType<string>()
			.Where(extension => extension.Length > 0)
			.Append(".cs")
			.Append(".editorconfig")
			.Select(extension => extension.ToLowerInvariant())
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();
		string membership = Membership(projectDirectory, extensions, loadStartedUtc, out string? addedDuringLoad);
		notCached = addedDuringLoad is null ? null : $"{addedDuringLoad} was written while the solution loaded";

		return new CachedProject(
			FileStamp.NormalizePath(captured.ProjectFile),
			project.Name,
			captured.Arguments,
			project.OutputFilePath,
			project.OutputRefFilePath,
			project.CompilationOutputInfo.AssemblyPath,
			project.CompilationOutputInfo.GeneratedFilesOutputDirectory,
			project.DefaultNamespace,
			project.ParseOptions?.DocumentationMode ?? DocumentationMode.Parse,
			projectReferences,
			replaced,
			[.. project.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().Select(reference => FileStamp.NormalizePath(reference.FullPath))],
			extensions,
			membership);
	}

	/// <summary>Every file the project lists, whose existence the entry depends on; references to other projects are project references.</summary>
	private static IEnumerable<string> Listed(Project project) =>
		project.Documents.Select(document => document.FilePath)
			.Concat(project.AdditionalDocuments.Select(document => document.FilePath))
			.Concat(project.AnalyzerConfigDocuments.Select(document => document.FilePath))
			.Concat(project.MetadataReferences.OfType<PortableExecutableReference>().Select(reference => reference.FilePath))
			.Concat(project.AnalyzerReferences.Select(reference => reference.FullPath))
			.OfType<string>()
			.Select(FileStamp.NormalizePath);

	private static bool IsUnder(string path, string directory)
	{
		string relative = Path.GetRelativePath(directory, path);
		return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
	}

	private static readonly string[] IgnoredFolders = ["bin", "obj", "node_modules"];

	/// <summary>
	/// A hash of the paths, relative to the project's folder, of the files with these extensions under it, less build
	/// output and the folders the SDK's default globs leave out. <paramref name="writtenSince"/> names a file written
	/// at or after <paramref name="since"/>, if any.
	/// </summary>
	private static string Membership(string projectDirectory, IReadOnlyCollection<string> extensions, DateTime since, out string? writtenSince)
	{
		writtenSince = null;
		var paths = new List<string>();
		if (Directory.Exists(projectDirectory))
		{
			var pending = new Stack<DirectoryInfo>([new DirectoryInfo(projectDirectory)]);
			while (pending.Count > 0)
			{
				DirectoryInfo directory = pending.Pop();
				foreach (FileSystemInfo item in directory.EnumerateFileSystemInfos())
				{
					if (item is DirectoryInfo child)
					{
						if (!child.Name.StartsWith('.') && !IgnoredFolders.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
							pending.Push(child);
					}
					else if (extensions.Contains(item.Extension.ToLowerInvariant()))
					{
						paths.Add(Path.GetRelativePath(projectDirectory, item.FullName));
						if (writtenSince is null && since != DateTime.MaxValue && item.LastWriteTimeUtc >= since)
							writtenSince = item.FullName;
					}
				}
			}
		}

		paths.Sort(StringComparer.Ordinal);
		return StableHash.Of(string.Join('\n', paths));
	}

	/// <summary>
	/// The .NET installation the design-time build's SDK comes from, and the SDKs it holds: <c>global.json</c> picks
	/// among them and is recorded as a build input.
	/// </summary>
	private static string SdkSelection()
	{
		string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
		if (string.IsNullOrEmpty(host))
		{
			string executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
			host = (Environment.GetEnvironmentVariable("PATH") ?? "")
				.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
				.Select(directory => Path.Combine(directory, executable))
				.FirstOrDefault(File.Exists);
		}

		if (host is null)
			return "";

		string resolved = new FileInfo(host).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? host;
		string sdkFolder = Path.Combine(Path.GetDirectoryName(resolved)!, "sdk");
		string[] sdks = Directory.Exists(sdkFolder)
			? [.. Directory.GetDirectories(sdkFolder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
			: [];
		return $"{resolved}|{string.Join(',', sdks)}";
	}

	/// <summary>Marks an entry used, so the sweep keeps it.</summary>
	private static void Touch(string entryPath)
	{
		try
		{
			File.SetLastWriteTimeUtc(entryPath, DateTime.UtcNow);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>Deletes entries unused for 30 days and capture folders a load left behind.</summary>
	private void Sweep()
	{
		DateTime now = DateTime.UtcNow;
		string solutions = Path.Combine(Root, "solutions");
		if (Directory.Exists(solutions))
		{
			foreach (FileInfo entry in new DirectoryInfo(solutions).EnumerateFiles())
			{
				if (now - entry.LastWriteTimeUtc > UnusedEntryLifetime)
					TryDelete(() => entry.Delete());
			}
		}

		string captures = Path.Combine(Root, "captures");
		if (Directory.Exists(captures))
		{
			foreach (DirectoryInfo folder in new DirectoryInfo(captures).EnumerateDirectories())
			{
				if (now - folder.LastWriteTimeUtc > AbandonedCaptureLifetime)
					TryDelete(() => folder.Delete(recursive: true));
			}
		}
	}

	private static void TryDelete(Action delete)
	{
		try
		{
			delete();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}
}

/// <summary>A solution rebuilt from the cache, with the diagnostics and warnings its MSBuild load reported.</summary>
internal sealed record CachedLoad(Solution Solution, IReadOnlyList<string> LoadDiagnostics, IReadOnlyList<string> LoadWarnings);

internal sealed record CacheEntry(
	int Format,
	string RoslynkVersion,
	string SolutionPath,
	string Sdk,
	SortedDictionary<string, string> Stamps,
	SortedDictionary<string, bool> Exists,
	SortedDictionary<string, string?> Environment,
	CachedProject[] Projects,
	string[] LoadDiagnostics,
	string[] LoadWarnings);

/// <summary>
/// One project and target framework: its captured compiler arguments, and what MSBuildWorkspace's load adds to them.
/// <see cref="ReplacedReferences"/> are the argument references MSBuildWorkspace turned into
/// <see cref="ProjectReferences"/>; <see cref="UnresolvedAnalyzers"/> were missing at load.
/// </summary>
internal sealed record CachedProject(
	string FilePath,
	string Name,
	string[] Arguments,
	string? OutputFilePath,
	string? OutputRefFilePath,
	string? CompilationOutputAssemblyPath,
	string? GeneratedFilesOutputDirectory,
	string? DefaultNamespace,
	DocumentationMode DocumentationMode,
	CachedProjectReference[] ProjectReferences,
	string[] ReplacedReferences,
	string[] UnresolvedAnalyzers,
	string[] MembershipExtensions,
	string Membership);

internal sealed record CachedProjectReference(string FilePath, string Name, string[] Aliases, bool EmbedInteropTypes);

internal static class StableHash
{
	public static string Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
