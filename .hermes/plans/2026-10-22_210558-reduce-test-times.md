# Reduce Roslynk test-suite duration (issue #160) — proposal

> **Status:** proposal only. No source changes made; this file is the review artifact.
> Branch: `160/PM-ReduceTestTimes`, worktree `C:\Data\ZZZScratch\Source\Roslynk\160`.

**Goal:** cut the wall-clock time of `dotnet test Source/Morris.Roslynk.slnx --configuration Release`
without weakening what the tests prove, by removing redundant solution loads and restores.

**Answer to the issue's suggestion** ("one solution with many read-only scenarios, built once"):
the reuse it describes is achievable without merging fixtures. A process-wide shared
`InstanceRegistry` gives every read-only test the same loaded snapshot, so each fixture is loaded
once per test run and Roslyn's per-`Solution`/`Project` caches (compilations, `SymbolFinder`
declaration indexes, diagnostics memo) are reused too. Merging fixtures into one solution buys
almost nothing on top of that and breaks tests that assert single-solution shape (project counts,
path resolution, per-fixture MSBuild properties).

---

## Baseline (measured, this machine: 16 cores / 64 GB, Windows, Release, .NET 10.0.4xx)

| Suite | Tests | Wall | Sum of test durations | Parallel factor |
|---|---|---|---|---|
| `Morris.Roslynk.Tests` | 980 | **5 m 26 s** (326 s) | 3,389 s | 10.4 / 16 cores |
| `Morris.Roslynk.McpTests` | 84 | 5 s (17.6 s incl. build) | — | — |

Engine suite distribution: median test 3.36 s, 253 tests > 5 s, 32 tests > 10 s.
Slowest classes (summed): `SolutionFileSyncTests` 214.6 s (44), `RazorSourceToolTests` 157.3 s (26),
`GetDiagnosticsTests` 123.5 s (28), `ApplyPatchTests` 119.1 s (20), `RazorRenameTests` 103.1 s (14).

### Grounded facts (verified by probe, not inferred)

- **413 `GetOrAddAsync` calls** across 922 test attributes. 223 pass a committed
  `TestSolutions.*` path; ~190 pass a scratch copy or a variable path.
- **A load is the unit cost.** `SolutionWorkspace.LoadAsync` per fixture (warm process):
  Simple 1.8 s, Broken/References/Access/Dispose/LocalFunction/Conditional 1.7–1.9 s,
  Expression 2.2 s, CodeStyle 2.1 s, MultiTarget 1.8 s, LargeType 1.5 s, Generator 2.4 s,
  Razor 2.7 s, RazorInject 2.1 s, Bind 2.0 s, Cshtml 2.4 s, RazorMultiProject 3.1 s.
  First load in a process adds ~2–3 s of JIT (Simple measured 4.2 s cold).
- **A warm registry hit is free:** `GetOrAddAsync` on an already-loaded path = 0 ms, same instance.
- **Roslyn caches are per `Solution`/`Project` and survive only while the instance lives:**
  `GetCompilationAsync` #1 = 10.5 s cold / #2 = 0 ms (same object);
  `SymbolFinder.FindDeclarationsAsync` #1 = 4.0 s cold / #2 = 8 ms;
  `GetSemanticModelAsync` #1 = 57 ms / #2 = 0 ms.
  A fresh registry per test therefore re-pays load + first compilation + first declaration index.
- **Every scratch copy pays `dotnet restore`:** 1.69 s per copy (`TestSolutions.CreateScratch`).
  ~170 scratch copies exist in the suite.
- **Restore cannot simply be dropped everywhere.** Loading a source-only copy (no `obj/`) works
  for package-free fixtures (Simple: 0 compile errors; Cshtml: 0) but silently loses
  `PackageReference` resolution for package fixtures (Razor: 9 × CS0234, `ComponentBase` unresolved)
  and breaks `netstandard2.0` (MultiTarget: 74 errors).
