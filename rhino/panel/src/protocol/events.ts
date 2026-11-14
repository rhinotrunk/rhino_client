export type PanelCommand = { type: 'ready' } | { type: 'button.click' };

export type HostEvent =
  | { type: 'state'; clicks: number; message: string }
  | { type: 'theme'; scheme: 'light' | 'dark' };
