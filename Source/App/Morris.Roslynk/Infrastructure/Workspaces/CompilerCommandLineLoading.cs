namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Whether a load may build the solution from the compiler command lines a build kept
/// (<see cref="CompilerCommandLineLoad"/>) before falling back to MSBuild. On unless the host reads
/// 'Roslynk:LoadFromCompilerCommandLines' as false; the files exist only where a build chose to keep them.
/// </summary>
public sealed record CompilerCommandLineLoading(bool Enabled)
{
	public static readonly CompilerCommandLineLoading Default = new(true);
}
