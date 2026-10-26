# Prototypes for corral-ai's shared Roslyn process plan

Not for merge. Measurements for corral-ai `docs/plans/control-room/2026-10-25-shared-roslyn-process.md`
(corral-ai#3370), on Roslynk 3.0.0 (0ab3b07).

- `SharedRoslynProto` (WI-1): Roslynk's engine and MCP tools hosted beside the change graph analyzer
  (corral-ai `tools/Corral.Tools.Analyzer/SymbolGraph`, compiled in from a corral-ai checkout via `CorralRoot`).
  Opens the solution through `InstanceRegistry`, walks the graph from `ReadModelAsync()` snapshots and polls for new ones.
  `dotnet SharedRoslynProto.dll <solution> <graph.json> <port>`.
- `WorktreeShare` (WI-2): two worktrees loaded from hayloft's kept compiler command lines under one placeholder root,
  the second as a version of the first, against two separate solutions. `dotnet WorktreeShare.dll <shared|separate|load> <rootA> <rootB> <report.json>`.
- `bench/`: the scripts that ran them on a corral-ai codespace (`wi1.sh`, `wi2.sh`, an MCP client, a graph comparison, an RSS sampler).

The repository root's `nuget.config` names nuget.org only.