- **Copying `obj/` instead of restoring is not a reliable win** (Razor copy-with-obj 6.1 s vs
  source-only 1.8 s; CodeStyle the same), so the restore must be skipped, not replaced by a copy.
- **Raising xUnit parallelism is harmful.** `MaxParallelThreads=32` took **24 m 8 s** (vs 5 m 26 s)
  and failed 4 of 980 tests (`DocumentDiagnosticsProvider` falls back from IDE0005 to CS8019 after
  its 5 s analyzer budget; `RoslynInstanceTests` rebuild timeout). Keep the default (cores).
- **Tests never write to committed fixtures** (`git status` clean after the full run): writes go to
  scratch copies, so sharing a committed fixture's instance is safe from disk mutation.

### Where the 3,389 s (summed) goes

| Cost | Estimate | Share |
|---|---|---|
| 413 solution loads × ~2.1 s | ~870 s | 26% |
| ~170 scratch restores × 1.7 s | ~290 s | 9% |
| Roslyn work: compilations, analyzers, Razor generation, projections | ~2,200 s | 65% |

So roughly **one third of the summed test time is avoidable setup**, spread as ~2.1 s per test
(load) and ~1.7 s per scratch copy (restore). Wall-clock savings are sum ÷ ~10.4 (parallel factor),
tempered by less contention.

---

## Options considered

| Option | Expected wall saving | Risk / effort |
|---|---|---|
| **A. Shared registry for read-only fixtures** | ~30–45 s | Low; one-line change per test, exclusions listed below |
| **B. Class-scoped scratch copies with fold reset** | ~30–45 s | Medium; reset correctness must be proven per class |
| **C. Skip restore for package-free fixtures** | ~20–25 s | Low; per-fixture validation required |
| **D. Read-only tests stop using scratch copies** (use the shared committed fixture) | ~10–20 s | Low; audit of which tests only read |
| **E. Warm-snapshot seeding** (fresh instance from a cached `Solution`, no MSBuild load) | ~35–50 s | High; needs test-only seams in `SolutionWorkspace`/`RoslynInstance` |
| **F. Merge fixtures into one solution** | ≈ 0 beyond A | High churn; changes fixture semantics and breaks solution-shape tests |
| **G. Raise xUnit parallelism** | negative | Measured: causes analyzer-budget and rebuild-timeout failures |

Recommended: **A + C + D now, B next (biggest classes first), E only if more is needed.**

---

## Phase A — shared registry for read-only fixture tests (recommended first)

**Objective:** every read-only test that uses a committed fixture shares one loaded instance, so
the fixture loads once per run and Roslyn's compilation/index caches persist across tests.

**Files:**
- Modify: `Source/App/Morris.Roslynk.Tests/TestSolutions.cs` (add the shared registry + helper).
- Modify: the read-only test files listed below (mechanical, one line per test).

**Step A1 — add to `TestSolutions`:**

```csharp
// One registry for the whole test process. Committed fixtures are read-only, so tests that only
// read can share the loaded snapshot (and Roslyn's compilation and SymbolFinder caches). Never
// dispose it: a disposed registry would tear down instances other tests are using.
private static readonly InstanceRegistry SharedRegistry = new();

/// <summary>The process-wide registry for read-only fixture tests. Do not dispose.</summary>
public static InstanceRegistry Shared => SharedRegistry;

/// <summary>Returns the shared registry with <paramref name="solutionPath"/> loaded and ready.</summary>
public static async Task<InstanceRegistry> SharedAsync(string solutionPath)
{
	await SharedRegistry.GetOrAddAsync(solutionPath);
	return SharedRegistry;
}
```

**Step A2 — convert the call site** in each read-only test:

```csharp
// before
using var registry = new InstanceRegistry();
await registry.GetOrAddAsync(TestSolutions.Simple);

// after
var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
```

No `using`: the registry lives for the run. (Do not add `using` — that would dispose it.)

