// Map between git_engine (RhinoTrunk.Contracts, prototype v0.1) and the panel UI.
// See docs/ENGINE_MAP.md for the element-by-element table.
//
// Flow: the panel never calls HTTP. It sends a PanelCommand over the bridge, C# calls
// RhinoTrunkClient, and C# posts the engine DTOs back as a HostEvent. The adapters
// below turn those DTOs into the view models the components render.

// ---------------------------------------------------------------------------------
// 1. Engine DTOs, as serialized by the server (camelCase, nulls omitted).
//    Source: git_engine/src/RhinoTrunk.Contracts/Dtos.cs
// ---------------------------------------------------------------------------------

export type Hash = `sha256:${string}`;
export type EntityKind =
  | 'settings' | 'layer' | 'object' | 'blockDefinition' | 'material' | 'linetype'
  | 'dimensionStyle' | 'hatchPattern' | 'group' | 'namedView' | 'note';

export interface AuthorInfo { id: string; name?: string; kind: 'human' | 'agent' }

/** GET /repos/{repo}/graph — newest first, every commit before its parents. */
export interface GraphCommit {
  id: Hash;
  parents: Hash[];
  generation: number;
  timestamp: string;
  message: string;
  author: AuthorInfo;
  /** e.g. "main", "agent/facade-study", "tag:review-1" */
  refs: string[];
}
export interface GraphResponse { commits: GraphCommit[]; truncated: boolean }

/** GET /repos/{repo}/commits/{rev} */
export interface CommitInfo {
  id: Hash;
  tree: Hash;
  parents: Hash[];
  generation: number;
  author: AuthorInfo;
  timestamp: string;
  message: string;
  meta?: Record<string, string>;
}

/** GET /repos/{repo}/diff?from=&to= */
export interface DiffEntry {
  doc: string;
  kind: EntityKind;
  id: string;
  change: 'added' | 'deleted' | 'modified';
  from?: Hash;
  to?: Hash;
  /** For "modified": component names whose hash changed, e.g. ["geometry", "attributes"]. */
  components?: string[];
  refsChanged?: boolean;
  subtypeChanged?: boolean;
}
export interface DiffResponse { from?: Hash; to: Hash; changes: DiffEntry[] }

/** Entity manifest, from GET /commits/{rev}/entities?manifests=true or /entities/{kind}/{id}. */
export interface Manifest {
  kind: EntityKind;
  subtype?: string;
  components: Record<string, Hash>;
  refs: { kind: EntityKind; id: string; role: string }[];
}

/** GET /repos/{repo}/commits/{rev}/annotations */
export interface Annotation {
  namespace: string;
  status: 'pending' | 'success' | 'failure' | 'info';
  summary?: string;
  data?: Record<string, unknown>;
  author?: AuthorInfo;
  timestamp?: string;
}

/** GET /repos/{repo}/refs */
export interface RefInfo { name: string; type: 'branch' | 'tag'; commit: Hash }

/** POST /repos/{repo}/commits response */
export interface CommitResponse { commit: Hash; tree: Hash; branch: string }

/** Every failure: one shape. */
export interface ApiError {
  code: string;
  message: string;
  errors?: { code: string; message: string; doc?: string; kind?: EntityKind; id?: string; fields?: string[] }[];
  data?: Record<string, string>;
}

/** Diff entry enriched by C#: the diff carries no names or subtypes, so C# reads the
 *  manifest (subtype) and its attributes blob (name) for each object it reports. */
export interface DiffEntryWithInfo extends DiffEntry {
  subtype?: string;
  name?: string;
  /** Attribute fields that differ, only when the user expanded the row. */
  fields?: { field: string; before: unknown; after: unknown }[];
}

// ---------------------------------------------------------------------------------
// 2. View models the components render.
// ---------------------------------------------------------------------------------

export type ChangeOp = 'A' | 'M' | 'D';

export interface CommitRow {
  id: Hash;
  shortId: string;
  message: string;
  author: string;
  isAgent: boolean;
  isMerge: boolean;
  time: string;
  branches: string[];
  tags: string[];
  lane: number;
  parentIds: Hash[];
  counts?: { added: number; modified: number; deleted: number };
  check?: { ok: boolean; text: string };
  /** Agent commits only, from CommitInfo.meta (tokensFromMeta). */
  tokens?: number;
}

export interface ObjectChangeRow {
  id: string;
  op: ChangeOp;
  name: string;
  subtype: string;
  components: string[];
  fields: { field: string; before: string; after: string }[];
  /** Version to draw in the viewport: `to` for added/modified, `from` for deleted. */
  drawVersion?: Hash;
  ghostVersion?: Hash;
}

export interface SyncState {
  repo: string;
  server: string;
  connected: boolean;
  branch: string;
  head?: Hash;
  base?: Hash;
  behind: number;
  localChanges: number;
  fetchedAt?: string;
}

