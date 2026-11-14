// In-memory stand-in for git_engine + the C# plugin, for the browser preview and demos.
// It speaks the engine's DTO shapes (map.ts) so components work unchanged when the
// real bridge events arrive. Replace with a bridge-backed store when C# is wired.
import {
  AGENT_META, AGENT_RUN_NAMESPACE, branchKind, toAgentRun,
  type AgentRun, type AgentRunData, type Annotation, type BranchKind, type DiffEntryWithInfo,
  type GraphCommit, type Hash, type SyncState,
} from './map';

export interface BranchInfo { name: string; kind: BranchKind; head: Hash; run?: AgentRun }

export interface TrunkState {
  sync: SyncState;
  branches: BranchInfo[];
  commits: GraphCommit[];
  diffs: Record<string, DiffEntryWithInfo[]>;
  meta: Record<string, Record<string, string>>;
  checks: Record<string, Annotation[]>;
  localChanges: DiffEntryWithInfo[];
  /** Commits teammates pushed to main that we have not pulled yet. */
  remote: RemoteCommit[];
  /** A commit, or 'working' for the uncommitted changes row. */
  selected?: Hash | typeof WORKING;
  fresh: Hash[];
}

export const WORKING = 'working' as const;
interface RemoteCommit { message: string; author: string; changes: DiffEntryWithInfo[] }

export interface AgentRequest { name: string; prompt: string; model: string; budget: number }

type Listener = () => void;

let counter = 0x9f2c41;
const newHash = () => `sha256:${(counter += 0x1b3d7).toString(16).padStart(7, '0')}${'0'.repeat(57)}` as Hash;
const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();
const obj = (id: string, name: string, subtype: string, change: DiffEntryWithInfo['change'], components?: string[], fields?: DiffEntryWithInfo['fields']): DiffEntryWithInfo => ({
  doc: 'main', kind: 'object', id, name, subtype, change,
  components: components ?? (change === 'added' ? ['geometry', 'attributes'] : undefined), fields,
  from: change === 'added' ? undefined : newHash(), to: change === 'deleted' ? undefined : newHash(),
});
// crypto.randomUUID needs a secure context; Rhino's WebView loads the panel from a string, which isn't one.
const guid = () => {
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6]! & 0x0f) | 0x40; b[8] = (b[8]! & 0x3f) | 0x80;
  const h = [...b].map((x) => x.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
};

