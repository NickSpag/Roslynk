using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Workspaces;
using Morris.Roslynk.Tests.Infrastructure.Workspaces.CompilerArgumentCacheTests;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.SharedMetadataReferencesTests;

public class ShareTests
{
	[Fact]
	public async Task WhenTwoSolutionsLoadThroughMSBuild_ThenTheyShareOneReferencePerDll()
	{
		using SolutionWorkspace first = await SolutionWorkspace.LoadAsync(TestSolutions.Simple, TestCaches.New());
		using SolutionWorkspace second = await SolutionWorkspace.LoadAsync(TestSolutions.Simple, TestCaches.New());

		Assert.False(second.LoadedFromCache);
		AssertSameReferences(first, second);
	}

	[Fact]
	public async Task WhenASolutionLoadsFromCachedCompilerArguments_ThenItSharesTheReferencesOfAnMSBuildLoad()
	{
		CompilerArgumentCache cache = TestCaches.New();
		using SolutionWorkspace first = await SolutionWorkspace.LoadAsync(TestSolutions.Simple, cache);
		using SolutionWorkspace second = await SolutionWorkspace.LoadAsync(TestSolutions.Simple, cache);

		Assert.True(second.LoadedFromCache);
		AssertSameReferences(first, second);
	}

	[Fact]
	public void WhenADllIsRebuiltInPlace_ThenItGetsAFreshReference()
	{
		string path = CopyOfADll();
		PortableExecutableReference original = MetadataReference.CreateFromFile(path);

		Assert.Same(original, SharedMetadataReferences.Share(original));
		Assert.Same(original, SharedMetadataReferences.Share(MetadataReference.CreateFromFile(path)));

		File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
		PortableExecutableReference rebuilt = MetadataReference.CreateFromFile(path);

		Assert.Same(rebuilt, SharedMetadataReferences.Share(rebuilt));
	}

	[Fact]
	public void WhenAReferenceHasOtherAliases_ThenItKeepsThemAndSharesTheMetadata()
	{
		string path = CopyOfADll();
		PortableExecutableReference original = MetadataReference.CreateFromFile(path);
		SharedMetadataReferences.Share(original);

		var aliased = (PortableExecutableReference)MetadataReference.CreateFromFile(path).WithAliases(["other"]);
		PortableExecutableReference shared = SharedMetadataReferences.Share(aliased);

		Assert.Equal("other", Assert.Single(shared.Properties.Aliases));
		Assert.Equal(original.GetMetadataId(), shared.GetMetadataId());
	}

	private static void AssertSameReferences(SolutionWorkspace first, SolutionWorkspace second)
	{
		PortableExecutableReference[] firstReferences = FileReferences(first);
		PortableExecutableReference[] secondReferences = FileReferences(second);

		Assert.NotEmpty(firstReferences);
		Assert.Equal(firstReferences.Length, secondReferences.Length);
		for (int i = 0; i < firstReferences.Length; i++)
			Assert.Same(firstReferences[i], secondReferences[i]);
	}

	private static PortableExecutableReference[] FileReferences(SolutionWorkspace workspace) =>
		[.. Assert.Single(workspace.Solution.Projects).MetadataReferences
			.OfType<PortableExecutableReference>()
			.OrderBy(reference => reference.FilePath, StringComparer.Ordinal)];

	private static string CopyOfADll()
	{
		string directory = Path.Combine(Path.GetTempPath(), "RoslynkTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, "Copy.dll");
		File.Copy(typeof(object).Assembly.Location, path);
		return path;
	}
}