// ---------------------------------------------------------------------------------
// 3. Adapters: DTO -> view model.
// ---------------------------------------------------------------------------------

export const shortHash = (hash: string) => hash.replace(/^sha256:/, '').slice(0, 7);

const OPS: Record<DiffEntry['change'], ChangeOp> = { added: 'A', modified: 'M', deleted: 'D' };

/**
 * Lanes in one pass over newest-first commits, as the client guide suggests.
 * Lane 0 is reserved for the first-parent history of `mainline` (main's head), so a
 * branch sitting on top of main still gets its own lane instead of looking like main.
 */
export function toCommitRows(graph: GraphResponse, now = new Date(), mainline?: Hash): CommitRow[] {
  const byId = new Map(graph.commits.map((c) => [c.id, c]));
  const main = new Set<Hash>();
  for (let id: Hash | undefined = mainline ?? graph.commits[0]?.id; id && !main.has(id); id = byId.get(id)?.parents[0]) main.add(id);

  const lanes: (Hash | null)[] = [null];
  const free = () => { const i = lanes.indexOf(null, 1); return i < 0 ? lanes.length : i; };
  return graph.commits.map((c) => {
    let lane = main.has(c.id) ? 0 : lanes.indexOf(c.id, 1);
    if (lane < 0) lane = free();
    for (let i = 1; i < lanes.length; i++) if (lanes[i] === c.id) lanes[i] = null;
    const [first, ...rest] = c.parents;
    // A side lane ends where it rejoins main; main's lane always continues.
    lanes[lane] = lane === 0 ? first ?? null : first && !main.has(first) ? first : null;
    for (const p of rest) if (!main.has(p) && !lanes.includes(p)) lanes[free()] = p;
    return {
      id: c.id,
      shortId: shortHash(c.id),
      message: c.message,
      author: c.author.name ?? c.author.id,
      isAgent: c.author.kind === 'agent',
      isMerge: c.parents.length > 1,
      time: relativeTime(c.timestamp, now),
      branches: c.refs.filter((r) => !r.startsWith('tag:')),
      tags: c.refs.filter((r) => r.startsWith('tag:')).map((r) => r.slice(4)),
      lane,
      parentIds: c.parents,
    };
  });
}

/** Objects only: the panel hides layers, settings and other tables. */
export function toObjectChanges(diff: { changes: DiffEntryWithInfo[] }): ObjectChangeRow[] {
  return diff.changes.filter((e) => e.kind === 'object').map((e) => ({
    id: e.id,
    op: OPS[e.change],
    name: e.name || `${e.subtype ?? 'Object'} ${e.id.slice(0, 8)}`,
    subtype: e.subtype ?? '',
    components: e.components ?? (e.change === 'added' ? ['geometry', 'attributes'] : []),
    fields: (e.fields ?? []).map((f) => ({ field: f.field, before: String(f.before), after: String(f.after) })),
    drawVersion: e.change === 'deleted' ? e.from : e.to,
    ghostVersion: e.change === 'modified' && e.components?.includes('geometry') ? e.from : undefined,
  }));
}

export function countChanges(rows: ObjectChangeRow[]) {
  return {
    added: rows.filter((r) => r.op === 'A').length,
    modified: rows.filter((r) => r.op === 'M').length,
    deleted: rows.filter((r) => r.op === 'D').length,
  };
}

/** First annotation that failed wins; otherwise the first success. */
export function toCheck(annotations: Annotation[]): CommitRow['check'] {
  const pick = annotations.find((a) => a.status === 'failure') ?? annotations.find((a) => a.status === 'success');
  return pick && { ok: pick.status === 'success', text: `${pick.namespace} · ${pick.summary ?? pick.status}` };
}

/** Plain-language text for engine errors shown in the UI (STYLE_GUIDE.md, "Errors"). */
export function describeError(error: ApiError): { title: string; detail: string; action?: 'pull' | 'includeDependency' } {
  const first = error.errors?.[0];
  switch (error.code) {
    case 'ref_conflict':
      return { title: 'main has new commits', detail: 'Pull them first, then push. Your changes stay in the document.', action: 'pull' };
    case 'validation_failed':
      if (first?.code === 'missing_ref') return { title: 'Something this change needs is not included', detail: first.message, action: 'includeDependency' };
      if (first?.code === 'dangling_ref') return { title: 'Still in use', detail: first.message };
      return { title: 'The server rejected this commit', detail: first?.message ?? error.message };
    case 'no_changes':
      return { title: 'Nothing to push', detail: 'The document matches the last commit.' };
    case 'object_missing':
      return { title: 'Upload incomplete', detail: 'Some geometry did not reach the server. Push again.' };
    case 'not_found':
      return { title: 'Not found on the server', detail: error.message };
    default:
      return { title: 'Sync failed', detail: error.message };
  }
}

