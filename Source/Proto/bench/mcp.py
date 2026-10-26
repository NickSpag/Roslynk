#!/usr/bin/env python3
"""mcp.py <base-url> <out-dir> <solution>: one agent session against a Roslynk MCP endpoint, every page of every answer saved."""
import json, sys, time, urllib.request, os, re

base, out, solution = sys.argv[1], sys.argv[2], sys.argv[3]
os.makedirs(out, exist_ok=True)
session = None
seq = 0

def post(body):
    global session
    data = json.dumps(body).encode()
    req = urllib.request.Request(base, data=data, method="POST", headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
        **({"Mcp-Session-Id": session} if session else {}), "MCP-Protocol-Version": "2025-06-18"})
    with urllib.request.urlopen(req, timeout=3600) as r:
        session = r.headers.get("Mcp-Session-Id") or session
        raw = r.read().decode()
        if r.headers.get("Content-Type", "").startswith("text/event-stream"):
            msgs = [json.loads(l[5:]) for l in raw.splitlines() if l.startswith("data:") and l[5:].strip()]
            return next((m for m in msgs if "id" in m), None)
        return json.loads(raw) if raw.strip() else None

def call(name, args):
    global seq
    seq += 1
    r = post({"jsonrpc": "2.0", "id": seq, "method": "tools/call", "params": {"name": name, "arguments": args}})
    if "error" in r: return "ERROR " + json.dumps(r["error"])
    return "".join(c.get("text", "") for c in r["result"]["content"])

def paged(name, args, label):
    pages, start, t0 = [], 1, time.time()
    while True:
        text = call(name, {**args, "startItem": start})
        pages.append(text)
        m = re.search(r"^next(?:StartItem)?=(\d+)", text, re.M) or re.search(r"nextStartItem=(\d+)", text)
        if not m or len(pages) > 500: break
        start = int(m.group(1))
    took = time.time() - t0
    with open(os.path.join(out, label + ".txt"), "w") as f: f.write("\n\f\n".join(pages))
    print(f"{label}\t{took:.1f}s\t{len(pages)} page(s)\t{sum(len(p) for p in pages)} chars", flush=True)
    return pages

seq += 1
post({"jsonrpc": "2.0", "id": seq, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "bench", "version": "1"}}})
post({"jsonrpc": "2.0", "method": "notifications/initialized"})
t0 = time.time()
# An agent retries open_solution while it reports the load still building (each call waits up to Roslynk's 30 s).
while True:
    opened = call("open_solution", {"solutionPath": solution})
    if "status=Building" not in opened or time.time() - t0 > 1800: break
print(f"open_solution\t{time.time()-t0:.1f}s", flush=True)
with open(os.path.join(out, "open_solution.txt"), "w") as f: f.write(opened)
sid = re.search(r"solutionId=(\S+)", opened).group(1)
steps = sys.argv[4].split(",") if len(sys.argv) > 4 else ["diag", "refs", "callers"]
if "diagc" in steps:
    paged("get_diagnostics", {"solutionId": sid, "includeErrors": True, "includeWarnings": True, "includeAnalyzers": False}, "diagnostics-compiler")
if "diag" in steps:
    paged("get_diagnostics", {"solutionId": sid, "includeErrors": True, "includeWarnings": True}, "diagnostics")
for sym, label in [("Corral.Core.Jobs.JobModel.From", "JobModel.From"), ("Corral.Core.Jobs.JobModel", "JobModel")]:
    if "refs" in steps: paged("find_references", {"solutionId": sid, "symbolName": sym}, f"find_references {label}")
    if "callers" in steps: paged("get_callers", {"solutionId": sid, "symbolName": sym}, f"get_callers {label}")