**Step A3 — do NOT convert these (they need a fresh instance):**

- Cold-load / `Indexing` tests: every use of `new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero))`
  (25 occurrences; one per tool plus `RenameSymbolTests`, `ApplyCodeFixTests`, `ApplyPatchTests`, etc.).
- Dirty / watcher-state tests: `Infrastructure/Watching/SolutionFileSyncTests.cs` (43 tests — they
  `MarkDirty`/`DetachWatcher` and say so in a comment), `Infrastructure/Lifecycle/RoslynInstanceTests`.
- Registry lifecycle: `Infrastructure/Lifecycle/InstanceRegistryTests/*`
  (`GetOrAddAsyncTests`, `GetOrBeginTests`, `OpenSolutionPathsTests`, `InstanceRegistryTests`).
- Reload/open-state: `Features/Solutions/ReloadSolutionTests`, `OpenSolutionTests` (no-wait
  `Building`), `GetSolutionStatusTests` cases asserting `Building`/`loadDiagnostics`.
- Metrics state: `Infrastructure/Observability/SolutionMetricsTests` (asserts `error=Computing`,
  which needs a fresh snapshot).
- Analyzer swap: `Infrastructure/Workspaces/TrackedAnalyzersTests/*`.
- Any test that edits a committed fixture (none today; keep the rule).

**Expected saving:** ~146 of the 223 direct loads disappear; at ~2.4 s each (load + first
compilation/index) that is ~350 s of summed time, ~35 s wall. Diagnostics tests additionally reuse
`DiagnosticsService`'s per-instance memo (the first `GetDiagnostics` compile per project/mode is
shared), worth a further ~50 s summed.

**Risk:** cross-test coupling through per-instance state (diagnostics memo, watcher). Mitigated by
the exclusion list; fixtures are never written, so the snapshot cannot drift. A test that
accidentally leaves the shared instance dirty would poison later tests — add an assertion helper
(`Assert.False(instance.IsDirty)`) in the shared-load helper for tests that opt in.

---

## Phase B — one scratch solution per test class, reset between tests

**Objective:** the ~170 scratch copies become ~40 (one per test class that writes), and each class
loads its copy once.

**Design:** a reusable `ScratchSolutionFixture` base (or generic fixture) that:
1. Creates one scratch copy per class (source-only, restored once) — reuse `TestSolutions.CreateScratch`.
2. Loads it once in `InitializeAsync` and keeps one `InstanceRegistry`.
3. Resets before each test:
   - re-copy the pristine fixture tree over the scratch tree (delete extra files, restore originals),
   - then bring the snapshot back in sync by calling `SolutionFileSync.OnFileChangedAsync(path)`
     for each changed/added/deleted path — the same fold the watcher uses, no reload,
   - if the instance reports `IsDirty` (a build-file edit), fall back to `registry.ReloadAsync` and
     accept the cost for that class only.
4. Verifies the reset: `IsDirty == false` and every document's text equals disk text.

**Target classes first (largest scratch users):** `ApplyPatchTests` (19 copies),
`GetDiagnosticsTests` (18), `RazorSourceToolTests` (16), `RazorRenameTests` (14),
`SolutionFileSyncTests` (35 — likely keep per-test, it tests watcher state), `RenameSymbolTests`,
`ApplyCodeFixTests`, `RemoveUnusedUsingsTests`, `ChangeSignatureTests`, `ExtractMethodTests`.

**Expected saving:** ~125 loads × ~2.6 s (load + first compile) ≈ 325 s summed, ~30 s wall; plus
~125 fewer restores if Phase C is in place.

**Risk:** reset correctness (added/renamed/deleted files, `.razor` regeneration, build-file edits).
Mitigate by landing this per class, verifying with the existing per-test assertions, and keeping
classes that edit `.csproj` or build generator DLLs on per-test copies.

---

## Phase C — skip `dotnet restore` for package-free fixtures

