#!/usr/bin/env python3
"""graphdiff.py a.json b.json: node-for-node, edge-for-edge comparison of two change graphs (ids, not indices)."""
import json, sys
def load(p):
    g = json.load(open(p))
    nodes = g["nodes"]; ids = [n["id"] for n in nodes]
    kinds = g.get("edgeKinds", [])
    out = {"nodes": {n["id"]: json.dumps(n, sort_keys=True) for n in nodes},
           "edges": {(ids[e[0]], ids[e[1]], kinds[e[2]] if kinds else e[2], tuple(e[3:]) if len(e) > 3 else ()) for e in g["edges"]}}
    names = g.get("inputNames", [])
    out["flows"] = {tuple(ids[x] for x in row[:3]) + tuple(row[3:]) for row in g.get("flows", [])}
    out["inputs"] = {json.dumps([ids[row[0]], ids[row[1]], row[2]] + [names[i] for i in row[3:]]) for row in g.get("inputs", [])}
    out["urls"] = {(ids[u["member"]], u["line"], u["path"]) for u in g.get("urls", [])}
    return g, out
ga, a = load(sys.argv[1]); gb, b = load(sys.argv[2])
same = True
for key in ("nodes", "edges", "flows", "inputs", "urls"):
    if key == "nodes":
        only_a = set(a[key]) - set(b[key]); only_b = set(b[key]) - set(a[key])
        diff = [k for k in set(a[key]) & set(b[key]) if a[key][k] != b[key][k]]
        print(f"nodes: {len(a[key])} vs {len(b[key])}; only first {len(only_a)}, only second {len(only_b)}, fields differ {len(diff)}")
        for k in sorted(only_a)[:8]: print("  - only first:", k)
        for k in sorted(only_b)[:8]: print("  - only second:", k)
        for k in sorted(diff)[:5]: print("  - differs:", a[key][k][:300], "\n           ", b[key][k][:300])
        same &= not (only_a or only_b or diff)
    else:
        only_a = a[key] - b[key]; only_b = b[key] - a[key]
        print(f"{key}: {len(a[key])} vs {len(b[key])}; only first {len(only_a)}, only second {len(only_b)}")
        for k in sorted(map(str, only_a))[:8]: print("  - only first:", k[:300])
        for k in sorted(map(str, only_b))[:8]: print("  - only second:", k[:300])
        same &= not (only_a or only_b)
print("IDENTICAL" if same else "DIFFERENT")
