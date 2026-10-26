#!/usr/bin/env bash
# heap.sh <roslynk|roslynk-wks|proto> <run>: where a Roslynk process's memory goes, step by step. At each checkpoint:
# resident memory (process tree), the GC's own view (dotnet-counters), then a heap dump (dotnet-gcdump, which collects
# first) and resident memory after it.
set -u
MODE=$1; OUT=/workspaces/bench/heap/$2; rm -rf "$OUT"; mkdir -p "$OUT"
SLN=/workspaces/corral-ai/Corral.slnx
ROSLYNK=/workspaces/roslynk/Source/App/Morris.Roslynk.Mcp/bin/Release/net10.0/Morris.Roslynk.Mcp.dll
PROTO=/workspaces/roslynk/Source/Proto/SharedRoslynProto/bin/Release/net10.0/SharedRoslynProto.dll
T=~/.dotnet/tools
tree_rss() { ps -eo pid=,ppid=,rss= | awk -v r=$1 '{p[NR]=$1;pp[NR]=$2;s[NR]=$3} END{k[r]=1;c=1;while(c){c=0;for(i=1;i<=NR;i++) if(!(p[i] in k)&&(pp[i] in k)){k[p[i]]=1;c=1}} for(i=1;i<=NR;i++) if(p[i] in k) t+=s[i]; printf "%.2f", t/1048576}'; }
own_rss() { ps -o rss= -p $1 | awk '{printf "%.2f", $1/1048576}'; }
check() {
  local name=$1
  echo "== $name: tree $(tree_rss $P) GB, process $(own_rss $P) GB" | tee -a "$OUT/summary"
  $T/dotnet-counters collect -p $P --counters System.Runtime --format json -o "$OUT/$name.counters.json" --duration 00:00:03 > /dev/null 2>&1
  python3 - "$OUT/$name.counters.json" <<'PY' | tee -a "$OUT/summary"
import json,sys
try: d=json.load(open(sys.argv[1]))
except Exception as e: print("   counters: none", e); sys.exit()
last={}
for e in d.get("Events",[]): last[e["name"]]=e["value"]
for k,v in sorted(last.items()):
    if any(w in k.lower() for w in ("heap","committed","fragment","working","gen")): print(f"   {k}: {v}")
PY
  $T/dotnet-gcdump collect -p $P -o "$OUT/$name.gcdump" > /dev/null 2>&1
  echo "   after the dump's collection: process $(own_rss $P) GB" | tee -a "$OUT/summary"
  # The dump's full collection is now the last one, so the GC's heap size is the live heap.
  $T/dotnet-counters collect -p $P --counters System.Runtime --format json -o "$OUT/$name.after.json" --duration 00:00:03 > /dev/null 2>&1
  python3 -c "
import json,sys
d=json.load(open(sys.argv[1])); last={e['name']:e['value'] for e in d.get('Events',[])}
print('   after the dump, live heap %.2f GB, committed %.2f GB, fragmentation %.2f GB' % tuple(last.get(k,0)/2**30 for k in ('dotnet.gc.last_collection.heap.size (By)','dotnet.gc.last_collection.memory.committed_size (By)','dotnet.gc.last_collection.heap.fragmentation.size (By)')))" "$OUT/$name.after.json" | tee -a "$OUT/summary"
  $T/dotnet-gcdump report "$OUT/$name.gcdump" 2>/dev/null | head -30 > "$OUT/$name.top"
  head -4 "$OUT/$name.top" | tee -a "$OUT/summary"
}
rm -rf /tmp/Roslynk
case $MODE in
  roslynk|roslynk-wks)
    [ $MODE = roslynk-wks ] && export DOTNET_gcServer=0
    dotnet "$ROSLYNK" --Roslynk:Port=6502 > "$OUT/daemon.log" 2>&1 & P=$!; PORT=6502
    for i in $(seq 1 120); do curl -sf http://127.0.0.1:6502/version >/dev/null && break; sleep 0.25; done
    python3 /workspaces/bench/mcp.py "http://127.0.0.1:$PORT/" "$OUT/a1" "$SLN" none | tee -a "$OUT/summary"; check loaded
    python3 /workspaces/bench/mcp.py "http://127.0.0.1:$PORT/" "$OUT/a2" "$SLN" diagc | tee -a "$OUT/summary"; check compiler-diagnostics
    python3 /workspaces/bench/mcp.py "http://127.0.0.1:$PORT/" "$OUT/a3" "$SLN" diag | tee -a "$OUT/summary"; check analyzer-diagnostics
    ;;
  proto)
    dotnet "$PROTO" "$SLN" "$OUT/graph.json" 6602 > "$OUT/graph.out" 2> "$OUT/graph.err" & P=$!; PORT=6602
    for i in $(seq 1 1200); do grep -q @@ready "$OUT/graph.out" 2>/dev/null && break; sleep 0.5; done; check graph
    python3 /workspaces/bench/mcp.py "http://127.0.0.1:$PORT/" "$OUT/a3" "$SLN" diag | tee -a "$OUT/summary"; check analyzer-diagnostics
    ;;
esac
kill $P
