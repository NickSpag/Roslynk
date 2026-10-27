using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// One metadata reference per DLL for the whole process. MSBuildWorkspace's reference cache is a workspace
/// service, so every loaded solution reads each NuGet and framework DLL again and builds its own symbols for
/// it: two checkouts of one repository hold two copies. Roslyn reuses a DLL's symbols across every compilation
/// holding the same metadata, so handing each solution the same reference shares them. Keyed by path and file
/// stamp, so a DLL rebuilt in place gets a fresh reference. Held weakly: the cache enables reuse, it does not
/// keep a closed solution's references alive.
/// </summary>
internal static class SharedMetadataReferences
{
	private static readonly Dictionary<(string Path, string Stamp), WeakReference<PortableExecutableReference>> References = [];
	private static readonly Lock Gate = new();

	/// <summary>The solution with every project's file references replaced by the process-wide ones.</summary>
	public static Solution Share(Solution solution)
	{
		foreach (Project project in solution.Projects)
		{
			var shared = new List<MetadataReference>(project.MetadataReferences.Count);
			bool changed = false;

			foreach (MetadataReference reference in project.MetadataReferences)
			{
				MetadataReference replacement = reference is PortableExecutableReference file ? Share(file) : reference;
				changed |= !ReferenceEquals(replacement, reference);
				shared.Add(replacement);
			}

			if (changed)
				solution = solution.WithProjectMetadataReferences(project.Id, shared);
		}

		return solution;
	}

	/// <summary>
	/// The process-wide reference for <paramref name="reference"/>'s file, with its properties (aliases, embedded
	/// interop types), or <paramref name="reference"/> itself when it is the first for that file or the file
	/// cannot be stamped.
	/// </summary>
	public static PortableExecutableReference Share(PortableExecutableReference reference)
	{
		if (reference.FilePath is not string filePath || FileStamp.Of(filePath) is not string stamp)
			return reference;

		var key = (FileStamp.NormalizePath(filePath), stamp);
		PortableExecutableReference? cached;

		lock (Gate)
		{
			if (!References.TryGetValue(key, out WeakReference<PortableExecutableReference>? entry) || !entry.TryGetTarget(out cached))
			{
				References[key] = new WeakReference<PortableExecutableReference>(reference);
				return reference;
			}
		}

		// WithProperties keeps the metadata, so a reference with other aliases still shares its symbols.
		return cached.Properties.Equals(reference.Properties) ? cached : cached.WithProperties(reference.Properties);
	}
}
