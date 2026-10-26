using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;

// WorktreeShare <shared|separate|load> <rootA> <rootB> <report.json>
//   shared:   both worktrees under the placeholder root; B is A's solution with B's differences swapped in.
//   separate: each worktree loaded as its own solution under its own paths.
//   load:     A only, mapped (a baseline for one solution).
// Every project of each solution is compiled (generators run) and fully bound (GetDiagnostics).
string mode = args[0];
string rootA = Path.GetFullPath(args[1]).TrimEnd('/');
string rootB = Path.GetFullPath(args[2]).TrimEnd('/');
string reportPath = args[3];
const string Placeholder = "/_";
var stopwatch = Stopwatch.StartNew();
var report = new Dictionary<string, object?> { ["mode"] = mode, ["rootA"] = rootA, ["rootB"] = rootB };
void Log(string s) => Console.Error.WriteLine($"[{stopwatch.Elapsed.TotalSeconds,6:F1}s] {s}");

using var workspace = new AdhocWorkspace();
var analyzerLoader = workspace.Services.GetRequiredService<IAnalyzerService>().GetLoader();
var shared = new Shared(analyzerLoader);

Solution solutionA, solutionB;
var leaks = new Leaks();
if (mode == "separate")
{
    var a = Loader.Load(rootA, rootA, shared, leaks);
    var b = Loader.Load(rootB, rootB, shared, leaks);
    solutionA = new AdhocWorkspace().AddSolution(a.Solution);
    solutionB = new AdhocWorkspace().AddSolution(b.Solution);
    report["projects"] = a.Projects.Count;
    report["fromMSBuildOnly"] = a.Skipped;
}
else
{
    var a = Loader.Load(rootA, Placeholder, shared, leaks);
    solutionA = workspace.AddSolution(a.Solution);
    report["projects"] = a.Projects.Count;
    report["fromMSBuildOnly"] = a.Skipped;
    if (mode == "load") solutionB = solutionA;
    else
    {
        var b = Loader.Load(rootB, Placeholder, shared, leaks);
        (solutionB, var swapped) = await Versioning.AsVersionOf(solutionA, a, b);
        report["swapped"] = swapped;
    }
}
Log($"loaded: {solutionA.ProjectIds.Count} projects");
report["loadSeconds"] = stopwatch.Elapsed.TotalSeconds;

var compsA = await Compile.AllAsync(solutionA, Log, "A");
report["afterA"] = Memory.Snapshot();
var compsB = mode == "load" ? compsA : await Compile.AllAsync(solutionB, Log, "B");
report["afterB"] = Memory.Snapshot();
report["compileSeconds"] = stopwatch.Elapsed.TotalSeconds;

// Which of B's projects have a compilation of their own, and why.
var newCompilations = new List<object>();
foreach (var (id, compB) in compsB)
{
    if (!compsA.TryGetValue(id, out var compA)) { newCompilations.Add(new { project = solutionB.GetProject(id)!.Name, why = "only in B" }); continue; }
    if (ReferenceEquals(compA, compB)) continue;
    var pa = solutionA.GetProject(id)!; var pb = solutionB.GetProject(id)!;
    var ownChanged = !ReferenceEquals(pa.State(), pb.State()) && pb.GetChanges(pa).GetChangedDocuments().Any();
    newCompilations.Add(new
    {
        project = pb.Name,
        why = mode == "separate" ? "separate solution" : ownChanged ? "its own documents changed" : "a referenced project changed",
        sameTopLevelVersion = (await pa.GetDependentSemanticVersionAsync()) == (await pb.GetDependentSemanticVersionAsync()),
        treesShared = compB.SyntaxTrees.Count(t => compA.SyntaxTrees.Contains(t)),
        trees = compB.SyntaxTrees.Count(),
    });
}
report["newCompilationsInB"] = newCompilations.Count;
report["newCompilations"] = newCompilations;
report["leaks"] = leaks.ToReport();
report["generatedWithPlaceholder"] = await Leaks.GeneratedMentioningAsync(solutionB, mode == "separate" ? rootB : Placeholder + "/");
GC.KeepAlive(compsA); GC.KeepAlive(compsB); GC.KeepAlive(solutionA); GC.KeepAlive(solutionB);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Log($"done; {newCompilations.Count} of B's {compsB.Count} compilations are B's own");

static class Memory
{
    public static object Snapshot()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var self = Process.GetCurrentProcess();
        return new { managedMB = GC.GetTotalMemory(false) / 1048576, rssMB = self.WorkingSet64 / 1048576 };
    }
}

