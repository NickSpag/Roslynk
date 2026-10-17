---
tool: apply_patch
---
Applies content-anchored git unified diffs to existing text files. Targets: solution-compiled
.cs/.razor/.cshtml (model synchronized), plus other text files under the solution folder
(e.g. .csproj/.json/.md). Creation/deletion, binary files and paths outside that folder:
NotSupported; missing files: NotFound.

Header-only result: applied=<Y|N>. Hunk headers may omit line numbers (bare '@@'); each hunk
must match exactly once, so provide unique context. Unique-suffix paths also resolve.
Ambiguous paths return candidate=<path>; rejections list rejected=<path>.
Unmatched/ambiguous hunks: Conflict with file/reason; no file sections: Invalid.

baseVersions pairs patch paths with documentVersion: SHA-256 hex of loaded text encoded as
UTF-8 without BOM. No read tool reports this hash; supply only if you can compute it exactly.
Mismatches return Stale with stale=<path>.

## Parameters

- solutionId: Solution handle.
- patch: Git unified diff (---/+++/@@).
- baseVersions: Patch paths and expected documentVersion hashes.
- checkOnly: Preview only.
