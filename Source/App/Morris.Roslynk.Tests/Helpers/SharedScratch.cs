using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Watching;

namespace Morris.Roslynk.Tests;

/// <summary>
/// One writable scratch copy of a fixture per test class, loaded once, restored to the pristine
/// fixture between tests and folded back into the snapshot through the watcher's own sync path.
/// A test that edits a build file leaves the instance dirty; the next reset reloads it.
/// </summary>
internal sealed class SharedScratch<TOwner>
	where TOwner : class
{
	private static readonly Dictionary<string, SharedScratch<TOwner>> States = new(StringComparer.OrdinalIgnoreCase);
	private static readonly SemaphoreSlim Gate = new(1, 1);

	private readonly string FixtureName;
	private readonly string SolutionFile;
	private readonly InstanceRegistry Registry = new();
	private RoslynInstance Instance = null!;
	private string PristineDirectory = null!;
	private string ScratchDirectory = null!;

	private SharedScratch(string fixtureName, string solutionFile)
	{
		FixtureName = fixtureName;
		SolutionFile = solutionFile;
	}

	public string SolutionPath { get; private set; } = null!;

	public InstanceRegistry SharedRegistry => Registry;

	/// <summary>Returns the class-wide scratch state for a fixture, creating and loading it on first use.</summary>
	public static async Task<SharedScratch<TOwner>> ForAsync(string fixtureName, string solutionFile)
	{
		await Gate.WaitAsync();
		try
		{
			if (!States.TryGetValue(fixtureName, out SharedScratch<TOwner>? state))
			{
				state = new SharedScratch<TOwner>(fixtureName, solutionFile);
				States[fixtureName] = state;
				await state.InitializeAsync();
			}

			return state;
		}
		finally
		{
			Gate.Release();
		}
	}

	private async Task InitializeAsync()
	{
		PristineDirectory = TestSolutions.FixtureDirectory(FixtureName);
		SolutionPath = TestSolutions.CreateScratch(FixtureName, SolutionFile);

		// A fixture's solution may sit under a subdirectory (MultiTargetSolution/src), so walk up to
		// the copied fixture root rather than assuming the solution's own directory is it.
		DirectoryInfo root = new(Path.GetDirectoryName(SolutionPath)!);
		while (!string.Equals(root.Name, FixtureName, StringComparison.OrdinalIgnoreCase))
			root = root.Parent!;
		ScratchDirectory = root.FullName;

		Instance = await Registry.GetOrAddAsync(SolutionPath);
	}

	/// <summary>
	/// Restores the scratch tree to the pristine fixture, folds every changed, added or deleted path
	/// through <see cref="SolutionFileSync"/> (the watcher's own path, so no reload), and asserts the
	/// snapshot agrees with disk afterwards. A dirty instance (a build-file edit) is reloaded.
	/// </summary>
	public async Task ResetAsync()
	{
		var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (string file in SourceFiles(ScratchDirectory))
		{
			string relative = Path.GetRelativePath(ScratchDirectory, file);
			if (!File.Exists(Path.Combine(PristineDirectory, relative)))
			{
				await DeleteRetryingAsync(file);
				changed.Add(file);
			}
		}

		foreach (string pristine in SourceFiles(PristineDirectory))
		{
			string relative = Path.GetRelativePath(PristineDirectory, pristine);
			string target = Path.Combine(ScratchDirectory, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);

			byte[] wanted = await File.ReadAllBytesAsync(pristine);
			bool same = false;
			if (File.Exists(target))
			{
				byte[] current = await File.ReadAllBytesAsync(target);
				same = wanted.SequenceEqual(current);
			}

			if (!same)
			{
				await File.WriteAllBytesAsync(target, wanted);
				changed.Add(target);
			}
		}

		if (changed.Count > 0)
			await new SolutionFileSync(Instance).OnFilesChangedAsync(changed);

		if (Instance.IsDirty)
			Instance = await Registry.ReloadAsync(SolutionPath);

		await AssertSnapshotMatchesDiskAsync();
	}

	private async Task AssertSnapshotMatchesDiskAsync()
	{
		if (Instance.CurrentModel.Solution is null)
			throw new InvalidOperationException(
				$"Scratch '{FixtureName}' is {Instance.CurrentModel.Status}: {Instance.CurrentModel.FaultMessage} "
				+ string.Join(" | ", Instance.Workspace?.LoadDiagnostics ?? []));

		foreach (var project in Instance.CurrentSolution.Projects)
		{
			foreach (var document in project.Documents.Concat(project.AdditionalDocuments))
			{
				if (document.FilePath is null || document.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
					continue;
				if (!File.Exists(document.FilePath))
					continue;

				string disk = await File.ReadAllTextAsync(document.FilePath);
				string snapshot = (await document.GetTextAsync()).ToString();
				if (!string.Equals(disk, snapshot, StringComparison.Ordinal))
					throw new InvalidOperationException(
						$"Scratch reset left '{document.FilePath}' out of sync with the snapshot "
						+ $"(disk {disk.Length} chars, snapshot {snapshot.Length} chars, first difference at {FirstDifference(disk, snapshot)}).");
			}
		}
	}

	/// <summary>
	/// Deletes a file, retrying briefly: MSBuildWorkspace's design-time build, the watcher and the
	/// previous test's compilation can still hold a handle on a file another class just wrote.
	/// </summary>
	private static async Task DeleteRetryingAsync(string path)
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				File.Delete(path);
				return;
			}
			catch (IOException) when (attempt < 20)
			{
				await Task.Delay(50);
			}
		}
	}

	private static int FirstDifference(string left, string right)
	{
		int shared = Math.Min(left.Length, right.Length);
		for (int index = 0; index < shared; index++)
			if (left[index] != right[index])
				return index;
		return shared;
	}

	private static IEnumerable<string> SourceFiles(string directory)
	{
		if (!Directory.Exists(directory))
			return [];

		return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
			.Where(file => !Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar)
				.Any(segment => segment is "bin" or "obj"));
	}
}
