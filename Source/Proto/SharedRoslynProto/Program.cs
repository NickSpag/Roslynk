using System.Diagnostics;
using Corral.Tools.Analyzer.SymbolGraph;
using Microsoft.CodeAnalysis;
using Morris.Roslynk;
using Morris.Roslynk.Infrastructure.Lifecycle;

// args: <solution.slnx> <graph output.json> <port>
string solutionPath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);
int port = int.Parse(args[2]);

WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port));
builder.Services.AddRoslynk();
builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "Roslynk", Version = "proto" }).WithHttpTransport().WithRoslynkTools();
builder.Services.AddSingleton(new GraphOptions(solutionPath, outputPath));
builder.Services.AddHostedService<GraphService>();
WebApplication app = builder.Build();
app.MapMcp();
app.MapGet("/version", () => "proto");
app.Run();

sealed record GraphOptions(string SolutionPath, string OutputPath);

/// <summary>
/// The change graph as one more reader of Roslynk's snapshots: walks the solution Roslynk loaded, then polls
/// ReadModelAsync for a newer snapshot (Roslynk's watcher folds edits in) and re-walks what changed.
/// </summary>
sealed class GraphService(InstanceRegistry registry, GraphOptions options) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stop)
	{
		await Task.Yield();
		var stopwatch = Stopwatch.StartNew();
		string solutionDir = Path.GetDirectoryName(options.SolutionPath)!;
		RoslynInstance instance = await registry.GetOrAddAsync(options.SolutionPath);
		SolutionModel model = await instance.ReadModelAsync(stop);
		Solution solution = model.Solution!;
		var loaded = stopwatch.Elapsed;

		var excludedDirs = SymbolGraphCommand.NotInGraph.Select(d => Path.Combine(solutionDir, d) + Path.DirectorySeparatorChar).ToArray();
		var projects = solution.Projects
			.Where(p => p.Language == LanguageNames.CSharp)
			.Where(p => p.FilePath?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true)
			.Where(p => !excludedDirs.Any(d => p.FilePath!.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
			.Where(p => !p.AssemblyName.Contains("AppHost", StringComparison.Ordinal))
			.ToList();
		Console.Error.WriteLine($"Loaded {solution.Projects.Count()} projects in {loaded.TotalSeconds:F0}s; building symbols for {projects.Count}");
		var scope = projects.Select(p => p.Id).ToHashSet();
		var builder = new SymbolGraphBuilder(solutionDir);
		await Parallel.ForEachAsync(projects, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, async (project, ct) =>
		{
			var compilation = await project.GetCompilationAsync(ct);
			if (compilation is null) return;
			foreach (var tree in compilation.SyntaxTrees) builder.AddTree(project, compilation, tree);
		});
		var walked = stopwatch.Elapsed;
		var graph = builder.Build(null);
		await SymbolGraphCommand.WriteAsync(graph, options.OutputPath);
		Console.Error.WriteLine($"Timing: load {loaded.TotalSeconds:F0}s · compile and walk {(walked - loaded).TotalSeconds:F0}s · total {stopwatch.Elapsed.TotalSeconds:F0}s");
		Console.WriteLine($"@@ready {graph.Nodes.Count} {graph.Edges.Count}");

		Guid seen = model.Id;
		while (!stop.IsCancellationRequested)
		{
			await Task.Delay(250, stop);
			var next = await instance.ReadModelAsync(stop);
			if (next.Id == seen || next.Solution is null || next.Status != SolutionStatus.Ready || ReferenceEquals(next.Solution, solution)) continue;
			seen = next.Id;
			var started = Stopwatch.StartNew();
			var current = next.Solution;
			var changes = current.GetChanges(solution);
			var changed = new HashSet<string>(StringComparer.Ordinal);
			var removed = new HashSet<string>(StringComparer.Ordinal);
			foreach (var p in changes.GetProjectChanges().Where(p => scope.Contains(p.ProjectId)))
			{
				foreach (var id in p.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).Concat(p.GetAddedDocuments()))
					if (builder.Relative(current.GetDocument(id)?.FilePath) is { } rel) changed.Add(rel);
				foreach (var id in p.GetRemovedDocuments())
					if (builder.Relative(solution.GetDocument(id)?.FilePath) is { } rel) removed.Add(rel);
			}
			solution = current;
			if (changed.Count + removed.Count == 0) continue;
			var (count, fullBuild) = await builder.RewalkAsync(changed, removed, files => WalkAsync(current, files, scope, builder, solutionDir), SymbolGraphCommand.MaxRewalk);
			var updated = builder.Build(null);
			await SymbolGraphCommand.WriteAsync(updated, options.OutputPath);
			Console.WriteLine($"@@updated {count} {started.ElapsedMilliseconds}{(fullBuild is null ? "" : " partial")}");
		}
	}

	private static async Task WalkAsync(Solution solution, IReadOnlyCollection<string> files, HashSet<ProjectId> scope, SymbolGraphBuilder builder, string solutionDir)
	{
		var ids = files.SelectMany(f => solution.GetDocumentIdsWithFilePath(Path.Combine(solutionDir, f))).Where(id => scope.Contains(id.ProjectId));
		foreach (var group in ids.GroupBy(id => id.ProjectId))
		{
			var project = solution.GetProject(group.Key)!;
			var compilation = await project.GetCompilationAsync();
			if (compilation is null) continue;
			foreach (var id in group)
				if (await solution.GetDocument(id)!.GetSyntaxTreeAsync() is { } tree) builder.AddTree(project, compilation, tree);
		}
	}
}
