import type { Activity, AgentTask, ControlState, HostEvent, ModelingSession, PanelCommand, Scope } from './events';
import { budgetModel, newId } from './events';
const entry = (kind: Activity['kind'], text: string): Activity => ({ kind, text, at: new Date().toISOString() });
export function createPreview(emit: (e: HostEvent) => void) {
  const state: ControlState = { tasks: [], sessions: [], document: 'Facade study.3dm',
    layers: [{ id: 'grid', name: 'Structure / Grid' }, { id: 'facade', name: 'Envelope / Facade' }],
    settings: { codexPath: 'codex', model: budgetModel, concurrency: 2 }, storagePath: 'Browser preview only' };
  try {
    const saved = localStorage.getItem('rhino-client-preview');
    if (saved) {
      const prior = JSON.parse(saved) as ControlState;
      state.tasks = prior.tasks ?? [];
      state.sessions = prior.sessions ?? [];
      state.settings = { ...prior.settings, model: ['gpt-6-luna', 'gpt-6.1-terra'].includes(prior.settings?.model) ? prior.settings.model : budgetModel };
      if (!state.sessions.length && state.tasks.length) {
        const imported = makeSession('Previous modeling session', newId(), { objectIds: [], layerIds: [] });
        state.tasks.forEach(t => { t.sessionId = imported.id; });
        state.sessions.push(imported);
      }
      for (const session of state.sessions) session.isPlanning = false;
      for (const t of state.tasks) if (['running', 'queued', 'stopping'].includes(t.status)) t.status = 'interrupted';
    }
  } catch { /* Preview storage may be unavailable. */ }
  const publish = () => {
    try { localStorage.setItem('rhino-client-preview', JSON.stringify(state)); } catch { /* optional */ }
    emit({ type: 'control.state', ...structuredClone(state) });
  };
  const add = (sessionId: string, title: string, instructions: string, scope: Scope, parentId?: string) => {
    const task: AgentTask = { id: newId(), sessionId, title, instructions, scope: structuredClone(scope), parentId,
      status: 'draft', activity: [], revision: 0, inputTokens: 0, outputTokens: 0 };
    state.tasks.push(task); return task;
  };
  const pump = () => {
    let available = state.settings.concurrency - state.tasks.filter(t => t.status === 'running').length;
    for (const task of state.tasks) {
      if (available <= 0) break;
      if (task.status !== 'queued' || task.paused || task.archived || task.deleted || state.sessions.find(s => s.id === task.sessionId)?.archived) continue;
      const parent = state.tasks.find(t => t.id === task.parentId);
      if (task.parentId && !task.inputPath && (parent?.status !== 'ready' || parent.paused || parent.archived || parent.deleted)) continue;
      available--; task.status = 'running'; task.model = state.settings.model; task.startedAt = new Date().toISOString();
      task.activity.push(entry('status', 'Simulating an external worker. Browser preview makes no model requests or geometry changes.'));
      setTimeout(() => {
        if (task.status !== 'running') return;
        task.activity.push(entry('tool', 'get_context'), entry('result', 'Simulated document context.'),
          entry('assistant', `Preview result for “${task.title}”. In Rhino, the worker edits an isolated document copy.`));
        task.status = 'ready'; task.revision++; task.finishedAt = new Date().toISOString();
        task.resultPath = 'Simulated document copy'; publish(); pump();
      }, 2400);
    }
    publish();
  };
  const planningTurns = new Map<string, number>();
  const plan = (session: ModelingSession, prompt: string) => {
    const turn = (planningTurns.get(session.id) ?? 0) + 1; planningTurns.set(session.id, turn);
    session.isPlanning = true; session.conversation.push(entry('user', prompt)); publish();
    setTimeout(() => {
      if (!session.isPlanning || planningTurns.get(session.id) !== turn) return;
      if (/facade|grid|variation|option/i.test(prompt)) {
        const grid = add(session.id, 'Create structural grid', 'Create a 6 × 4 structural grid using polylines. Inspect document units and context first.', session.scope);
        for (let i = 1; i <= 3; i++) add(session.id, `Facade option ${i}`, `Explore facade variation ${i} using the upstream grid. Create panels within the editable scope.`, session.scope, grid.id);
        session.conversation.push(entry('assistant', 'Preview plan: one shared grid, then three independent facade studies. Review the task tree and run the tasks when ready.'));
      } else {
        add(session.id, prompt.slice(0, 70), prompt, session.scope);
        session.conversation.push(entry('assistant', 'Preview plan: one task for this request. Review it and click Run tasks.'));
      }
      session.isPlanning = false; publish();
    }, 850);
  };
  return (cmd: PanelCommand) => {
    const task = 'id' in cmd ? state.tasks.find(t => t.id === cmd.id) : undefined;
    switch (cmd.type) {
      case 'ready': publish(); return;
      case 'preview.clear': state.previewTaskId = null; publish(); return;
      case 'task.merge':
        if (task?.status === 'ready' && !task.mergedBranch) {
          task.mergedBranch = `agent/${task.title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}-${task.id.slice(0, 8)}-r${task.revision}`;
          task.mergedAt = new Date().toISOString();
          task.activity.push(entry('status', 'Preview: merged into the model via ' + task.mergedBranch + '.'));
        }
        publish(); return;
      case 'task.show': state.previewTaskId = task?.resultPath ? task.id : null; publish(); return;
      case 'panel.error': console.error(cmd.message); return;
      case 'scope.selection': emit({ type: 'scope.selection', objectIds: ['preview-object-1', 'preview-object-2'] }); return;
      case 'settings.save': state.settings = { codexPath: cmd.codexPath, model: cmd.model, concurrency: cmd.concurrency }; break;
      case 'session.create': {
        const session = makeSession(cmd.title || cmd.prompt.slice(0, 70), cmd.id, cmd.scope);
        state.sessions.push(session); plan(session, cmd.prompt); return;
      }
      case 'session.message': {
        const session = state.sessions.find(s => s.id === cmd.id);
        if (session) { if (cmd.scope) session.scope = cmd.scope; plan(session, cmd.prompt); }
        return;
      }
      case 'session.stop': {
        const session = state.sessions.find(s => s.id === cmd.id);
        if (session) { planningTurns.set(session.id, (planningTurns.get(session.id) ?? 0) + 1); session.isPlanning = false; session.conversation.push(entry('status', 'Session stopped. History retained.')); }
        state.tasks.filter(t => t.sessionId === cmd.id && ['running', 'queued', 'stopping'].includes(t.status)).forEach(t => { t.status = 'stopped'; });
        pump(); return;
      }
      case 'session.archive':
      case 'session.restore': {
        const session = state.sessions.find(s => s.id === cmd.id);
        if (session) { session.archived = cmd.type === 'session.archive'; if (session.archived) { session.isPlanning = false; state.tasks.filter(t => t.sessionId === session.id && ['running', 'queued'].includes(t.status)).forEach(t => { t.status = 'stopped'; }); } }
        pump(); return;
      }
      case 'task.pause': if (task) { task.paused = true; if (['running', 'queued'].includes(task.status)) task.status = 'stopped'; } pump(); return;
      case 'task.resume': if (task) { task.paused = false; if (['stopped', 'failed', 'interrupted'].includes(task.status)) task.status = 'queued'; } pump(); return;
      case 'task.archive':
      case 'task.restore':
      case 'task.remove': {
        const ids = new Set([cmd.id]);
        let count = -1;
        while (count !== ids.size) { count = ids.size; state.tasks.forEach(t => { if (t.parentId && ids.has(t.parentId)) ids.add(t.id); }); }
        state.tasks.filter(t => ids.has(t.id)).forEach(t => { if (cmd.type === 'task.remove') t.deleted = true; else t.archived = cmd.type === 'task.archive'; if (cmd.type !== 'task.restore' && ['running', 'queued'].includes(t.status)) t.status = 'stopped'; });
        if (state.previewTaskId && ids.has(state.previewTaskId)) state.previewTaskId = null;
        pump(); return;
      }
      case 'task.add': { const created = add(cmd.sessionId, cmd.title, cmd.instructions, cmd.scope, cmd.parentId); if (cmd.id) created.id = cmd.id; if (cmd.branchFromResult) created.inputPath = state.tasks.find(t => t.id === cmd.parentId)?.resultPath; if (cmd.launch) { created.status = 'queued'; pump(); return; } break; }
      case 'task.update': if (task?.status === 'draft') Object.assign(task, { title: cmd.title, instructions: cmd.instructions, scope: cmd.scope }); break;
      case 'tasks.launch': state.tasks.forEach(t => { if (t.status === 'draft' && t.sessionId === cmd.sessionId && !t.paused && !t.archived && !t.deleted) t.status = 'queued'; }); pump(); return;
      case 'task.launch': if (task?.status === 'draft') task.status = 'queued'; pump(); return;
      case 'task.stop': if (task) { task.status = 'stopped'; task.activity.push(entry('status', 'Stopped. History retained.')); } pump(); return;
      case 'task.message': if (task) { task.activity.push(entry('user', cmd.message)); task.status = 'queued'; } pump(); return;
      case 'task.preview': emit({ type: 'control.error', message: 'Browser preview simulates tasks. Open RhinoClient inside Rhino to inspect real document copies.' }); return;
    }
    publish();
  };
}
function makeSession(title: string, id: string, scope: Scope): ModelingSession {
  return { id, title, scope: structuredClone(scope), createdAt: new Date().toISOString(), conversation: [], isPlanning: false, inputTokens: 0, outputTokens: 0 };
}
