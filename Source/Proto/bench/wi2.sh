#!/usr/bin/env bash
# wi2.sh <same|test|core-body|core-signature> <shared|separate|load>: one WorktreeShare run against worktree B with the case's edit.
set -u
CASE=$1; MODE=$2; OUT=/workspaces/bench/wi2/$CASE-$MODE
A=/workspaces/corral-ai; B=/workspaces/share-b
rm -rf "$OUT"; mkdir -p "$OUT"
git -C "$B" checkout -q -- .
case $CASE in
  same) ;;
  test) python3 - "$B/tests/Corral.Feature.Todo.Tests/TodoToolsTests.cs" <<'PY'
import sys; p=sys.argv[1]; s=open(p).read()
open(p,"w").write(s + "\ninternal static class SharedRoslynProbe\n{\n    public static int Value() => 1;\n}\n")
PY
  ;;
  core-body) python3 - "$B/core/Corral.Core/Jobs/JobsApi.cs" <<'PY'
import sys; p=sys.argv[1]; s=open(p).read()
old="        return TypedResults.Ok(JobModel.From(job));\n    }\n}"
assert old in s; open(p,"w").write(s.replace(old, "        _ = JobModel.From(job);\n" + old, 1))
PY
  ;;
  core-signature) python3 - "$B/core/Corral.Core/Jobs/JobWatchdogFunctions.cs" <<'PY'
import sys; p=sys.argv[1]; s=open(p).read()
old='public static string InstanceIdFor(int jobId) => $"job-watchdog-{jobId}";'
assert old in s; open(p,"w").write(s.replace(old, 'public static string InstanceIdFor(int jobId, string prefix = "job-watchdog") => $"{prefix}-{jobId}";', 1))
PY
  ;;
esac
git -C "$B" diff --stat | tail -1 > "$OUT/diff"
echo "" > "$OUT/roots"
bash /workspaces/bench/rss-sampler.sh "$OUT/roots" "$OUT/rss.tsv" & SAMPLER=$!
dotnet /workspaces/roslynk/Source/Proto/WorktreeShare/bin/Release/net10.0/WorktreeShare.dll "$MODE" "$A" "$B" "$OUT/report.json" 2> "$OUT/log" & P=$!
echo "$P" > "$OUT/roots"
wait $P; echo "exit $?" >> "$OUT/log"
sleep 1; kill $SAMPLER
git -C "$B" checkout -q -- .
awk '{ if ($2>m) m=$2 } END { printf "peak %.2f GB\n", m/1048576 }' "$OUT/rss.tsv" | tee "$OUT/peak"
tail -3 "$OUT/log"
