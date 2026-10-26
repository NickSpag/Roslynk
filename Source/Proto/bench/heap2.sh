#!/usr/bin/env bash
# heap2.sh <roslynk|proto> <run>: Roslynk's engine alone (roslynk) or with the graph (proto), its memory at each step
# as the GC sees it (/gc: before and after a full compacting collection), and resident memory.
set -u
MODE=$1; OUT=/workspaces/bench/heap/$2; rm -rf "$OUT"; mkdir -p "$OUT"
SLN=/workspaces/corral-ai/Corral.slnx
PROTO=/workspaces/roslynk/Source/Proto/SharedRoslynProto/bin/Release/net10.0/SharedRoslynProto.dll
rm -rf /tmp/Roslynk
dotnet "$PROTO" "$SLN" "$OUT/graph.json" 6602 $([ $MODE = roslynk ] && echo nograph) > "$OUT/graph.out" 2> "$OUT/graph.err" & P=$!
check() { echo "== $1" | tee -a "$OUT/summary"; curl -s http://127.0.0.1:6602/gc | tee -a "$OUT/summary"; echo | tee -a "$OUT/summary"; }
for i in $(seq 1 120); do curl -sf http://127.0.0.1:6602/version >/dev/null && break; sleep 0.25; done
if [ $MODE = proto ]; then for i in $(seq 1 1200); do grep -q @@ready "$OUT/graph.out" 2>/dev/null && break; sleep 0.5; done; check graph; fi
python3 /workspaces/bench/mcp.py "http://127.0.0.1:6602/" "$OUT/a1" "$SLN" none | tee -a "$OUT/summary"; check loaded
python3 /workspaces/bench/mcp.py "http://127.0.0.1:6602/" "$OUT/a2" "$SLN" diagc | tee -a "$OUT/summary"; check compiler-diagnostics
python3 /workspaces/bench/mcp.py "http://127.0.0.1:6602/" "$OUT/a3" "$SLN" diag | tee -a "$OUT/summary"; check analyzer-diagnostics
kill $P