/** Format: "2 behind · 3 local changes · fetched 2 min ago" */
export function syncLine(s: SyncState, now = new Date()): string {
  return [
    s.behind ? `${s.behind} behind` : 'Up to date',
    s.localChanges ? `${s.localChanges} local change${s.localChanges === 1 ? '' : 's'}` : 'no local changes',
    s.fetchedAt ? `fetched ${relativeTime(s.fetchedAt, now)}` : 'not fetched yet',
  ].join(' · ');
}

export function relativeTime(iso: string, now = new Date()): string {
  const s = Math.round((now.getTime() - new Date(iso).getTime()) / 1000);
  if (s < 45) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return new Date(iso).toLocaleDateString();
}

// ---------------------------------------------------------------------------------
// 4. Branches and agent runs.
//    The engine has no token tracking yet. Proposal (see docs/ENGINE_MAP.md):
//    - each agent commit carries its token usage in CommitInfo.meta (AGENT_META keys);
//    - a running agent posts an annotation in namespace "agent/run" on its branch head,
//      status "pending" while running, with AgentRunData in `data`.
// ---------------------------------------------------------------------------------

export const AGENT_META = {
  model: 'agent.model',
  session: 'agent.session',
  prompt: 'agent.prompt',
  inputTokens: 'agent.tokens.input',
  outputTokens: 'agent.tokens.output',
} as const;

export const AGENT_RUN_NAMESPACE = 'agent/run';

export type BranchKind = 'human' | 'agent';
export const branchKind = (name: string): BranchKind => (name.startsWith('agent/') ? 'agent' : 'human');

export type AgentRunStatus = 'running' | 'done' | 'stopped' | 'budget' | 'failed' | 'merged';

/** `data` of an "agent/run" annotation. */
export interface AgentRunData {
  prompt: string;
  model: string;
  step: string;
  inputTokens: number;
  outputTokens: number;
  budget: number;
  startedAt: string;
  finishedAt?: string;
  commits: number;
  status: AgentRunStatus;
}

export interface AgentRun extends AgentRunData {
  branch: string;
  total: number;
  /** 0..1 of the token budget. */
  used: number;
}

export function toAgentRun(branch: string, annotation: Annotation): AgentRun | undefined {
  if (annotation.namespace !== AGENT_RUN_NAMESPACE || !annotation.data) return undefined;
  const d = annotation.data as unknown as AgentRunData;
  const total = d.inputTokens + d.outputTokens;
  return { ...d, branch, total, used: d.budget ? Math.min(1, total / d.budget) : 0 };
}

export function tokensFromMeta(meta?: Record<string, string>) {
  const input = Number(meta?.[AGENT_META.inputTokens] ?? NaN);
  const output = Number(meta?.[AGENT_META.outputTokens] ?? NaN);
  if (Number.isNaN(input) || Number.isNaN(output)) return undefined;
  return { input, output, total: input + output };
}

/** 950 → "950", 48_312 → "48.3k", 1_204_000 → "1.2M" */
export function formatTokens(n: number): string {
  if (n < 1000) return String(Math.round(n));
  if (n < 1_000_000) return `${(n / 1000).toFixed(n < 100_000 ? 1 : 0)}k`;
  return `${(n / 1_000_000).toFixed(1)}M`;
}

// ---------------------------------------------------------------------------------
// 5. Planned bridge messages (add to protocol/events.ts when wiring each feature).
// ---------------------------------------------------------------------------------

export type PlannedPanelCommand =
  | { type: 'sync.fetch' }
  | { type: 'sync.pull' }
  | { type: 'sync.push'; message: string }
  | { type: 'branch.switch'; name: string }
  | { type: 'branch.create'; name: string; from: Hash }
  | { type: 'agent.start'; branch: string; from: Hash; prompt: string; model: string; budget: number }
  | { type: 'agent.stop'; branch: string }
  | { type: 'branch.merge'; theirs: string; into: string }
  | { type: 'commit.select'; id: Hash }
  | { type: 'object.select'; id: string }
  | { type: 'commit.checkout'; id: Hash };

export type PlannedHostEvent =
  | { type: 'sync.state'; state: SyncState }
  | { type: 'graph'; graph: GraphResponse; checks: Record<string, Annotation[]>; meta: Record<string, Record<string, string>> }
  | { type: 'diff'; commit: CommitInfo; diff: { from?: Hash; to: Hash; changes: DiffEntryWithInfo[] } }
  | { type: 'localChanges'; changes: DiffEntryWithInfo[] }
  | { type: 'agent.run'; run: AgentRun }
  | { type: 'error'; during: 'fetch' | 'pull' | 'push' | 'checkout' | 'branch' | 'agent' | 'merge'; error: ApiError };
