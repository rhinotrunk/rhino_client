export type GraphAuthor = { id: string; name?: string | null; kind: string };
export type GraphCommit = {
  id: string; parents: string[]; generation: number; timestamp: string; message: string; author: GraphAuthor; refs: string[];
};
// A pasted image, already shrunk to a JPEG by the panel.
export type PromptImageData = { name: string; mediaType: string; data: string };
export type Scope = { wholeDocument?: boolean; objectIds: string[]; layerIds: string[] };
export type TaskStatus = 'draft' | 'queued' | 'running' | 'stopping' | 'stopped' | 'ready' | 'failed' | 'interrupted';
export type Activity = { kind: 'user' | 'assistant' | 'tool' | 'result' | 'error' | 'status'; text: string; at: string };
export type AgentTask = {
  id: string; sessionId: string; model?: string; title: string; instructions: string; parentId?: string | null; status: TaskStatus;
  archived?: boolean; deleted?: boolean; paused?: boolean; scope: Scope; activity: Activity[]; error?: string | null; revision: number;
  inputTokens: number; outputTokens: number; startedAt?: string | null; finishedAt?: string | null;
  resultPath?: string | null; workingPath?: string | null; inputPath?: string | null;
  baseCommit?: string | null; merging?: boolean; mergedBranch?: string | null; mergedAt?: string | null;
  opening?: boolean; prBranch?: string | null; prBase?: string | null; prOpenedAt?: string | null;
};
export type Settings = { codexPath: string; claudePath: string; model: string; concurrency: number };
export type ModelOption = { id: string; label: string; provider: 'codex' | 'claude'; available: boolean };
// Checkout replaces the model. 'ask' stops at unpushed changes and the host answers with 'checkout.unpushed'.
export type UnpushedChoice = 'ask' | 'push' | 'discard';
export type ModelingSession = {
  id: string; title: string; createdAt: string; scope: Scope; conversation: Activity[];
  archived?: boolean; isPlanning: boolean; inputTokens: number; outputTokens: number; model?: string | null;
};
export type ControlState = {
  previewTaskId?: string | null; tasks: AgentTask[]; sessions: ModelingSession[]; settings: Settings; models: ModelOption[];
  document: string; layers: { id: string; name: string }[]; storagePath: string;
};
export type PanelCommand =
  | { type: 'ready' | 'scope.selection' | 'preview.clear' }
  | { type: 'panel.error'; message: string }
  | { type: 'settings.save' } & Settings
  | { type: 'session.create'; id: string; title?: string; prompt: string; scope: Scope; model?: string; images?: PromptImageData[] }
  | { type: 'session.model'; id: string; model: string }
  | { type: 'session.message'; id: string; prompt: string; scope?: Scope; images?: PromptImageData[] }
  | { type: 'session.stop' | 'session.archive' | 'session.restore'; id: string }
  | { type: 'tasks.launch'; sessionId: string }
  | { type: 'task.add'; sessionId: string; id?: string; launch?: boolean; branchFromResult?: boolean; title: string; instructions: string; parentId?: string; scope: Scope }
  | { type: 'task.update'; id: string; title: string; instructions: string; scope: Scope }
  | { type: 'task.pause' | 'task.resume' | 'task.archive' | 'task.restore' | 'task.remove' | 'task.launch' | 'task.stop' | 'task.preview' | 'task.show' | 'task.merge' | 'task.pr.open' | 'task.pr.close'; id: string }
  // Retry a merge that conflicted: keep the model branch's version (ours) or the agent's (theirs) of every conflicting object.
  | { type: 'task.merge'; id: string; resolve: 'ours' | 'theirs' }
  | { type: 'task.pr.base'; id: string; branch: string }
  | { type: 'branch.merge'; theirs: string; into: string; resolve?: 'ours' | 'theirs' }
  | { type: 'task.message'; id: string; message: string; images?: PromptImageData[] }
  | { type: 'repos'; server: string }
  | { type: 'branches'; server: string; repo: string }
  | { type: 'pull'; server: string; repo: string; branch: string }
  | { type: 'checkout'; server: string; repo: string; branch: string; commit: string; message: string; unpushed: UnpushedChoice }
  | { type: 'push'; server: string; repo: string; branch: string; message: string }
  | { type: 'graph'; server: string; repo: string };
export type HostEvent =
  | { type: 'control.state' } & ControlState
  | { type: 'control.error'; message: string }
  | { type: 'scope.selection'; objectIds: string[] }
  | { type: 'session'; server: string; repo: string; branch: string }
  | { type: 'repos'; server: string; repos: string[]; message: string; error: boolean }
  | { type: 'branches'; server: string; repo: string; branches: string[]; message: string; error: boolean }
  | { type: 'graph'; repo: string; head?: string; commits: GraphCommit[]; truncated: boolean; message: string; error: boolean }
  | { type: 'status'; message: string; error: boolean; busy: boolean }
  | { type: 'merge.conflict'; id: string; title: string; branch: string; into: string; conflicts: number }
  | { type: 'checkout.unpushed'; commit: string; changes: number }
  // A push stopped because the model's base commit is not the branch tip. The panel offers another branch.
  | { type: 'push.detached'; commit: string }
  // The commit the model is based on, or null before its first pull or push.
  | { type: 'head'; repo: string; branch: string; commit: string | null }
  | { type: 'theme'; scheme: 'light' | 'dark' };

export function newId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `local-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}
export const budgetModel = 'gpt-6-luna';
