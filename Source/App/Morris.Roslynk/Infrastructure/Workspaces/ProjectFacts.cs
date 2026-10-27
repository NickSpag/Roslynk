using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Every C# project of a solution described one fact per line, in a stable order, so two loads of the same
/// solution compare as text and a difference reads as a diff. <see cref="CompilerArgumentCache"/> stores a capture
/// only when the solution it rebuilds describes exactly as MSBuildWorkspace's load did.
/// </summary>
internal static class ProjectFacts
{
	public static string[] Describe(Solution solution)
	{
		var lines = new List<string>();
		foreach (Project project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
		{
			string p = $"{project.Name} ({project.FilePath})";
			lines.Add($"{p} assembly {project.AssemblyName}");
			lines.Add($"{p} output {project.OutputFilePath}");
			lines.Add($"{p} ref output {project.OutputRefFilePath}");
			lines.Add($"{p} compilation output {project.CompilationOutputInfo.AssemblyPath}");
			lines.Add($"{p} generated files {project.CompilationOutputInfo.GeneratedFilesOutputDirectory}");
			lines.Add($"{p} default namespace {project.DefaultNamespace}");
			lines.AddRange(project.Documents.Select(d => $"{p} document {d.FilePath} folders {string.Join('/', d.Folders)} {d.SourceCodeKind}"));
			lines.AddRange(project.AdditionalDocuments.Select(d => $"{p} additional {d.FilePath}"));
			lines.AddRange(project.AnalyzerConfigDocuments.Select(d => $"{p} analyzer config {d.FilePath}"));
			lines.AddRange(project.MetadataReferences.OfType<PortableExecutableReference>().Select(r => $"{p} reference {r.FilePath} {string.Join(',', r.Properties.Aliases)} {r.Properties.EmbedInteropTypes}"));
			lines.AddRange(project.ProjectReferences.Select(r => $"{p} project reference {solution.GetProject(r.ProjectId)?.Name} {string.Join(',', r.Aliases)} {r.EmbedInteropTypes}"));
			lines.AddRange(project.AnalyzerReferences.Select(r => $"{p} analyzer {r.FullPath} {r.GetType().Name}"));
			if (project.ParseOptions is CSharpParseOptions parse)
			{
				lines.Add($"{p} language version {parse.SpecifiedLanguageVersion}");
				lines.AddRange(parse.PreprocessorSymbolNames.Select(s => $"{p} define {s}"));
				lines.AddRange(parse.Features.Select(f => $"{p} feature {f.Key}={f.Value}"));
				lines.Add($"{p} documentation mode {parse.DocumentationMode}");
			}

			if (project.CompilationOptions is CSharpCompilationOptions compilation)
			{
				lines.Add($"{p} output kind {compilation.OutputKind}");
				lines.Add($"{p} module {compilation.ModuleName} main {compilation.MainTypeName}");
				lines.Add($"{p} nullable {compilation.NullableContextOptions}");
				lines.Add($"{p} unsafe {compilation.AllowUnsafe}");
				lines.Add($"{p} platform {compilation.Platform}");
				lines.Add($"{p} checked {compilation.CheckOverflow}");
				lines.Add($"{p} optimization {compilation.OptimizationLevel}");
				lines.Add($"{p} warning level {compilation.WarningLevel}");
				lines.Add($"{p} general diagnostics {compilation.GeneralDiagnosticOption}");
				lines.Add($"{p} global usings {string.Join(',', compilation.Usings)}");
				lines.Add($"{p} key {compilation.CryptoKeyFile} {compilation.CryptoKeyContainer} delay {compilation.DelaySign} public {compilation.PublicSign}");
				lines.AddRange(compilation.SpecificDiagnosticOptions.Select(o => $"{p} diagnostic {o.Key}={o.Value}"));
			}
		}

		lines.Sort(StringComparer.Ordinal);
		return [.. lines];
	}

	/// <summary>The lines only one of two descriptions has, each marked with the side it came from.</summary>
	public static string[] Differences(string[] expected, string expectedName, string[] actual, string actualName) =>
	[
		.. expected.Except(actual).Select(line => $"only {expectedName}: {line}"),
		.. actual.Except(expected).Select(line => $"only {actualName}: {line}"),
	];
}
