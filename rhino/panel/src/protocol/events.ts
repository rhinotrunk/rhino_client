export type CommitSummary = { id: string; message: string };

export type GraphAuthor = { id: string; name?: string | null; kind: string };

export type GraphCommit = {
  id: string;
  parents: string[];
  generation: number;
  timestamp: string;
  message: string;
  author: GraphAuthor;
  refs: string[];
};

export type PanelCommand =
  | { type: 'ready' }
  | { type: 'commits'; server: string; repo: string; branch: string }
  | { type: 'pull'; server: string; repo: string; branch: string; commit: string }
  | { type: 'push'; server: string; repo: string; branch: string; message: string }
  | { type: 'graph'; server: string; repo: string };

export type HostEvent =
  | { type: 'session'; server: string; repo: string; branch: string }
  | { type: 'commits'; commits: CommitSummary[]; message: string; error: boolean }
  | { type: 'graph'; repo: string; commits: GraphCommit[]; truncated: boolean; message: string; error: boolean }
  | { type: 'status'; message: string; error: boolean; busy: boolean }
  | { type: 'theme'; scheme: 'light' | 'dark' };