function seed(): TrunkState {
  const h = { a1: newHash(), b2: newHash(), c3: newHash(), f4: newHash(), d5: newHash(), e6: newHash() };
  const human = (id: string, name: string) => ({ id, name, kind: 'human' as const });
  const commits: GraphCommit[] = [
    { id: h.e6, parents: [h.d5, h.f4], generation: 5, timestamp: minutesAgo(18), message: 'Merge agent/south-facade', author: human('mhayward', 'mhayward'), refs: ['main'] },
    { id: h.d5, parents: [h.c3], generation: 4, timestamp: minutesAgo(40), message: 'Thicken slab, delete setout line', author: human('mhayward', 'mhayward'), refs: [] },
    { id: h.f4, parents: [h.c3], generation: 4, timestamp: minutesAgo(52), message: 'Panelize south facade', author: { id: 'agent:facade', name: 'Facade agent', kind: 'agent' }, refs: ['agent/south-facade'] },
    { id: h.c3, parents: [h.b2], generation: 3, timestamp: minutesAgo(75), message: 'Move south columns to grid B', author: human('jlee', 'jlee'), refs: ['tag:review-1'] },
    { id: h.b2, parents: [h.a1], generation: 2, timestamp: minutesAgo(110), message: 'Add setout line', author: human('mhayward', 'mhayward'), refs: [] },
    { id: h.a1, parents: [], generation: 1, timestamp: minutesAgo(140), message: 'Share document', author: human('mhayward', 'mhayward'), refs: [] },
  ];
  const panels = [1, 2, 3].map((n) => obj(guid(), `Panel S${n}`, 'Brep', 'added'));
  const diffs: TrunkState['diffs'] = {
    [h.e6]: panels,
    [h.d5]: [obj(guid(), 'Level 03 slab', 'Brep', 'modified', ['geometry']), obj(guid(), 'Setout line', 'NurbsCurve', 'deleted'),
      obj(guid(), 'Column C5', 'Extrusion', 'modified', ['attributes'], [{ field: 'name', before: 'C5', after: 'Column C5' }])],
    [h.f4]: panels,
    [h.c3]: ['C4', 'C5', 'C6'].map((n) => obj(guid(), `Column ${n}`, 'Extrusion', 'modified', ['geometry'])),
    [h.b2]: [obj(guid(), 'Setout line', 'NurbsCurve', 'added')],
    [h.a1]: [obj(guid(), 'Level 03 slab', 'Brep', 'added'), ...[1, 2, 3, 4, 5, 6].map((n) => obj(guid(), `Column C${n}`, 'Extrusion', 'added'))],
  };
  const meta = { [h.f4]: { [AGENT_META.model]: 'claude-opus-5-5', [AGENT_META.prompt]: 'Panelize the south facade with 1.5 m glass panels', [AGENT_META.inputTokens]: '38412', [AGENT_META.outputTokens]: '21906' } };
  const checks: TrunkState['checks'] = {
    [h.e6]: [{ namespace: 'ci/geometry-valid', status: 'success', summary: '142 breps valid' }],
    [h.f4]: [{ namespace: 'ci/geometry-valid', status: 'success', summary: '3 breps valid' }],
    [h.b2]: [{ namespace: 'ci/naming', status: 'failure', summary: 'object on non-standard layer' }],
  };
  const southRun: AgentRunData = { prompt: 'Panelize the south facade with 1.5 m glass panels', model: 'claude-opus-5-5', step: 'Merged', inputTokens: 38412, outputTokens: 21906, budget: 100_000, startedAt: minutesAgo(58), finishedAt: minutesAgo(52), commits: 1, status: 'merged' };
  return {
    sync: { repo: 'tower', server: 'localhost:5280', connected: true, branch: 'main', head: h.e6, base: h.e6, behind: 2, localChanges: 3, fetchedAt: minutesAgo(2) },
    remote: [
      { message: 'Add stair core', author: 'jlee', changes: [obj(guid(), 'Stair core', 'Brep', 'added')] },
      { message: 'Close facade gap', author: 'jlee', changes: [obj(guid(), 'Panel S4', 'Brep', 'added')] },
    ],
    branches: [
      { name: 'main', kind: 'human', head: h.e6 },
      { name: 'agent/south-facade', kind: 'agent', head: h.f4, run: toAgentRun('agent/south-facade', { namespace: AGENT_RUN_NAMESPACE, status: 'success', data: { ...southRun } }) },
    ],
    commits, diffs, meta, checks,
    localChanges: [obj(guid(), 'Column C2', 'Extrusion', 'modified', ['geometry']), obj(guid(), 'Column C1', 'Extrusion', 'modified', ['attributes'], [{ field: 'name', before: 'C1', after: 'Column C1' }]), obj(guid(), 'Level 03 slab', 'Brep', 'modified', ['geometry'])],
    selected: h.e6,
    fresh: [],
  };
}

// What an agent "does", step by step. Tokens per step are scaled by the model's verbosity.
const STEPS = [
  { label: 'Reading the model', ticks: 6, input: 2600, output: 40 },
  { label: 'Planning', ticks: 6, input: 500, output: 420 },
  { label: 'Generating geometry', ticks: 10, input: 700, output: 950, commit: 0 },
  { label: 'Checking geometry', ticks: 5, input: 900, output: 160 },
  { label: 'Generating geometry', ticks: 9, input: 650, output: 880, commit: 1 },
  { label: 'Writing commit notes', ticks: 3, input: 300, output: 260, commit: 2 },
];
const MODELS: Record<string, number> = { 'claude-opus-5-5': 1, 'claude-sonnet-5-5': 0.8, 'claude-haiku-5-5': 0.55 };
const NOUNS: [RegExp, string, string][] = [
  [/column/i, 'columns', 'Column'], [/stair/i, 'stair flights', 'Stair'], [/mullion/i, 'mullions', 'Mullion'],
  [/louver|louvre|fin/i, 'louvers', 'Louver'], [/slab|floor/i, 'slab edges', 'Slab edge'], [/beam/i, 'beams', 'Beam'],
];

