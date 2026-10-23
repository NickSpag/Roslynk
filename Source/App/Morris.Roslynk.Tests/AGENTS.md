# Writing fast Roslynk tests

This folder holds the engine's behavioural tests. Each test that touches a solution pays for an
MSBuild load, a first compilation and a first declaration index; those costs dominate the suite.
Measured on a 16-core machine: a fixture load is 1.0–3.1 s, a warm registry hit is 0 ms, and a
scratch copy's `dotnet restore` is ~1.7 s. The whole suite is 980 tests; reusing loaded instances
and scratch copies is what keeps it under five minutes.

The helpers are in this folder: `TestSolutions.SharedAsync`, `TestSolutions.Cshtml`,
`FixturesNeedingRestore`, and `SharedScratch<TestClass>`. A `SharedScratch` state is keyed by the
test class, so two classes that use the same fixture get separate copies and cannot reset each
other's files while running in parallel.

## The one rule

**Never load the same unchanged solution twice when the test only reads it.** A read-only test
shares the process-wide registry; a test that writes gets its own scratch copy; a test that asserts
instance state gets its own registry.

## Choose by test kind

| Test kind | Fixture | Registry | Restore |
|---|---|---|---|
| Read-only query (`get_symbol`, `get_members`, `search_symbols`, `find_references`, `get_callers`, `get_callees`, `get_diagnostics`, `get_expression_info`, `get_type_hierarchy`, `find_definition`, `find_implementations`, `get_symbol_body`, `find_dead_code`, `find_dead_conditionals`, `get_code_actions`) | Committed fixture (`TestSolutions.Simple`, `.Razor`, `.Cshtml`, …) | `await TestSolutions.SharedAsync(path)` — **never dispose** | Committed fixtures are prepared once |
| Read-only query needing a fixture that was edited first (e.g. the Razor tests' `unused` local) | One prepared scratch copy shared by the class | Shared registry for that copy | Only for package-bearing fixtures |
| Test that writes files, applies a fix, renames, patches, extracts or changes a signature | `TestSolutions.CreateScratch...()` — a fresh copy per test | `new InstanceRegistry()` | Skip for package-free fixtures |
| Many write tests in one class that can tolerate a reset between tests | `SharedScratch<TestClass>.ForAsync(fixture, solution)` — one copy per class | `scratch.SharedRegistry` | Once, at creation |
| Cold load: asserts `error=Indexing` / `status=Building` / load diagnostics | Committed or scratch | `new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero))` | As the fixture needs |
| Asserts instance state: `IsDirty`, watcher errors, rebuild, eviction, reload, `OpenSolutionPaths` | Committed or scratch | `new InstanceRegistry()` — never the shared one | As the fixture needs |
| Asserts metrics state (`error=Computing`) or tracked-analyzer swaps | Committed or scratch | `new InstanceRegistry()` | As the fixture needs |
| Needs per-test isolation (watcher/dirty tests) | Committed or scratch | `new InstanceRegistry()` — a fresh load is the isolation; there is no seeding helper | As the fixture needs |

## Rules

1. **Never write to a committed fixture.** Tests that write must use `CreateScratch...` (or
   `SharedScratch`). `git status` must stay clean after a full run.
2. **Never dispose the shared registry.** Write `var registry = await TestSolutions.SharedAsync(path);`
   without `using`; disposing it tears down instances other tests are using.
3. **Use a private registry whenever the test asserts instance state** — cold load, `Building`,
   `Indexing`, `IsDirty`, watcher diagnostics, reload, eviction, metrics `Computing`, tracked
   analyzers, `OpenSolutionPaths`. Sharing those poisons later tests.
4. **Reset a shared scratch copy through the watcher's own path.** `SharedScratch<TestClass>.ResetAsync` restores
   the pristine files and folds the changes with `SolutionFileSync.OnFilesChangedAsync`. A test that
   creates files *after* the instance was loaded must fold those paths explicitly before querying;
   otherwise the snapshot does not see them. A test that edits a build file (`.csproj`, `.props`,
   `.targets`, `.slnx`) leaves the instance dirty, so keep it on a per-test copy.
5. **Restore only package-bearing fixtures.** `Simple`, `Broken`, `References`, `Access`, `Dispose`,
   `Expression`, `LocalFunction`, `Conditional`, `LargeType` and `Cshtml` load cleanly without
   `obj/`; `Razor`, `RazorInject`, `Bind`, `RazorMultiProject`, `CodeStyle`, `Generator` and
   `MultiTarget` need NuGet assets. `TestSolutions` owns the `FixturesNeedingRestore` allowlist; do
   not add a restore to a package-free fixture.
6. **Do not change xUnit parallelism.** The default (one thread per core) is fastest; 8 and 32
   threads both measured ~20% slower, and higher counts make the analyzer budget flaky.
7. **Do not merge fixtures into one solution.** A shared registry already loads each fixture once;
   merging changes project counts, path resolution and name-resolution ambiguity that tests assert.
8. **Leave shared state clean.** A test that marks a shared instance dirty must be on a private
   registry; otherwise the next test sees a rebuild.

## Patterns

Read-only test on a committed fixture:

```csharp
[Fact]
public async Task WhenAMethodCallsOtherMembers_ThenTheCalleesAreReturned()
{
	var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
	var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

	string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.Caller.Run");

	Assert.Contains("resolvedSymbol=SimpleLibrary.Caller.Run", result);
	Assert.DoesNotContain("error=", result);
}
```

Write test on a scratch copy:

```csharp
[Fact]
public async Task WhenRenamingAType_ThenItsDeclarationAndReferencesAreRewrittenOnDisk()
{
	string solutionPath = TestSolutions.CreateScratchSimpleSolution();
	using var registry = new InstanceRegistry();
	await registry.GetOrAddAsync(solutionPath);

	string result = await new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline())
		.RenameSymbol(solutionPath, "SimpleLibrary.Greeter", "Welcomer");

	Assert.Contains("applied=Y", result);
}
```

Cold-load test (must not use the shared registry):

```csharp
[Fact]
public async Task WhenTheLoadWaitTimeoutElapses_ThenIndexingIsReturned()
{
	using var registry = new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero));

	string result = await new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService())
		.GetCallees(TestSolutions.Simple, "SimpleLibrary.Caller.Run");

	Assert.Contains("error=Indexing", result);
	Assert.Contains("status=Building", result);

	await registry.GetOrAddAsync(TestSolutions.Simple);
}
```

Per-test isolation (watcher/dirty tests) — a private registry, never the shared one:

```csharp
[Fact]
public async Task WhenAWatcherReportsAnOverflow_ThenTheInstanceIsMarkedDirty()
{
	using var registry = new InstanceRegistry();
	RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);
	instance.DetachWatcher();

	// ... assert on this instance only
}
```

## Checklist for a new test

- Does it write? → scratch copy (`CreateScratch...`), private registry.
- Does it assert instance state (cold load, dirty, watcher, reload, metrics)? → private registry.
- Does it belong to a class with many write tests? → `SharedScratch<TestClass>`, reset in `IAsyncLifetime.InitializeAsync`.
- Otherwise → `TestSolutions.SharedAsync(committedFixture)` and no `using`.
- Does the fixture have a `PackageReference`, `FrameworkReference` or `netstandard` target? → it
  needs restore; leave that to `TestSolutions`.
- Does the test create files after the instance is loaded? → fold those paths explicitly, or use a
  per-test copy.
- Run the class alone, then the full suite; both must pass. Compare with a trx logger rather than
  wall-clock guesswork, and remember that machine background load can double a run — paired,
  back-to-back runs are the only reliable comparison.
