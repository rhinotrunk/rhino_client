import { useEffect, useRef, useState } from 'react';
import { ArrowUp, Check, ChevronDown, ChevronRight, Circle, GitBranch, GitMerge, Layers, ListTree, Loader2, MessageSquare, Plus, Settings2, Square, MoreHorizontal, Play, Pause, Archive, ArchiveRestore, Trash2, X } from 'lucide-react';
import type { Bridge } from './protocol/bridge';
import type { Activity, AgentTask, ControlState, Scope, Settings, TaskStatus, ModelingSession } from './protocol/events';
import { budgetModel, newId } from './protocol/events';

const labels: Record<TaskStatus, string> = { draft: 'Not launched', queued: 'Queued for worker', running: 'Running', stopping: 'Stopping', stopped: 'Stopped', ready: 'PR ready', failed: 'Failed', interrupted: 'Interrupted' };
const emptyScope = (): Scope => ({ objectIds: [], layerIds: [] });
function Status({ status, label }: { status: TaskStatus; label?: string }) {
  return <span className={`status status-${status}`}>{status === 'running' || status === 'stopping' ? <Loader2 className="spin" size={12} /> : status === 'ready' ? <Check size={12} /> : <Circle size={8} fill="currentColor" />} {label ?? labels[status]}</span>;
}
function ScopePicker({ scope, setScope, state, capture }: { scope: Scope; setScope: (s: Scope) => void; state: ControlState; capture: () => void }) {
  return <div className="scope-picker">
    <p className="field-label">Optional geometry scope</p>
    {state.layers.length === 0 && <p className="muted">Create a layer in Rhino, then refresh the panel.</p>}
    <div className="layer-options">{state.layers.map(layer => <label key={layer.id} className="layer-option">
      <input type="checkbox" checked={scope.layerIds.includes(layer.id)} onChange={e => setScope({ ...scope, wholeDocument: false, layerIds: e.target.checked ? [...scope.layerIds, layer.id] : scope.layerIds.filter(x => x !== layer.id) })} />
      <Layers size={13} /><span>{layer.name}</span>
    </label>)}</div>
    <button className="quiet" type="button" onClick={capture}>Use Rhino selection{scope.objectIds.length ? ` (${scope.objectIds.length} objects)` : ''}</button>
    {scope.objectIds.length > 0 && <button className="quiet" type="button" onClick={() => setScope({ ...scope, wholeDocument: false, objectIds: [] })}>Clear objects</button>}
    <p className="hint">Leave empty to work on the whole task copy. Select objects or layers to restrict edits.</p>
  </div>;
}
function ActivityList({ entries, activity }: { entries: Activity[]; activity: boolean }) {
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => { end.current?.scrollIntoView({ block: 'nearest' }); }, [entries.length]);
  const shown = activity ? entries : entries.filter(e => ['user', 'assistant', 'error', 'status'].includes(e.kind));
  return <div className="transcript" aria-live="polite" aria-relevant="additions">
    {shown.map((e, i) => <div key={i} className={`entry entry-${e.kind}`}>
      <div className="entry-meta"><span>{e.kind === 'user' ? 'You' : e.kind === 'assistant' ? 'Agent' : e.kind === 'tool' ? 'Tool call' : e.kind === 'result' ? 'Tool result' : e.kind === 'error' ? 'Needs attention' : 'Activity'}</span><time>{new Date(e.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</time></div>
      {e.kind === 'tool' || e.kind === 'result' ? <details><summary>{e.text.split('\n')[0]?.slice(0, 110)}</summary><pre>{e.text}</pre></details> : <p>{e.text}</p>}
    </div>)}<div ref={end} />
  </div>;
}
export function App({ bridge }: { bridge: Bridge }) {
  const [state, setState] = useState<ControlState>();
  const [selected, setSelected] = useState('sessions');
  const [tab, setTab] = useState<'conversation' | 'activity' | 'scope'>('conversation');
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [collapsedPlans, setCollapsedPlans] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState('');
  const [showArchived, setShowArchived] = useState(false);
  const [error, setError] = useState('');
  const [scope, setScope] = useState<Scope>(emptyScope);
  const [description, setDescription] = useState('');
  const [reply, setReply] = useState('');
  const [showSettings, setShowSettings] = useState(false);
  const [settings, setSettings] = useState<Settings>({ codexPath: 'codex', model: budgetModel, concurrency: 2 });
  const [taskSessionId, setTaskSessionId] = useState('');
  const [parentId, setParentId] = useState('');
  const [branchMode, setBranchMode] = useState(false);
  const [title, setTitle] = useState('');
  const [instructions, setInstructions] = useState('');
  const [draftScope, setDraftScope] = useState<Scope>(emptyScope);
  const selectionTarget = useRef<'scope' | 'draft'>('scope');
  useEffect(() => {
    const unsubscribe = bridge.subscribe(event => {
      if (event.type === 'theme') {
        document.documentElement.dataset.scheme = event.scheme;
        document.documentElement.classList.toggle('dark', event.scheme === 'dark');
      } else if (event.type === 'control.state') setState(event);
      else if (event.type === 'control.error') setError(event.message);
      else if (event.type === 'scope.selection') {
        const change = (s: Scope) => ({ ...s, wholeDocument: false, objectIds: event.objectIds });
        if (selectionTarget.current === 'draft') setDraftScope(change); else setScope(change);
      }
    });
    bridge.send({ type: 'ready' }); return unsubscribe;
  }, [bridge]);
  const task = state?.tasks.find(t => t.id === selected);
  const session = state?.sessions.find(s => s.id === (task?.sessionId ?? (selected.startsWith('session:') ? selected.slice(8) : selected === 'new-task' ? taskSessionId : '')));
  useEffect(() => {
    if (task) { setTitle(task.title); setInstructions(task.instructions); setDraftScope(structuredClone(task.scope)); }
    setReply('');
  }, [selected, task?.status]);
  const send = (command: Parameters<Bridge['send']>[0]) => { setError(''); bridge.send(command); };
  const select = (id: string) => { setSelected(id); setTab('conversation'); if (state?.tasks.some(t => t.id === id)) send({ type: 'task.show', id }); else send({ type: 'preview.clear' }); };
  const capture = (target: 'scope' | 'draft') => { selectionTarget.current = target; send({ type: 'scope.selection' }); };
  const newSession = () => { setDescription(''); setScope(emptyScope()); select('new-session'); };
  const addTask = (from?: AgentTask, branch = false) => {
    setBranchMode(branch);
    const owner = from ? state?.sessions.find(s => s.id === from.sessionId) : session;
    if (!owner) return;
    setTaskSessionId(owner.id); setParentId(from?.id ?? ''); setTitle(''); setInstructions('');
    setScope(structuredClone(from?.scope ?? owner.scope)); select('new-task');
  };
  if (!state) return <div className="connection-screen"><ListTree size={30} /><h1>RhinoClient</h1><p>{bridge.kind === 'unavailable' ? 'Open this panel through RhinoClient in Rhino.' : 'Connecting to your modeling workspace…'}</p></div>;
  const visibleTasks = state.tasks.filter(t => !t.deleted && (showArchived || !t.archived));
  const visibleSessions = state.sessions.filter(s => showArchived || !s.archived);
  const sessionTasks = session ? visibleTasks.filter(t => t.sessionId === session.id) : [];
  const drafts = sessionTasks.filter(t => t.status === 'draft' && !t.paused && !t.archived).length;
  const busy = task && ['running', 'queued', 'stopping'].includes(task.status);
  const sessionBusy = session && (session.isPlanning || sessionTasks.some(t => ['running', 'queued', 'stopping'].includes(t.status)));
  const upstream = state.tasks.find(t => t.id === task?.parentId);
  const toggle = (id: string) => setCollapsed(prev => { const next = new Set(prev); next.has(id) ? next.delete(id) : next.add(id); return next; });
  const matchTask = (t: AgentTask): boolean => t.title.toLowerCase().includes(search.toLowerCase()) || visibleTasks.filter(c => c.parentId === t.id).some(matchTask);
  const waitingLabel = (node: AgentTask): string => {
    if (node.archived) return 'Archived';
    if (node.paused) return node.status === 'stopping' ? 'Pausing…' : 'Paused';
    if (node.status !== 'queued' || node.inputPath) return labels[node.status];
    const parent = state.tasks.find(t => t.id === node.parentId);
    if (!parent || parent.status === 'ready') return labels.queued;
    if (parent.paused || parent.archived) return 'Waiting for task';
    if (['failed', 'stopped', 'interrupted'].includes(parent.status)) return 'Blocked by task';
    return 'Waiting for task';
  };
  const taskControls = (node: AgentTask) => <div className="row-actions">
    {node.archived ? <button className="icon-button" aria-label={`Restore ${node.title}`} title="Restore task and children" onClick={() => send({ type: 'task.restore', id: node.id })}><Archive size={13} /></button> : <>
    {node.paused || ['stopped', 'interrupted', 'failed'].includes(node.status) ? <button className="icon-button" aria-label={`Resume ${node.title}`} title="Resume task" onClick={() => send({ type: 'task.resume', id: node.id })}><Play size={13} /></button> : node.status === 'draft' ? <button className="icon-button" aria-label={`Run ${node.title}`} title="Run task" onClick={() => send({ type: 'task.launch', id: node.id })}><Play size={13} /></button> : null}
    {node.status === 'ready' && (node.mergedBranch
      ? <span className="merged-chip" title={`Merged via ${node.mergedBranch}`}><Check size={12} aria-hidden="true" />Merged</span>
      : <button className="merge-button" disabled={node.merging} aria-label={`Merge ${node.title}`} title={node.merging ? 'Merging…' : "Merge into the branch this model is on (main by default): saves the result on its own agent branch, merges it, then pulls it into the model"} onClick={() => send({ type: 'task.merge', id: node.id })}>{node.merging ? <Loader2 size={14} className="spin" aria-hidden="true" /> : <GitMerge size={14} aria-hidden="true" />}</button>)}
    <details className="row-menu" onClick={event => { if ((event.target as HTMLElement).closest('button')) event.currentTarget.open = false; }}><summary aria-label={`Actions for ${node.title}`} title="Task actions"><MoreHorizontal size={15} /></summary><div>
      <button onClick={() => addTask(node)}><Plus size={14} aria-hidden="true" />Add task</button>
      <button disabled={node.status !== 'ready'} onClick={() => addTask(node, true)}><GitBranch size={14} aria-hidden="true" />Branch out</button>
      <button disabled={node.paused || node.status === 'ready'} onClick={() => send({ type: 'task.pause', id: node.id })}><Pause size={14} aria-hidden="true" />Pause</button>
      <button title="Archive this task and its children" onClick={() => send({ type: 'task.archive', id: node.id })}><Archive size={14} aria-hidden="true" />Archive</button>
      <button title="Remove this task and its children from the plan" onClick={() => { send({ type: 'task.remove', id: node.id }); select(`session:${node.sessionId}`); }}><Trash2 size={14} aria-hidden="true" />Remove</button>
    </div></details></>}
  </div>;
  const tree = (node: AgentTask, depth = 0): React.ReactNode => {
    if (search && !matchTask(node)) return null;
    const children = visibleTasks.filter(t => t.parentId === node.id);
    const expanded = !collapsed.has(node.id) || !!search;
    return <li key={node.id}><div className={`tree-row ${selected === node.id ? 'selected' : ''}`} style={{ paddingLeft: 12 + depth * 15 }}>
      {children.length ? <button className="expand" aria-label={`${expanded ? 'Collapse' : 'Expand'} ${node.title}`} aria-expanded={expanded} onClick={() => toggle(node.id)}>{expanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}</button> : <span className="tree-leaf" />}
      <button className="tree-select" onClick={() => select(node.id)} aria-current={selected === node.id ? 'true' : undefined}><span className="tree-title">{node.title}</span><Status status={node.status} label={waitingLabel(node)} /></button>
      {taskControls(node)}
    </div>{expanded && !!children.length && <ul>{children.map(t => tree(t, depth + 1))}</ul>}</li>;
  };
  const sessionLabel = (s: ModelingSession) => s.isPlanning ? 'Preparing tasks' : state.tasks.some(t => t.sessionId === s.id && t.status === 'running') ? 'Running' : `${visibleTasks.filter(t => t.sessionId === s.id).length} tasks`;
  const planCard = session && !task && sessionTasks.length > 0 ? <section className="session-plan" aria-label="Session tasks">
    <div className="session-plan-heading"><h3><button className="session-plan-toggle" aria-expanded={!collapsedPlans.has(session.id)} aria-controls={`session-plan-${session.id}`} onClick={() => setCollapsedPlans(prev => { const next = new Set(prev); next.has(session.id) ? next.delete(session.id) : next.add(session.id); return next; })}>
      {collapsedPlans.has(session.id) ? <ChevronRight size={14} /> : <ChevronDown size={14} />}<span>Tasks</span><span className="muted">({sessionTasks.length})</span>
    </button></h3><button className="quiet" onClick={() => addTask()}><Plus size={13} />Add task</button></div>
    <div id={`session-plan-${session.id}`} className="session-plan-body" hidden={collapsedPlans.has(session.id)}>
      <div className="session-plan-tasks">{sessionTasks.map(t => <div key={t.id} className="session-plan-row"><button className="plan-task-title" onClick={() => select(t.id)}><span>{t.title}</span></button><Status status={t.status} label={waitingLabel(t)} />{taskControls(t)}</div>)}</div>
    </div>
  </section> : null;
  return <div className="control-plane">
    <header className="workspace-header"><div className="workspace-brand"><ListTree size={20} /><div><h1>RhinoClient</h1><span className="document-name">{state.document || 'Untitled model'}</span></div></div><div className="header-actions"><span className={`connection ${bridge.kind === 'preview' ? 'preview' : ''}`}>{bridge.kind === 'preview' ? 'Simulated preview' : 'Rhino connected'}</span><button className="icon-button" aria-label="Worker settings" onClick={() => { setSettings(state.settings); setShowSettings(true); }}><Settings2 size={17} /></button></div></header>
    {bridge.kind === 'preview' && <div className="preview-banner">Browser preview simulates agents and geometry. Real workers run inside Rhino.</div>}
    {error && <div className="error-banner" role="alert"><span>{error}</span><button className="icon-button" aria-label="Dismiss error" onClick={() => setError('')}><X size={15} /></button></div>}
    <div className="workspace-body">
      <aside className="task-sidebar" aria-label="Sessions"><div className="sidebar-heading"><h2>Sessions</h2><button className="quiet" onClick={newSession}><Plus size={15} />New session</button></div>
        <button className={`planner-link ${selected === 'sessions' ? 'selected' : ''}`} onClick={() => select('sessions')}><ListTree size={15} />All sessions</button>
        <button className="quiet archive-toggle" onClick={() => setShowArchived(!showArchived)}><Archive size={13} />{showArchived ? 'Hide archived' : 'Show archived'}</button>
        {!!state.sessions.length && <input className="task-search" aria-label="Search sessions and tasks" placeholder="Find a session or task…" value={search} onChange={e => setSearch(e.target.value)} />}
        <div className="tree-scroll">{visibleSessions.filter(s => !search || s.title.toLowerCase().includes(search.toLowerCase()) || state.tasks.some(t => t.sessionId === s.id && matchTask(t))).map(s => <div key={s.id} className="session-tree-group"><div className={`session-nav-line ${session?.id === s.id && !task ? 'selected' : ''}`}>
          <button className="session-nav" onClick={() => select(`session:${s.id}`)}><MessageSquare size={14} /><span><strong>{s.title}</strong><small>{sessionLabel(s)}</small></span>{s.isPlanning && <Loader2 size={12} className="spin" />}</button><details className="row-menu" onClick={event => { if ((event.target as HTMLElement).closest('button')) event.currentTarget.open = false; }}><summary aria-label={`Session actions for ${s.title}`}><MoreHorizontal size={15} /></summary><div><button onClick={() => select(`session:${s.id}`)}><MessageSquare size={14} aria-hidden="true" />Chat</button><button onClick={() => { send({ type: s.archived ? 'session.restore' : 'session.archive', id: s.id }); select('sessions'); }}>{s.archived ? <ArchiveRestore size={14} aria-hidden="true" /> : <Archive size={14} aria-hidden="true" />}{s.archived ? 'Restore' : 'Archive'}</button></div></details></div>
          <ul className="task-tree">{visibleTasks.filter(t => t.sessionId === s.id && !t.parentId).map(t => tree(t))}</ul>
        </div>)}{!state.sessions.length && <p className="tree-empty">Start a session to describe your modeling work.</p>}</div>
        {session && <div className="sidebar-footer"><button className="primary" disabled={!drafts} onClick={() => send({ type: 'tasks.launch', sessionId: session.id })}>Run reviewed tasks <span className="count">{drafts}</span></button><p>Children wait for their upstream result.</p></div>}
      </aside>
      <section className="task-inspector" aria-label={task?.title ?? session?.title ?? 'Sessions'}>
        {selected === 'sessions' ? <><div className="inspector-heading"><h2>Sessions</h2><button className="primary small" onClick={newSession}><Plus size={14} />New session</button></div><div className="sessions-page">
          <div className="session-metrics"><div><strong>{state.sessions.length}</strong><span>Sessions</span></div><div><strong>{state.tasks.length}</strong><span>Tasks</span></div><div><strong>{state.tasks.filter(t => t.status === 'running').length}</strong><span>Running tasks</span></div><div><strong>{(state.tasks.reduce((n, t) => n + t.inputTokens + t.outputTokens, 0) + state.sessions.reduce((n, s) => n + s.inputTokens + s.outputTokens, 0)).toLocaleString()}</strong><span>Tokens</span></div></div>
          <div className="sessions-table"><div className="sessions-table-heading"><span>Session</span><span>Status</span><span>Tasks</span></div>{visibleSessions.map(s => <button className="session-table-row" key={s.id} onClick={() => select(`session:${s.id}`)}><span className="session-row-title"><MessageSquare size={15} /><span>{s.title}</span></span><span className="status">{sessionLabel(s)}</span><span className="muted">{visibleTasks.filter(t => t.sessionId === s.id).length}</span></button>)}
          {!state.sessions.length && <div className="sessions-empty"><h3>Describe an exploration</h3><p>Start a session. The orchestrator turns your description into manageable tasks and dependencies for review.</p><button className="primary" onClick={newSession}>New session</button></div>}</div>
        </div></> : selected === 'new-session' ? <div className="new-task-page"><div className="inspector-heading"><h2>New session</h2></div><form className="new-task-form" onSubmit={e => {
          e.preventDefault(); const id = newId(); send({ type: 'session.create', id, prompt: description, scope }); select(`session:${id}`);
        }}><div className="new-task-intro"><MessageSquare size={27} /><h3>What would you like to work on?</h3><p>Describe the outcome and any alternatives. Your session starts with a plan, then you choose which tasks to run.</p></div>
          <div className="composer"><textarea autoFocus aria-label="Session description" required rows={5} value={description} onChange={e => setDescription(e.target.value)} placeholder="Create a structural grid, then explore three facade options…" /><div className="composer-bottom"><span>{state.settings.model} · low reasoning</span><button className="primary" disabled={!description.trim()}><ArrowUp size={15} />Start session</button></div></div>
          <details className="task-options"><summary>Context (optional)<ChevronDown size={13} /></summary><ScopePicker scope={scope} setScope={setScope} state={state} capture={() => capture('scope')} /></details>
        </form></div> : selected === 'new-task' && session ? <div className="new-task-page"><div className="inspector-heading"><h2>{branchMode ? 'Branch from saved result' : 'Add dependent task'}</h2></div><form className="new-task-form" onSubmit={e => {
          e.preventDefault(); const id = newId(); send({ type: 'task.add', id, sessionId: session.id, title: title.trim() || instructions.slice(0, 70), instructions, parentId: parentId || undefined, branchFromResult: branchMode, scope }); select(id);
        }}><label>Task name (optional)<input value={title} onChange={e => setTitle(e.target.value)} /></label><label>Instructions<textarea autoFocus required rows={5} value={instructions} onChange={e => setInstructions(e.target.value)} /></label><p className="hint">{branchMode ? `Starts from the saved result of “${state.tasks.find(t => t.id === parentId)?.title ?? 'the selected task'}”. The new task appears beneath it in the tree.` : parentId ? 'Creates a child task. Once launched, it waits for its parent to finish before running.' : 'Adds a root task to this session.'}</p><label>{branchMode ? 'Saved result' : 'Depends on'}<select value={parentId} onChange={e => setParentId(e.target.value)}><option value="" disabled={branchMode}>Main snapshot</option>{sessionTasks.filter(t => !branchMode || t.status === 'ready').map(t => <option key={t.id} value={t.id}>{t.title}</option>)}</select></label><details className="task-options"><summary>Context (optional)</summary><ScopePicker scope={scope} setScope={setScope} state={state} capture={() => capture('scope')} /></details><button className="primary" disabled={!instructions.trim()}>Add to session</button></form></div>
        : session ? <>
          <div className="inspector-heading"><div><div className="breadcrumb">{task ? <button className="session-return" onClick={() => select(`session:${session.id}`)}>← Back to session</button> : 'Session'}</div><h2>{task?.title ?? session.title}</h2></div><div className="task-actions">
            {task ? <><Status status={task.status} label={waitingLabel(task)} />{task.status === 'draft' && <button className="primary small" onClick={() => send({ type: 'task.launch', id: task.id })}>Run task</button>}{busy && task.status !== 'stopping' && <button className="stop-button" onClick={() => send({ type: 'task.stop', id: task.id })}><Square size={11} />Stop task</button>}{task.status === 'ready' && <button className="quiet" onClick={() => addTask(task, true)}><GitBranch size={14} />Branch out</button>}</> : sessionBusy ? <button className="stop-button" onClick={() => send({ type: 'session.stop', id: session.id })}><Square size={11} />Stop session</button> : null}
          </div></div>
          {session.archived && <div className="notice">Archived session <button className="quiet" onClick={() => send({ type: 'session.restore', id: session.id })}>Restore to continue</button></div>}
          <nav className="inspector-tabs" aria-label="Details">{(['conversation', 'activity', ...(task ? ['scope'] : [])] as const).map(t => <button key={t} className={tab === t ? 'active' : ''} onClick={() => setTab(t as typeof tab)}>{t === 'conversation' ? 'Chat' : t === 'activity' ? 'Records' : 'Details'}</button>)}</nav>
          <div className="execution-layout"><div className="execution-chat">{planCard}<div className="inspector-content">
            {!task && session.isPlanning && <div className="notice"><Loader2 size={13} className="spin" /> Orchestrator is preparing tasks and dependencies…</div>}
            {task?.status === 'queued' && <div className="notice">{upstream && upstream.status !== 'ready' ? waitingLabel(task) : 'Waiting for an available worker.'}</div>}
            {task?.status === 'ready' && <div className="proposal-strip"><div><Check size={16} />Ready to merge <span className="muted">/ revision {task.revision}</span></div><button className="quiet" onClick={() => send({ type: 'task.show', id: task.id })}>Show in viewport</button><p>Use Merge on the task row to bring it into the model.{upstream ? ` Includes the unaccepted result of ${upstream.title}.` : ''}</p></div>}
            {tab === 'scope' && task ? <div className="scope-details">{task.status === 'draft' ? <><label>Task name<input value={title} onChange={e => setTitle(e.target.value)} /></label><label>Instructions<textarea rows={5} value={instructions} onChange={e => setInstructions(e.target.value)} /></label><ScopePicker scope={draftScope} setScope={setDraftScope} state={state} capture={() => capture('draft')} /><button className="primary" disabled={!title.trim() || !instructions.trim()} onClick={() => send({ type: 'task.update', id: task.id, title, instructions, scope: draftScope })}>Save task</button></> : <><h3>Instructions</h3><p className="preserve">{task.instructions}</p><h3>Editable scope</h3><p>{task.scope.wholeDocument ? 'Whole task document copy' : `${task.scope.layerIds.length} layers, ${task.scope.objectIds.length} objects`}</p><ul>{task.scope.layerIds.map(id => <li key={id}>{state.layers.find(l => l.id === id)?.name ?? id}</li>)}</ul></>}<div className="task-facts"><h3>Execution</h3><dl><dt>Worker</dt><dd>Codex CLI</dd><dt>Model</dt><dd>{task.model ?? state.settings.model}</dd><dt>Reasoning</dt><dd>Low</dd><dt>Input tokens</dt><dd>{task.inputTokens.toLocaleString()}</dd><dt>Output tokens</dt><dd>{task.outputTokens.toLocaleString()}</dd></dl>{task.inputPath && <p className="file-path">Input: {task.inputPath}</p>}{task.workingPath && <button className="quiet" disabled={!!busy} onClick={() => send({ type: 'task.preview', id: task.id })}>Inspect saved copy</button>}</div></div> : <><ActivityList entries={task?.activity ?? session.conversation} activity={tab === 'activity'} />{task?.status === 'draft' && <div className="inspector-empty"><h3>Ready for review</h3><p>{task.instructions}</p><button className="quiet" onClick={() => setTab('scope')}>Review instructions and scope</button></div>}</>}
          </div>
          {tab !== 'scope' && <div className="composer-area">{task?.status === 'draft' ? <div className="draft-footer">Review this task, then run it individually or with its session.<button className="quiet" onClick={() => setTab('scope')}>Review details</button></div> : <form onSubmit={e => { e.preventDefault(); if (task) send({ type: 'task.message', id: task.id, message: reply }); else send({ type: 'session.message', id: session.id, prompt: reply }); setReply(''); }}><div className="composer"><textarea aria-label={task ? 'Task follow-up' : 'Session message'} rows={2} value={reply} onChange={e => setReply(e.target.value)} disabled={task ? !!busy || !!task.archived : session.isPlanning || !!session.archived} placeholder={task ? busy ? 'Wait for the task or stop it to send a follow-up…' : 'Refine this task…' : 'Discuss the plan or ask for more tasks…'} /><div className="composer-bottom"><span>{task ? 'Continues in this task’s copy' : 'New tasks stay in this session'} · {state.settings.model}</span><button className="send-button" aria-label="Send message" disabled={!reply.trim() || (task ? !!busy || !!task.archived : session.isPlanning || !!session.archived)}><ArrowUp size={18} /></button></div></div></form>}</div>}
          </div><aside className="execution-overview" aria-label="Overview"><h3>Overview</h3>{task ? <Status status={task.status} label={waitingLabel(task)} /> : <span className="status">{sessionLabel(session)}</span>}<dl><dt>Session</dt><dd>{session.title}</dd><dt>Model</dt><dd>{task?.model ?? state.settings.model}</dd><dt>Reasoning</dt><dd>Low</dd><dt>Tasks</dt><dd>{sessionTasks.length}</dd><dt>Builds on</dt><dd>{upstream?.title ?? 'Main snapshot'}</dd><dt>Versioning</dt><dd>Mocked</dd><dt>Document</dt><dd>Isolated task copies</dd></dl><p className="hint">Task dependencies are separate from commit history. Main stays unchanged until a future acceptance flow.</p></aside></div>
        </> : <div className="notice">Loading session…</div>}
      </section>
    </div>
    {showSettings && <div className="modal-backdrop"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="settings-title"><header><h2 id="settings-title">Worker settings</h2><button className="icon-button" aria-label="Close settings" onClick={() => setShowSettings(false)}><X size={18} /></button></header><form onSubmit={e => { e.preventDefault(); send({ type: 'settings.save', ...settings }); setShowSettings(false); }}><label>Codex executable<input required value={settings.codexPath} onChange={e => setSettings({ ...settings, codexPath: e.target.value })} /></label><p className="hint">Uses your existing Codex CLI sign-in. No API key is entered in the panel.</p><label>Model<select value={settings.model} onChange={e => setSettings({ ...settings, model: e.target.value })}><option value="gpt-6-luna">GPT-6 Luna — budget default</option><option value="gpt-6.1-terra">GPT-6.1 Terra — lower-cost 6.1</option></select></label><p className="hint">Low reasoning effort for planning and workers. No automatic upgrade to a more expensive model.</p><label>Concurrent task workers<select value={settings.concurrency} onChange={e => setSettings({ ...settings, concurrency: Number(e.target.value) })}>{[1, 2, 3, 4].map(n => <option key={n} value={n}>{n}</option>)}</select></label><footer><button className="primary">Save settings</button></footer></form></section></div>}
  </div>;
}