export function createMockEngine() {
  let state = seed();
  const listeners = new Set<Listener>();
  const timers = new Map<string, ReturnType<typeof setInterval>>();
  const emit = (next: Partial<TrunkState>) => { state = { ...state, ...next }; listeners.forEach((l) => l()); };
  const head = (branch: string) => state.branches.find((b) => b.name === branch)?.head;
  const moveRef = (commits: GraphCommit[], ref: string, to: Hash) =>
    commits.map((c) => (c.id === to ? { ...c, refs: [...c.refs.filter((r) => r !== ref), ref] } : c.refs.includes(ref) ? { ...c, refs: c.refs.filter((r) => r !== ref) } : c));
  const setBranch = (name: string, patch: Partial<BranchInfo>) => state.branches.map((b) => (b.name === name ? { ...b, ...patch } : b));
  const generation = (id?: Hash) => state.commits.find((c) => c.id === id)?.generation ?? 0;

  function addCommit(branch: string, c: Omit<GraphCommit, 'id' | 'generation' | 'timestamp' | 'refs'>, changes: DiffEntryWithInfo[], meta?: Record<string, string>, check?: Annotation) {
    const id = newHash();
    const commit: GraphCommit = { ...c, id, generation: Math.max(0, ...c.parents.map(generation)) + 1, timestamp: new Date().toISOString(), refs: [] };
    const commits = moveRef([commit, ...state.commits], branch, id);
    return {
      id,
      patch: {
        commits, branches: setBranch(branch, { head: id }),
        diffs: { ...state.diffs, [id]: changes },
        meta: meta ? { ...state.meta, [id]: meta } : state.meta,
        checks: check ? { ...state.checks, [id]: [check] } : state.checks,
      } satisfies Partial<TrunkState>,
    };
  }

  const engine = {
    subscribe(l: Listener) { listeners.add(l); return () => { listeners.delete(l); }; },
    getState: () => state,

    select(id: Hash | typeof WORKING) { emit({ selected: id }); },
    isMock: true,

    fetch() { emit({ sync: { ...state.sync, fetchedAt: new Date().toISOString() } }); return state.sync.behind; },

    pull() {
      if (state.sync.branch !== 'main' || !state.remote.length) return { count: 0, names: [] as string[] };
      const queued = state.remote;
      const ids: Hash[] = [];
      for (const r of queued) {
        const c = addCommit('main', { parents: [head('main')!], message: r.message, author: { id: r.author, name: r.author, kind: 'human' } }, r.changes);
        state = { ...state, ...c.patch };
        ids.push(c.id);
      }
      const last = ids[ids.length - 1]!;
      emit({ remote: [], sync: { ...state.sync, head: last, base: last, behind: 0, fetchedAt: new Date().toISOString() }, selected: last, fresh: ids });
      return { count: ids.length, names: queued.flatMap((r) => r.changes.map((x) => x.name ?? x.id)) };
    },

    push(message: string) {
      const branch = state.sync.branch;
      if (state.sync.behind && branch === 'main') return { ok: false as const, code: 'ref_conflict' as const };
      if (!state.localChanges.length) return { ok: false as const, code: 'no_changes' as const };
      const c = addCommit(branch, { parents: [head(branch)!], message: message.trim() || 'Update', author: { id: 'mhayward', name: 'mhayward', kind: 'human' } }, state.localChanges);
      emit({ ...c.patch, localChanges: [], selected: c.id, fresh: [c.id], sync: { ...state.sync, head: c.id, base: c.id, localChanges: 0 } });
      return { ok: true as const, id: c.id, count: state.diffs[c.id]?.length ?? 0 };
    },

    /** Mock only: pretend the user edited objects in Rhino. */
    simulateEdit() {
      const pool = [
        obj(guid(), 'Column C3', 'Extrusion', 'modified', ['geometry']),
        obj(guid(), 'Level 03 slab', 'Brep', 'modified', ['geometry']),
        obj(guid(), 'Core wall W1', 'Brep', 'added'),
        obj(guid(), 'Beam B12', 'Extrusion', 'modified', ['attributes'], [{ field: 'name', before: 'B12', after: 'Beam B12' }]),
        obj(guid(), 'Opening O4', 'Brep', 'deleted'),
      ];
      const pick = pool[Math.floor(Math.random() * pool.length)]!;
      const localChanges = [...state.localChanges.filter((c) => c.name !== pick.name), pick];
      emit({ localChanges, sync: { ...state.sync, localChanges: localChanges.length }, selected: WORKING });
      return pick.name ?? pick.id;
    },

    /** Mock only: pretend a teammate pushed to main. */
    simulateTeammatePush() {
      const ideas: RemoteCommit[] = [
        { message: 'Adjust level 04 height', author: 'jlee', changes: [obj(guid(), 'Level 04 slab', 'Brep', 'modified', ['geometry'])] },
        { message: 'Add roof parapet', author: 'aprice', changes: [obj(guid(), 'Parapet P1', 'Extrusion', 'added'), obj(guid(), 'Parapet P2', 'Extrusion', 'added')] },
        { message: 'Rename grid lines', author: 'jlee', changes: [obj(guid(), 'Grid A', 'LineCurve', 'modified', ['attributes'], [{ field: 'name', before: 'A', after: 'Grid A' }])] },
        { message: 'Remove temp massing', author: 'aprice', changes: [obj(guid(), 'Massing block', 'Brep', 'deleted')] },
      ];
      const next = ideas[state.remote.length % ideas.length]!;
      const remote = [...state.remote, next];
      emit({ remote, sync: { ...state.sync, behind: state.sync.branch === 'main' ? remote.length : 0 } });
      return next.message;
    },

    switchBranch(name: string) {
      const h = head(name); if (!h) return;
      emit({ sync: { ...state.sync, branch: name, head: h, base: h, behind: name === 'main' ? state.remote.length : 0 }, selected: WORKING });
    },

    createBranch(name: string) {
      if (head(name)) return false;
      const from = head(state.sync.branch)!;
      emit({ branches: [...state.branches, { name, kind: 'human', head: from }], commits: state.commits.map((c) => (c.id === from ? { ...c, refs: [...c.refs, name] } : c)) });
      engine.switchBranch(name);
      return true;
    },

    startAgent(req: AgentRequest) {
      const name = req.name.startsWith('agent/') ? req.name : `agent/${req.name}`;
      if (head(name)) return false;
      const from = head(state.sync.branch)!;
      const [, plural, single] = NOUNS.find(([re]) => re.test(req.prompt)) ?? [null, 'facade panels', 'Panel'];
      const run: AgentRunData = { prompt: req.prompt, model: req.model, step: STEPS[0]!.label, inputTokens: 0, outputTokens: 0, budget: req.budget, startedAt: new Date().toISOString(), commits: 0, status: 'running' };
      const annotate = (d: AgentRunData) => toAgentRun(name, { namespace: AGENT_RUN_NAMESPACE, status: d.status === 'running' ? 'pending' : 'success', data: { ...d } });
      emit({
        branches: [...state.branches, { name, kind: 'agent', head: from, run: annotate(run) }],
        commits: state.commits.map((c) => (c.id === from ? { ...c, refs: [...c.refs, name] } : c)),
      });

      const scale = MODELS[req.model] ?? 1;
      let step = 0, tick = 0, made = 0, sinceIn = 0, sinceOut = 0;
      const timer = setInterval(() => {
        const branch = state.branches.find((b) => b.name === name);
        const cur = branch?.run; const s = STEPS[step];
        if (!branch || !cur || cur.status !== 'running' || !s) return finish('done');
        const jitter = 0.7 + Math.random() * 0.6;
        const dIn = Math.round(s.input * scale * jitter), dOut = Math.round(s.output * scale * jitter);
        sinceIn += dIn; sinceOut += dOut;
        const next: AgentRunData = { ...cur, step: s.label, inputTokens: cur.inputTokens + dIn, outputTokens: cur.outputTokens + dOut };
        let patch: Partial<TrunkState> = { branches: setBranch(name, { run: annotate(next) }) };
        state = { ...state, ...patch };
        if (next.inputTokens + next.outputTokens >= req.budget) return finish('budget');
        if (++tick >= s.ticks) {
          tick = 0;
          if (s.commit !== undefined) {
            const n = 3 + Math.floor(Math.random() * 3);
            const message = s.commit === 0 ? `Generate ${n} ${plural}` : s.commit === 1 ? `Refine ${plural} spacing` : `Fix 1 invalid ${single.toLowerCase()}`;
            const changes = s.commit === 1
              ? Array.from({ length: n }, (_, i) => obj(guid(), `${single} N${i + 1}`, 'Brep', 'modified', ['geometry']))
              : s.commit === 2 ? [obj(guid(), `${single} N2`, 'Brep', 'modified', ['geometry'])]
              : Array.from({ length: n }, (_, i) => obj(guid(), `${single} N${i + 1}`, 'Brep', 'added'));
            const c = addCommit(name, { parents: [head(name)!], message, author: { id: `agent:${name.slice(6)}`, name: `${single} agent`, kind: 'agent' } }, changes,
              { [AGENT_META.model]: req.model, [AGENT_META.prompt]: req.prompt, [AGENT_META.inputTokens]: String(sinceIn), [AGENT_META.outputTokens]: String(sinceOut) },
              { namespace: 'ci/geometry-valid', status: 'success', summary: `${changes.length} breps valid` });
            sinceIn = 0; sinceOut = 0; made++;
            state = { ...state, ...c.patch };
            patch = { branches: setBranch(name, { run: annotate({ ...next, commits: made }) }), fresh: [c.id] };
          }
          step++;
        }
        emit(patch);
      }, 350);
      timers.set(name, timer);

      function finish(status: AgentRunData['status']) {
        clearInterval(timer); timers.delete(name);
        const cur = state.branches.find((b) => b.name === name)?.run;
        if (!cur) return;
        emit({ branches: setBranch(name, { run: annotate({ ...cur, status, step: status === 'done' ? 'Finished' : status === 'budget' ? 'Token budget reached' : 'Stopped', finishedAt: new Date().toISOString() }) }) });
      }
      return name;
    },

    stopAgent(name: string) {
      const t = timers.get(name); if (t) { clearInterval(t); timers.delete(name); }
      const cur = state.branches.find((b) => b.name === name)?.run;
      if (cur?.status === 'running') emit({ branches: setBranch(name, { run: { ...cur, status: 'stopped', step: 'Stopped', finishedAt: new Date().toISOString() } }) });
    },

    merge(theirs: string, into = 'main') {
      const t = state.branches.find((b) => b.name === theirs); const ours = head(into);
      if (!t || !ours) return undefined;
      // Their commits not yet in ours, along the first-parent chain.
      const theirCommits: Hash[] = [];
      for (let id: Hash | undefined = t.head; id && !isAncestor(id, ours); id = state.commits.find((x) => x.id === id)?.parents[0]) theirCommits.push(id);
      // Net change per object across their commits: added-then-modified stays "added".
      const net = new Map<string, DiffEntryWithInfo>();
      for (const e of [...theirCommits].reverse().flatMap((id) => state.diffs[id] ?? [])) {
        const prev = net.get(e.name ?? e.id);
        net.set(e.name ?? e.id, prev?.change === 'added' && e.change === 'modified' ? { ...e, change: 'added', components: ['geometry', 'attributes'] } : e);
      }
      const changes = [...net.values()];
      const c = addCommit(into, { parents: [ours, t.head], message: `Merge ${theirs}`, author: { id: 'mhayward', name: 'mhayward', kind: 'human' } }, changes);
      state = { ...state, ...c.patch };
      emit({
        branches: setBranch(theirs, t.run ? { run: { ...t.run, status: 'merged', step: 'Merged' } } : {}),
        selected: c.id, fresh: [c.id],
        sync: state.sync.branch === into ? { ...state.sync, head: c.id, base: c.id } : state.sync,
      });
      return c.id;
    },
  };

  function isAncestor(id: Hash, of: Hash): boolean {
    const seen = new Set<Hash>(); const stack: Hash[] = [of];
    while (stack.length) { const x = stack.pop()!; if (x === id) return true; if (seen.has(x)) continue; seen.add(x); stack.push(...(state.commits.find((c) => c.id === x)?.parents ?? [])); }
    return false;
  }

  return engine;
}

export type MockEngine = ReturnType<typeof createMockEngine>;
export { branchKind };
