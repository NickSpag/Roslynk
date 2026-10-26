#!/usr/bin/env bash
# wi1.sh <separate|shared|shared-concurrent> <run-name>
# separate: today's two processes (the analyzer's --serve, then Roslynk's daemon); shared: the prototype.
set -u
MODE=$1; OUT=/workspaces/bench/runs/$2
rm -rf "$OUT"; mkdir -p "$OUT/answers"
SLN=/workspaces/corral-ai/Corral.slnx
EDIT=/workspaces/corral-ai/core/Corral.Core/Jobs/JobsApi.cs
ANALYZER=/workspaces/corral-ai/tools/Corral.Tools.Analyzer/bin/Release/net10.0/Corral.Tools.Analyzer.dll
ROSLYNK=/workspaces/roslynk/Source/App/Morris.Roslynk.Mcp/bin/Release/net10.0/Morris.Roslynk.Mcp.dll
PROTO=/workspaces/roslynk/Source/Proto/SharedRoslynProto/bin/Release/net10.0/SharedRoslynProto.dll
: > "$OUT/roots"
mark() { echo "$(date +%s.%N | cut -c1-14) $*" >> "$OUT/timeline"; echo "[$(date +%T)] $*"; }
waitfor() { for i in $(seq 1 3600); do grep -q "$2" "$1" 2>/dev/null && return 0; sleep 0.25; done; return 1; }
bash /workspaces/bench/rss-sampler.sh "$OUT/roots" "$OUT/rss.tsv" & SAMPLER=$!
rm -rf ~/.corral/change-graph /tmp/Roslynk
git -C /workspaces/corral-ai checkout -q -- "$EDIT"
mark start
if [ "$MODE" = separate ]; then
  mkfifo "$OUT/in"; exec 3<>"$OUT/in"
  dotnet "$ANALYZER" --solution "$SLN" --symbols-output "$OUT/graph.json" --serve < "$OUT/in" > "$OUT/graph.out" 2> "$OUT/graph.err" & GRAPH=$!
  echo -n "$GRAPH " >> "$OUT/roots"
  waitfor "$OUT/graph.out" "@@ready"; mark graph-ready
  dotnet "$ROSLYNK" --Roslynk:Port=6502 > "$OUT/roslynk.log" 2>&1 & RK=$!
  echo -n "$RK " >> "$OUT/roots"; PORT=6502
  for i in $(seq 1 120); do curl -sf http://127.0.0.1:6502/version >/dev/null && break; sleep 0.25; done; mark roslynk-up
else
  dotnet "$PROTO" "$SLN" "$OUT/graph.json" 6602 > "$OUT/graph.out" 2> "$OUT/graph.err" & GRAPH=$!; RK=$GRAPH
  echo -n "$GRAPH " >> "$OUT/roots"; PORT=6602
  for i in $(seq 1 120); do curl -sf http://127.0.0.1:6602/version >/dev/null && break; sleep 0.25; done; mark proto-up
  if [ "$MODE" = shared ]; then waitfor "$OUT/graph.out" "@@ready"; mark graph-ready; fi
fi
mark session-start
python3 /workspaces/bench/mcp.py "http://127.0.0.1:$PORT/" "$OUT/answers" "$SLN" | tee "$OUT/session.tsv"
mark session-done
if [ "$MODE" = shared-concurrent ]; then waitfor "$OUT/graph.out" "@@ready"; mark graph-ready; fi
cp "$OUT/graph.json" "$OUT/graph-before-edit.json"
# One edit: a line added to the get-job endpoint's body, so the method's calls and every later line move.
python3 - "$EDIT" <<'PY'
import sys; p=sys.argv[1]; s=open(p).read()
old="        return TypedResults.Ok(JobModel.From(job));\n    }\n}"
assert old in s; open(p,"w").write(s.replace(old, "        _ = JobModel.From(job);\n" + old, 1))
PY
mark edited
if [ "$MODE" = separate ]; then echo "{\"changed\":[\"$EDIT\"]}" >&3; fi
waitfor "$OUT/graph.out" "@@updated"; mark graph-updated
sleep 5
# The agent sees the edit too: callers of JobModel.From after the watcher folded it in.
python3 /workspaces/bench/mcp.py "http://127.0.0.1:$PORT/" "$OUT/answers-after-edit" "$SLN" refs,callers | tee "$OUT/session-after-edit.tsv"
mark after-edit-done
ps -o rss= -p $GRAPH > "$OUT/rss-graph-end"; ps -o rss= -p $RK > "$OUT/rss-roslynk-end"
git -C /workspaces/corral-ai checkout -q -- "$EDIT"
kill $GRAPH $RK 2>/dev/null; sleep 2; kill $SAMPLER
awk '{ for (i=2;i<=NF;i++) if ($i>m[i]) m[i]=$i } END { printf "peak total %.2f GB; per process", m[2]/1048576; for (i=3;i<=NF;i++) printf " %.2f", m[i]/1048576; print " GB" }' "$OUT/rss.tsv" | tee "$OUT/peak"
grep -h "Timing\|Loaded\|@@" "$OUT/graph.err" "$OUT/graph.out" | tail -8
