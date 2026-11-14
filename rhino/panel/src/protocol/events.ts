export type PanelCommand =
  | { type: 'ready' }
  | { type: 'pull'; server: string; repo: string; branch: string }
  | { type: 'push'; server: string; repo: string; branch: string; message: string };

export type HostEvent =
  | { type: 'session'; server: string; repo: string; branch: string }
  | { type: 'status'; message: string; error: boolean; busy: boolean }
  | { type: 'theme'; scheme: 'light' | 'dark' };
