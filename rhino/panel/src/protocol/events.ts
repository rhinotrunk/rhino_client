export type CommitSummary = { id: string; message: string };

export type PanelCommand =
  | { type: 'ready' }
  | { type: 'commits'; server: string; repo: string; branch: string }
  | { type: 'pull'; server: string; repo: string; branch: string; commit: string }
  | { type: 'push'; server: string; repo: string; branch: string; message: string };

export type HostEvent =
  | { type: 'session'; server: string; repo: string; branch: string }
  | { type: 'commits'; commits: CommitSummary[]; message: string; error: boolean }
  | { type: 'status'; message: string; error: boolean; busy: boolean }
  | { type: 'theme'; scheme: 'light' | 'dark' };
