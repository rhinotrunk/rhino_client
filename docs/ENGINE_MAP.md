# Engine map

Where every element of the RhinoTrunk panel gets its data from git_engine (prototype v0.1). The types and adapters are in [`rhino/panel/src/engine/map.ts`](../rhino/panel/src/engine/map.ts). Engine references: `git_engine/docs/API.md`, `CLIENT_GUIDE.md` and `src/RhinoTrunk.Contracts/Dtos.cs`.

## How data flows

```
Panel (React)  --PanelCommand-->  C# plugin  --RhinoTrunkClient (HTTP)-->  git_engine :5280
Panel (React)  <--HostEvent-----  C# plugin  <--DTOs----------------------
```

- The panel never calls HTTP. C# owns the network, the document and the viewport, as `AGENTS.md` asks.
- C# runs every HTTP call off Rhino's UI thread and returns results with `RhinoApp.InvokeOnUiThread`.
- C# posts the engine DTOs mostly unchanged. The panel converts them with the adapters in `map.ts`.
- Working-copy state lives in `RhinoDoc.Strings`: `rhinotrunk.server`, `rhinotrunk.repo`, `rhinotrunk.branch`, `rhinotrunk.base`.

## Header and sync bar

| UI element | View field (`map.ts`) | Engine source | SDK call (C#) | Notes |
| --- | --- | --- | --- | --- |
| Repo name | `SyncState.repo` | `RhinoDoc.Strings["rhinotrunk.repo"]` | — | |
| Server + status dot | `SyncState.server`, `.connected` | last call succeeded? | `ListReposAsync()` as a ping | Red dot plus "Can't reach…" when it fails |
| Branch picker | `SyncState.branch` | `RefInfo[]` where `type == "branch"` | `ListRefsAsync(repo)` | |
| Pull count | `SyncState.behind` | commits in `graph(ref=branch)` before reaching `base` | `GraphAsync(repo, limit, [branch])` | 0 disables Pull |
| Push count | `SyncState.localChanges` | client-side status, no network | serial-number check, then hash compare | 0 disables Push |
| "fetched 2 min ago" | `SyncState.fetchedAt` | time of the last successful fetch | — | `syncLine()` formats the whole line |
| Fetch ⟳ | sends `sync.fetch` | `RefInfo.commit` for the branch | `GetBranchAsync(repo, branch)` | Updates `head`, then recomputes `behind` |
| Pull ↓ | sends `sync.pull` | `DiffResponse` base → head, then geometry | `DiffAsync(repo, base, head)`, `BatchGetAsync(…)` | Apply changes to the document; set `base = head` |
| Push ↑ → Commit & push | sends `sync.push { message }` | `CommitResponse` or `ApiError` | `UploadAsync(repo, blobs)`, then `CommitAsync(repo, CommitRequest{ Branch, ExpectedHead = base, Message, Author, Changes })` | On success set `base = response.commit`; on 409 show `describeError` |

## History (commit graph)

| UI element | View field (`CommitRow`) | Engine field (`GraphCommit`) | Call |
| --- | --- | --- | --- |
| Row order | array order | newest first, parents after children | `GET /repos/{repo}/graph?limit=200` |
| Lane | `lane` | computed from `parents` | `toCommitRows()` |
| Dot shape | `isMerge`, `isAgent` | `parents.length > 1`, `author.kind == "agent"` | |
| Message | `message` | `message` | |
| Author | `author` | `author.name ?? author.id` | |
| Time | `time` | `timestamp` (ISO) | `relativeTime()` |
| Branch badges | `branches` | `refs` without the `tag:` prefix | |
| Tag badges | `tags` | `refs` starting with `tag:` | |
| `+A ~M −D` counts | `counts` | **not in the graph**; needs one diff per visible commit | `DiffAsync(repo, parents[0], id)` | Fetch lazily for visible rows and cache by commit id |
| ✓ / ✕ check | `check` | `Annotation[]` | `ListAnnotationsAsync(repo, id)` → `toCheck()` | Optional (P1) |

## Commit detail

| UI element | View field | Engine field | Call |
| --- | --- | --- | --- |
| Title | `CommitRow.message` | `CommitInfo.message` | `GetCommitAsync(repo, id)` |
| Hash line | `shortHash(id)`, parent → commit | `CommitInfo.id`, `.parents[0]` | |
| Agent line | — | `CommitInfo.meta["agent.model"]` | |
| Change bar | `countChanges(rows)` | derived from the diff | |
| Object rows | `ObjectChangeRow[]` | `DiffEntry` where `kind == "object"` | `DiffAsync(repo, parents[0], id)` → `toObjectChanges()` |
| Op letter | `op` | `change` → A / M / D | |
| Name | `name` | **not in the diff**: attributes blob `name` of the `to` (or `from`) manifest | C# enriches: `GetEntityAsync(repo, rev, "object", id)` + `BatchGetAsync([components.attributes])` |
| Subtype | `subtype` | **not in the diff**: `Manifest.subtype` | same call as Name |
| Component badges | `components` | `DiffEntry.components` | |
| Field diffs (`− / +`) | `fields` | compare both attribute blobs | `BatchGetAsync([from.attributes, to.attributes])` on expand only |
| Text view | built from the rows | same data | client-side |
| Check out | sends `commit.checkout { id }` | full snapshot | `ListEntitiesAsync(repo, id, includeManifests: true)`, `BatchGetAsync` in chunks of 1000 |

C# sends the enriched diff as `DiffEntryWithInfo` so the panel never needs a second round trip for names.

## Viewport overlay

| Overlay | Data | Call |
| --- | --- | --- |
| Added / modified (new) | geometry blob of `drawVersion` manifest | `BatchGetAsync([manifest.components.geometry])` → `CommonObject.FromJSON` |
| Modified (previous, ghost) | geometry blob of `ghostVersion` | same; only when `components` contains `geometry` |
| Deleted (ghost) | geometry blob of the `from` manifest | same |
| Selection box | object selected in the panel (`object.select { id }`) | none; uses the geometry already loaded |

Cache geometry blobs by hash: content-addressed blobs never change.

## Branches and AI agents

| UI element | View field | Engine source | SDK call (C#) | Notes |
| --- | --- | --- | --- | --- |
| Branch list (picker) | `BranchInfo[]` | `RefInfo[]` where `type == "branch"`; kind from the `agent/` prefix (`branchKind()`) | `ListRefsAsync(repo)` | |
| Switch branch | sends `branch.switch { name }` | `RefInfo.commit` | `GetBranchAsync(repo, name)` | Updates `rhinotrunk.branch` and `base` |
| New human branch | sends `branch.create { name, from }` | new ref | `SetBranchAsync(repo, name, from, expected: null)` | |
| Start AI agent | sends `agent.start { branch, from, prompt, model, budget }` | new ref + agent process | `SetBranchAsync(...)`, then the agent harness | The agent commits with `author.kind = "agent"` |
| Stop agent | sends `agent.stop { branch }` | agent process | harness | Commits made so far stay on the branch |
| Merge into main | sends `branch.merge { theirs, into }` | `MergeResponse` | `MergeAsync(repo, MergeRequest{ Branch = into, ExpectedHead, Theirs })` | 409 `merge_conflict` → show `errors[].fields` |
| Live tokens on the run card | `AgentRun.inputTokens / outputTokens / total / used` | **proposed:** annotation `agent/run` (`status: pending`) on the branch head, `data` = `AgentRunData` | `AddAnnotationAsync` (agent), `ListAnnotationsAsync` (panel, polled every 1–2 s) | `toAgentRun()` |
| Step, status, commits | `AgentRun.step / status / commits` | same annotation | same | |
| Tokens per agent commit | `CommitRow.tokens` | **proposed:** `CommitInfo.meta["agent.tokens.input"]`, `["agent.tokens.output"]` | written in `CommitRequest.Meta` by the agent | `tokensFromMeta()` |
| Model and prompt | detail card | `meta["agent.model"]`, `meta["agent.prompt"]` | same | `AGENT_META` keys |

## Bridge messages

Planned types are in `map.ts` (`PlannedPanelCommand`, `PlannedHostEvent`). Move each one into `src/protocol/events.ts` and `ClientPanel.ai.cs` when you wire that feature.

| Panel → C# | Payload | C# does |
| --- | --- | --- |
| `sync.fetch` | — | refs + graph; replies `sync.state`, `graph` |
| `sync.pull` | — | diff base→head, update document; replies `sync.state`, `graph` |
| `sync.push` | `message` | upload + commit; replies `sync.state`, `graph`, or `error` |
| `commit.select` | `id` | commit + enriched diff; replies `diff`; starts the overlay |
| `object.select` | `id` | selects and zooms to the object in Rhino |
| `commit.checkout` | `id` | loads the snapshot into a new document |
| `branch.switch` | `name` | moves the working copy to that branch |
| `branch.create` | `name`, `from` | creates the ref, switches to it |
| `agent.start` | `branch`, `from`, `prompt`, `model`, `budget` | creates the ref, launches the agent, starts relaying `agent.run` |
| `agent.stop` | `branch` | stops the agent |
| `branch.merge` | `theirs`, `into` | merges; replies `graph`, `sync.state` or `error` |

| C# → Panel | Payload |
| --- | --- |
| `sync.state` | `SyncState` |
| `graph` | `GraphResponse` + checks by commit id |
| `diff` | `CommitInfo` + diff with names and subtypes |
| `localChanges` | working-copy changes for the Push dialog |
| `agent.run` | `AgentRun`, every time the agent reports progress |
| `error` | which action failed + `ApiError` |

## Gaps to raise with the engine team

1. **Counts per commit.** The graph has no change counts, so the panel needs one diff per row. A `counts` field on `GraphCommit` would save those calls.
2. **Names and subtypes in the diff.** `DiffEntry` has neither, so C# makes extra calls per object. An optional `subtype` on `DiffEntry` would help.
3. **Behind count.** The panel has to walk the graph to count commits between `base` and `head`. A `GET /compare?base=&head=` returning `ahead` and `behind` would be simpler.
4. **Live agent progress.** Nothing in the engine tracks a running agent or its token use. The proposal above reuses existing pieces (an `agent/run` annotation plus commit `meta`), so no new endpoint is needed. A push channel (SSE) would beat polling later.
5. **Commit meta in the graph.** Token chips need `meta` per commit, which `GraphCommit` does not carry. Either add `meta` to `GraphCommit` or fetch `GetCommitAsync` for visible agent commits.