static class Extensions
{
    // A project's identity in the solution: unchanged projects keep the same ProjectState object.
    public static object State(this Project p) => typeof(Project).GetProperty("State", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(p)!;
}

static class Compile
{
    public static async Task<Dictionary<ProjectId, Compilation>> AllAsync(Solution solution, Action<string> log, string label)
    {
        var result = new ConcurrentDictionary<ProjectId, Compilation>();
        var errors = 0;
        await Parallel.ForEachAsync(solution.Projects, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, async (p, ct) =>
        {
            var c = await p.GetCompilationAsync(ct);
            if (c is null) return;
            Interlocked.Add(ref errors, c.GetDiagnostics(ct).Count(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS0436"));
            result[p.Id] = c;
        });
        log($"{label}: compiled {result.Count} projects, {errors} errors");
        return new(result);
    }
}

/// <summary>Paths that carry a worktree root after mapping, found while loading.</summary>
sealed class Leaks
{
    public ConcurrentDictionary<string, int> Counts = new();
    public ConcurrentBag<string> Examples = [];
    public void Add(string kind, string example) { Counts.AddOrUpdate(kind, 1, (_, n) => n + 1); if (Examples.Count < 60) Examples.Add($"{kind}: {example}"); }
    public object ToReport() => new { counts = Counts, examples = Examples.Distinct().Order().ToList() };

    public static async Task<object> GeneratedMentioningAsync(Solution solution, string marker)
    {
        var hits = new ConcurrentBag<string>();
        var names = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(solution.Projects, async (p, ct) =>
        {
            foreach (var d in await p.GetSourceGeneratedDocumentsAsync(ct))
            {
                if (d.FilePath?.Contains(marker) == true) names.Add(d.FilePath);
                var text = (await d.GetTextAsync(ct)).ToString();
                if (text.Contains(marker, StringComparison.Ordinal)) hits.Add($"{p.Name}: {d.HintName}");
            }
        });
        return new { documentsWhoseTextNamesARoot = hits.Count, examples = hits.Order().Take(20), generatedPathsExample = names.Order().Take(5) };
    }
}

/// <summary>One reference object per file across both worktrees, as MSBuildWorkspace does within one.</summary>
sealed class Shared(IAnalyzerAssemblyLoader loader)
{
    readonly ConcurrentDictionary<(string, string, bool), MetadataReference> _metadata = new();
    readonly ConcurrentDictionary<string, AnalyzerReference> _analyzers = new();
    public MetadataReference Metadata(string path, MetadataReferenceProperties props) =>
        _metadata.GetOrAdd((path, string.Join(',', props.Aliases), props.EmbedInteropTypes), _ => MetadataReference.CreateFromFile(path, props));
    // Keyed by the mapped path; loaded from the first worktree that names it.
    public AnalyzerReference Analyzer(string mapped, string real) => _analyzers.GetOrAdd(mapped, _ => new AnalyzerFileReference(real, loader));
}

sealed record Loaded(SolutionInfo Solution, Dictionary<ProjectId, (string Rsp, ProjectInfo Info)> Projects, List<string> Skipped, string Root, string MappedRoot);

static class Loader
{
    public static Loaded Load(string root, string mappedRoot, Shared shared, Leaks leaks)
    {
        string Map(string s) => root == mappedRoot ? s : s.Replace(root + "/", mappedRoot + "/", StringComparison.Ordinal);
        string Unmap(string s) => root == mappedRoot ? s : s.StartsWith(mappedRoot + "/", StringComparison.Ordinal) ? root + s[mappedRoot.Length..] : s;
        var projects = new Dictionary<ProjectId, (string, ProjectInfo)>();
        var skipped = new List<string>();
        var parsed = new List<(ProjectId Id, string ProjectFile, string Framework, string Rsp, CSharpCommandLineArguments Args, string Name)>();
        foreach (var projectFile in Listed(Path.Combine(root, "Corral.slnx")))
        {
            var frameworks = Frameworks(projectFile);
            if (frameworks is null) { skipped.Add(Rel(root, projectFile) + " (not restored)"); continue; }
            foreach (var tfm in frameworks)
            {
                var rspPath = Path.Combine(Path.GetDirectoryName(projectFile)!, "obj", "Debug", tfm, Path.GetFileName(projectFile) + ".csc.rsp");
                if (!File.Exists(rspPath)) { skipped.Add($"{Rel(root, projectFile)} {tfm} (no kept command line)"); continue; }
                var lines = File.ReadAllLines(rspPath).Where(l => l.Length > 0).Select(Map).ToArray();
                var mappedProject = Map(projectFile);
                var a = CSharpCommandLineParser.Default.Parse(lines, Path.GetDirectoryName(mappedProject)!, sdkDirectory: null);
                var name = Path.GetFileNameWithoutExtension(projectFile) + (frameworks.Length > 1 ? $"({tfm})" : "");
                parsed.Add((ProjectId.CreateFromSerialized(Guid(mappedProject + "|" + tfm), name), mappedProject, tfm, string.Join('\n', lines), a, name));
            }
        }
        // A reference to another project's built assembly (bin/ or obj/ under the project's folder) becomes a project reference.
        var byOutput = new Dictionary<string, ProjectId>(StringComparer.Ordinal);
        foreach (var p in parsed)
        {
            var dir = Path.GetDirectoryName(p.ProjectFile)!;
            var asm = p.Args.OutputFileName!;
            byOutput[Path.Combine(dir, "obj", "Debug", p.Framework, "ref", asm)] = p.Id;
            byOutput[Path.Combine(dir, "obj", "Debug", p.Framework, "refint", asm)] = p.Id;
            byOutput[Path.Combine(dir, "obj", "Debug", p.Framework, asm)] = p.Id;
            byOutput[Path.Combine(dir, "bin", "Debug", p.Framework, asm)] = p.Id;
        }
        var infos = new List<ProjectInfo>();
        foreach (var p in parsed)
        {
            var a = p.Args;
            var metadata = new List<MetadataReference>();
            var projectRefs = new List<ProjectReference>();
            foreach (var r in a.MetadataReferences)
            {
                var path = Path.GetFullPath(r.Reference, a.BaseDirectory);
                if (byOutput.TryGetValue(path, out var target) && target != p.Id) { projectRefs.Add(new ProjectReference(target, r.Properties.Aliases, r.Properties.EmbedInteropTypes)); continue; }
                var real = Unmap(path);
                if (real != path) leaks.Add("metadata reference inside the worktree (not a project output)", Rel(root, real));
                metadata.Add(shared.Metadata(real, r.Properties));
            }
            var analyzers = a.AnalyzerReferences.Select(r =>
            {
                var mapped = Path.GetFullPath(r.FilePath, a.BaseDirectory);
                var real = Unmap(mapped);
                if (real != mapped) leaks.Add("analyzer or generator built in the worktree", Rel(root, real));
                return shared.Analyzer(mapped, real);
            }).Distinct().ToList();
            DocumentInfo Doc(string mapped, Func<string, string>? rewrite = null) =>
                DocumentInfo.Create(DocumentId.CreateFromSerialized(p.Id, Guid(mapped + "|" + p.Framework), mapped), Path.GetFileName(mapped),
                    loader: new RealFileLoader(Unmap(mapped), rewrite), filePath: mapped);
            var documents = a.SourceFiles.Select(s => Doc(Path.GetFullPath(s.Path))).ToList();
            var additional = a.AdditionalFiles.Select(s => Doc(Path.GetFullPath(s.Path))).ToList();
            var configs = a.AnalyzerConfigPaths.Select(c => Doc(Path.GetFullPath(c), text =>
            {
                if (root != mappedRoot && text.Contains(root + "/", StringComparison.Ordinal))
                {
                    leaks.Add(".editorconfig content naming the worktree (rewritten)", Path.GetFileName(c));
                    return text.Replace(root + "/", mappedRoot + "/", StringComparison.Ordinal);
                }
                return text;
            })).ToList();
            var baseDir = a.BaseDirectory!;
            var options = a.CompilationOptions
                .WithXmlReferenceResolver(new XmlFileResolver(baseDir))
                .WithSourceReferenceResolver(new SourceFileResolver(ImmutableArray<string>.Empty, baseDir))
                .WithStrongNameProvider(new DesktopStrongNameProvider(a.KeyFileSearchPaths))
                .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);
            if (a.CompilationOptions.CryptoKeyFile is { } key) leaks.Add("key file", key);
            var parse = a.ParseOptions.DocumentationMode == DocumentationMode.None ? a.ParseOptions.WithDocumentationMode(DocumentationMode.Parse) : a.ParseOptions;
            var output = Path.Combine(Path.GetDirectoryName(p.ProjectFile)!, "obj", "Debug", p.Framework, a.OutputFileName!);
            var info = ProjectInfo.Create(p.Id, VersionStamp.Default, p.Name, Path.GetFileNameWithoutExtension(a.OutputFileName!), LanguageNames.CSharp,
                    filePath: p.ProjectFile, outputFilePath: output, compilationOptions: options, parseOptions: parse,
                    documents: documents, projectReferences: projectRefs.DistinctBy(r => r.ProjectId), metadataReferences: metadata.Distinct(),
                    analyzerReferences: analyzers, additionalDocuments: additional)
                .WithAnalyzerConfigDocuments(configs)
                .WithCompilationOutputInfo(default(CompilationOutputInfo).WithAssemblyPath(output));
            infos.Add(info);
            projects[p.Id] = (p.Rsp, info);
        }
        return new Loaded(SolutionInfo.Create(SolutionId.CreateFromSerialized(Guid(mappedRoot)), VersionStamp.Default, Path.Combine(mappedRoot, "Corral.slnx"), infos), projects, skipped, root, mappedRoot);
    }

    public static Guid Guid(string s) => new(MD5.HashData(Encoding.UTF8.GetBytes(s)));
    static string Rel(string root, string path) => Path.GetRelativePath(root, path);

    static IEnumerable<string> Listed(string slnx) =>
        XDocument.Load(slnx).Descendants().Where(e => e.Name.LocalName == "Project").Select(e => (string?)e.Attribute("Path")).OfType<string>()
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(slnx)!, p))).Distinct();

    static string[]? Frameworks(string projectFile)
    {
        var assets = Path.Combine(Path.GetDirectoryName(projectFile)!, "obj", "project.assets.json");
        if (!File.Exists(assets)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(assets));
        return json.RootElement.TryGetProperty("project", out var p) && p.TryGetProperty("frameworks", out var f) ? f.EnumerateObject().Select(x => x.Name).ToArray() : null;
    }
}