**Objective:** ~140 of the ~170 scratch restores (and 9 of the 17 committed-fixture restores) are
unnecessary.

**Files:** `Source/App/Morris.Roslynk.Tests/TestSolutions.cs` (`Prepare` and `CreateScratch`).

**Design:**

```csharp
// Fixtures whose projects have PackageReference/FrameworkReference or netstandard targets need
// NuGet assets before MSBuildWorkspace's design-time build can resolve them.
private static readonly HashSet<string> FixturesNeedingRestore = new(StringComparer.OrdinalIgnoreCase)
{
	"RazorSolution", "RazorInjectSolution", "BindSolution", "RazorMultiProjectSolution",
	"CodeStyleSolution", "GeneratorSolution", "MultiTargetSolution",
};
```

`Prepare`/`CreateScratch` call `Restore` only when the fixture is in that set.

**Validation required per fixture (already probed for Simple/Cshtml/MultiTarget):** load a
source-only copy and assert 0 compile errors before enabling the skip. Known-good: Simple, Broken,
References, Access, Dispose, Expression, LocalFunction, Conditional, LargeType, Cshtml.
Known-bad: Razor, RazorInject, Bind, RazorMultiProject, CodeStyle, Generator, MultiTarget.

**Expected saving:** ~140 × 1.69 s ≈ 238 s summed, ~23 s wall.

**Risk:** a fixture later gaining a package reference would silently lose it; the set is explicit
and covered by a test that asserts a package fixture still resolves its package.

---

## Phase D — read-only tests stop creating scratch copies

Audit tests that call a read-only tool (`get_code_actions`, `get_symbol`, `get_members`,
`search_symbols`, `find_references`, `get_callers`, `get_callees`, `get_diagnostics`,
`get_expression_info`, `get_type_hierarchy`, `find_definition`, `find_implementations`,
`get_symbol_body`, `find_dead_code`, `find_dead_conditionals`) against a scratch copy they never
modify, and point them at the committed fixture through `TestSolutions.SharedAsync`. `RazorSourceToolTests`
alone creates 16 scratch copies, most for listing actions at a position. Estimated ~40 loads → ~10 s wall.

**Risk:** low, but each converted test must be checked that it does not first edit the copy.

---

## Phase E (optional, only if the above is not enough) — warm-snapshot seeding

Give tests a fresh `RoslynInstance` seeded from a cached, already-loaded `Solution`, so per-test
isolation is kept without an MSBuild load. This would also remove the ~43 watcher-test loads and the
lifecycle-adjacent loads that Phases A–D cannot share.

**Files:** `Infrastructure/Workspaces/SolutionWorkspace.cs` (internal factory taking a loaded
`Solution`, no `MSBuildWorkspace`), `Infrastructure/Lifecycle/RoslynInstance.cs` (internal seed that
publishes a `Ready` model and completes `ReadySignal`), plus a test helper. ~80 lines of production
code used only by tests (the assembly already exposes internals to the test project).

**Risk:** a seeded instance has no workspace to reload from and a fresh `RazorGenerationState`;
disposal and Razor-regeneration paths must be guarded. Only worthwhile if A–D leave the suite above
~4 minutes.

---

## Rejected: merge the fixtures into one solution

- Instance sharing already collapses repeated loads to one per fixture (17 loads for the whole
  engine suite); merging would reduce that to ~10 — a few seconds.
- Merging changes what tests assert: `get_solution_status` project counts, `projectName` filters,
  path resolution, and bare-name resolution across projects (a name unique in one fixture becomes
  ambiguous in a merged one).
- The fixtures deliberately differ in MSBuild shape (multi-target, Razor SDK, `EnforceCodeStyleInBuild`,
  conditional symbols, analyzer-only generator project); merging the projects into one solution keeps
  those projects but adds cross-project name collisions and slower scratch copies.
- Net: no measurable gain beyond Phase A, high churn.

---

## Expected outcome

| After | Wall (projected) | Reduction |
|---|---|---|
| Baseline | 326 s | — |
| A + C + D | ~250–265 s | ~20% |
| + B | ~215–230 s | ~30% |
| + E (optional) | ~200 s | ~39% |

Projections are sum ÷ parallel factor, so real numbers depend on contention; measure after each
phase with the same trx logger and compare per-class durations, not just the total.

---

## Validation

1. Baseline is already captured:
   `dotnet test Source/App/Morris.Roslynk.Tests/Morris.Roslynk.Tests.csproj -c Release --logger "trx;LogFileName=baseline.trx"`.
2. After each phase: same command, `baseline.trx` vs `after.trx`; compare total wall, per-class sums,
   and the count of tests > 5 s. Require 0 failures (the suite is 980/980 green today).
3. After Phase A: run the suite twice in a row and once with `--filter` on a single fixture area, to
   prove shared instances do not leak state between tests.
4. After Phase B: for each converted class, run the class alone and then the whole suite; the
   class's tests must pass in both orders.
5. After Phase C: run the fixtures whose restore was skipped (Simple, References, Access, Dispose,
   Expression, LocalFunction, Conditional, Broken, LargeType, Cshtml) and assert the same results.
6. `Morris.Roslynk.McpTests` is already 5 s; leave it alone.

---

## Risks and open questions

- **Test-only seams in production code (Phase E):** acceptable? The repo already has internal test
  hooks (`RoslynInstance.DetachWatcher`, `SolutionFileSync.RaiseErrorForTest`), so this fits, but it
  is the only option that touches engine code.
- **Reset via fold (Phase B):** the fold path is the watcher's own; it must be proven for added,
  renamed and deleted files, and for `.razor`/`.cshtml` changes that regenerate output.
- **Shared instance and dirty state (Phase A):** one test leaving the shared instance dirty would
  affect later tests. The exclusion list covers the known cases; the helper should assert not-dirty.
- **Analyzer budget under load:** the 5 s budget in `DocumentDiagnosticsProvider` already fails at
  higher parallelism. Reducing loads lowers contention, so this should improve, but it is a latent
  flake source worth noting separately.
- **No `releases.md` entry:** this is test infrastructure, not a user-facing feature change. If you
  want it recorded anyway, it belongs under `# Unreleased` as one line.

## Decision points

1. **Scope:** A + C + D first (low risk, ~20%), then B (medium, ~10% more), then E only if needed?
   Recommended: yes.
2. **Phase B reset strategy:** fold-based reset (fast, more machinery) vs. reload per test (simple,
   no saving). Recommended: fold-based, landed per class.
3. **Phase C allowlist:** explicit `FixturesNeedingRestore` set vs. probing each fixture at prepare
   time (more robust, more code). Recommended: explicit set plus one guard test.
4. **Phase E:** accept ~80 lines of internal test-only engine code for ~15 s more? Recommended: defer
   until A–D are measured.
5. **Merge fixtures:** confirm "no" — sharing gives the reuse the issue asks for without changing
   fixture semantics.


---

## Hypothesis testing

**Measurement note.** The first round of measurements was contaminated by unrelated background work on
the machine (the same unmodified baseline measured 326 s, then 1,152 s, then 262 s across sessions).
Every number below was re-measured after that, sequentially, on an otherwise idle machine (total CPU
7–15%), in one session, with the same code and the same 980 tests. Every run passed 980/980. The
earlier contaminated figures are discarded; where a hypothesis's first-round result changed, the
correction is called out.

Experimental worktrees (all off `master`, none merged):

- `C:\Data\ZZZScratch\Source\Roslynk\160` — pristine baseline (deliverable branch `160/PM-ReduceTestTimes`, plan only)
- `C:\Data\ZZZScratch\Source\Roslynk\160-h3` — H3 only
- `C:\Data\ZZZScratch\Source\Roslynk\160-h1h3` — H3 + H1
- `C:\Data\ZZZScratch\Source\Roslynk\160-exp` — H1 + H2 + H3 + H4 + H5 (all-in)