sealed class RealFileLoader(string real, Func<string, string>? rewrite) : TextLoader
{
    public override async Task<TextAndVersion> LoadTextAndVersionAsync(LoadTextOptions options, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(real, ct);
        SourceText text = rewrite is null
            ? SourceText.From(bytes, bytes.Length, Encoding.UTF8, options.ChecksumAlgorithm, throwIfBinaryDetected: false, canBeEmbedded: true)
            : SourceText.From(rewrite(Encoding.UTF8.GetString(bytes)), Encoding.UTF8, options.ChecksumAlgorithm);
        return TextAndVersion.Create(text, VersionStamp.Default, real);
    }
}

static class Versioning
{
    /// <summary>
    /// B as a version of A: for a project whose mapped command line is A's, each document whose bytes differ is swapped in;
    /// a project whose command line differs is rebuilt from B's (options, references, document list).
    /// </summary>
    public static async Task<(Solution, object)> AsVersionOf(Solution a, Loaded la, Loaded lb)
    {
        var s = a;
        var swapped = new List<string>();
        var rebuilt = new List<string>();
        foreach (var (id, (rspB, infoB)) in lb.Projects)
        {
            if (!la.Projects.TryGetValue(id, out var pa)) { s = s.AddProject(infoB); rebuilt.Add(infoB.Name + " (added)"); continue; }
            if (pa.Rsp != rspB)
            {
                rebuilt.Add(infoB.Name);
                s = s.WithProjectCompilationOptions(id, infoB.CompilationOptions!).WithProjectParseOptions(id, infoB.ParseOptions!)
                     .WithProjectMetadataReferences(id, infoB.MetadataReferences).WithProjectReferences(id, infoB.ProjectReferences)
                     .WithProjectAnalyzerReferences(id, infoB.AnalyzerReferences);
                var project = s.GetProject(id)!;
                var want = infoB.Documents.Select(d => d.Id).ToHashSet();
                foreach (var d in project.DocumentIds.Where(d => !want.Contains(d)).ToList()) s = s.RemoveDocument(d);
                foreach (var d in infoB.Documents.Where(d => s.GetDocument(d.Id) is null)) s = s.AddDocument(d);
            }
            // Documents present in both: swap in B's text where the files differ.
            foreach (var d in infoB.Documents.Concat(infoB.AdditionalDocuments).Concat(infoB.AnalyzerConfigDocuments))
            {
                var realA = la.Root + d.FilePath![la.MappedRoot.Length..];
                var realB = lb.Root + d.FilePath![lb.MappedRoot.Length..];
                if (!File.Exists(realA) || !File.Exists(realB) || File.ReadAllBytes(realA).AsSpan().SequenceEqual(File.ReadAllBytes(realB))) continue;
                var text = (await d.TextLoader!.LoadTextAndVersionAsync(new LoadTextOptions(SourceHashAlgorithm.Sha256), default)).Text;
                if (s.GetDocument(d.Id) is not null) s = s.WithDocumentText(d.Id, text);
                else if (s.GetAdditionalDocument(d.Id) is not null) s = s.WithAdditionalDocumentText(d.Id, text);
                else if (s.GetAnalyzerConfigDocument(d.Id) is not null) s = s.WithAnalyzerConfigDocumentText(d.Id, text);
                swapped.Add(d.FilePath!);
            }
        }
        foreach (var id in la.Projects.Keys.Where(k => !lb.Projects.ContainsKey(k))) { s = s.RemoveProject(id); rebuilt.Add(id + " (removed)"); }
        return (s, new { documents = swapped.Distinct().Order().ToList(), projectsRebuilt = rebuilt });
    }
}