### Full-suite results (same session, idle machine)

| Configuration | Wall | Test duration | Sum of test durations | Tests |
|---|---|---|---|---|
| Baseline | **262.8 s** | 4 m 20 s | 3,454 s | 980/980 |
| H3 only (skip restore) | **262.6 s** (−0%) | 4 m 20 s | 3,134 s (−9%) | 980/980 |
| H3 + H1 (shared registry) | **231.9 s** (−11.8%) | 3 m 49 s | 2,507 s (−27%) | 980/980 |
| All-in (H1+H2+H3+H4+H5) | **179.8 s** (−31.6%) | 2 m 57 s | 2,071 s (−40%) | 980/980 |

### Per-hypothesis results

| # | Hypothesis | What was implemented | Measured outcome | Verdict |
|---|---|---|---|---|
| H1 | A shared registry lets read-only fixture tests reuse one loaded instance | `TestSolutions.Shared` / `SharedAsync`; 128 call sites converted; cold-load/dirty/lifecycle/metrics tests left on private registries | Full suite −11.8% (262.8 → 231.9 s); `GetCalleesTests` 17 → 8 s (−53%) | Confirmed |
| H2 | One scratch copy per test class, reset via the watcher's fold, avoids per-test loads and restores | `SharedScratch` helper (restore pristine tree + `SolutionFileSync.OnFilesChangedAsync` fold + snapshot/disk assertion); `ApplyPatchTests` converted (4 fixtures, 20 tests) | `ApplyPatchTests` 46 → 15 s (−67%), 20/20; full-suite class sum 135.4 → 4.6 s | Confirmed, with caveats |
| H3 | Package-free fixtures need no `dotnet restore` | `FixturesNeedingRestore` allowlist in `TestSolutions.Prepare` / `CreateScratch` | Full suite 262.6 s vs 262.8 s: **no wall-time gain** (restores overlap with test work on an idle machine); summed test time −9% | Not a wall-clock win (first round's −32% was contamination) |
| H4 | Read-only tests can share one prepared copy instead of each making a scratch copy | Prepared shared Razor copy (same edits `CreateRazorSolutionAsync` applies) + committed `Cshtml` fixture; 7 read-only `RazorSourceToolTests` converted | `RazorSourceToolTests` 65 → 43 s (−34%), 26/26 | Confirmed |
| H5 | A fresh instance can be seeded from a cached `Solution`, keeping isolation without an MSBuild load | Internal `SolutionWorkspace.FromSnapshot`, `RoslynInstance.Seed`, `InstanceRegistry.Seed`, `TestSolutions.SeededAsync`; 8 `SolutionFileSyncTests` + `GetCalleesTests` converted | `SolutionFileSyncTests` 100 → 61 s (−39%), 49/49; `GetCalleesTests` 17 → 8 s (same speed as H1) | Confirmed, but invasive |
| H6 | Merging the package-free fixtures into one solution saves load time | Built a merged 9-project `.slnx` in scratch and loaded it | One merged load 2,458 ms vs 9 individual loads 9,636 ms (3.9× for one pass). With a shared registry the 9 fixtures already load once (≈9.6 s), so merging saves ≈7.2 s per run (≈4% of the optimized suite) and changes fixture semantics | Rejected |
| H7 | More (or fewer) xUnit threads speed the suite up | Ran the unmodified suite with `MaxParallelThreads` 8 and 32, idle machine | 8 threads: 316.7 s (+20%); 32 threads: 311.8 s (+19%), 980/980; default (16): 262.8 s. The first round's 24 m/4 failures at 32 threads were contamination, not a real effect | Rejected — default is fastest |

### Per-class paired A/B runs (same machine state, back-to-back, `--no-build`)

| Class | Baseline | Optimized | Change | Tests |
|---|---|---|---|---|
| `ApplyPatchTests` (H2) | 46 s | 15 s | −67% | 20/20 |
| `SolutionFileSyncTests` (H3+H5) | 100 s | 61 s | −39% | 49/49 |
| `GetCalleesTests` (H1/H5) | 17 s | 8 s | −53% | 10/10 |
| `RazorSourceToolTests` (H4) | 65 s | 43 s | −34% | 26/26 |

### What the experiments exposed

- **Late-added files need an explicit fold.** `ApplyPatchTests.WhenASuffixMatchesTwoDistinctFiles...`
  creates two `.cs` files after the instance is loaded; the shared snapshot did not see them and the
  tool returned `NotFound` instead of `Ambiguous`. Calling `SolutionFileSync.OnFilesChangedAsync` on
  the new paths after creating them fixed it. The reset helper needs to expose that step, or such
  tests must keep a per-test copy.
- **Build-file edits make the instance dirty.** A test that patches a `.csproj` leaves the shared
  scratch instance dirty; the next reset reloads it (accepted cost). Classes that edit build files
  should keep per-test copies.
- **Restore cost does not translate to wall time on an idle machine.** H3 removes ~9% of summed test
  time but nothing from wall time, because restores overlap with other classes' test work. It may
  still help a constrained CI host or a slower machine; it is cheap to keep but is not a headline win.
- **Line endings:** the experimental edits left some files LF-only; a real change must keep the repo's
  CRLF convention.

## Recommendations

1. **Adopt H1 (shared registry) first.** Mechanical (128 call sites), low risk, −11.8% on the full
   suite and −53% on a read-only class. Keep the documented exclusion list (cold-load/`Indexing`,
   dirty/watcher, lifecycle/registry, reload/open status, metrics, tracked analyzers) and make the
   shared helper assert `!instance.IsDirty` for the tests that opt in.
2. **Adopt H2 for the largest write classes, starting with `ApplyPatchTests`.** Biggest per-class win
   (−67%) but needs the reset helper hardened: explicit fold for files created after load, reload for
   build-file edits, and a snapshot/disk assertion after each reset. Then apply it to
   `RazorRenameTests`, `GetDiagnosticsTests` and the write half of `RazorSourceToolTests`.
3. **Adopt H4 for read-only tests that need a prepared fixture** (the 7 `RazorSourceToolTests`
   listings/not-found/check-only tests): small, safe, −34% on that class.
4. **Defer H5 unless more isolation is needed.** It works (−39% on the watcher class) but adds ~40
   lines of internal engine seams (`FromSnapshot`/`Seed`); for read-only tests it gives the same speed
   as H1. Use it only for watcher/dirty tests that cannot share an instance.
5. **Treat H3 as optional.** It does not reduce wall time on an idle machine (262.6 s vs 262.8 s) but
   removes ~9% of summed test time and may help a loaded or constrained CI host. Keep it only if that
   matters; do not present it as a headline win.
6. **Do not merge fixtures and do not change parallelism.** H6 saves ≈4% once instances are shared and
   changes fixture semantics; H7 measured 19–20% slower with both fewer and more threads. Keep the
   default (cores).
7. **Expected result:** H1 + H2 + H4 (and H5 if adopted) measured **−31.6%** end-to-end (262.8 →
   179.8 s) with the suite still 980/980. The class-level numbers show where the remaining time is:
   `RazorSourceToolTests` (121 s), `SolutionFileSyncTests` (99.5 s), `MultiQueryLimitTests` (88.5 s)
   and `GeneratorDiagnosticsTests` (75.5 s) are the next targets, and they are dominated by Roslyn
   compilation/analyzer work rather than loads.
8. **Keep a timing baseline in CI.** Emit the trx per-class durations and compare after each phase;
   machine drift and background load are large enough that only paired/back-to-back runs are
   trustworthy for attribution.
