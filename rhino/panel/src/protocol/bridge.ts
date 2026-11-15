import type { HostEvent, PanelCommand } from './events';
import { createPreview } from './preview';
declare const __PREVIEW__: boolean;
type Listener = (event: HostEvent) => void;
type Transport = { postMessage(message: string): void };
declare global {
  interface Window {
    eto?: Transport;
    chrome?: { webview?: Transport };
    rhinoClient?: { receive(event: HostEvent): void };
  }
}
export interface Bridge {
  readonly kind: 'native' | 'preview' | 'unavailable';
  send(command: PanelCommand): void;
  subscribe(listener: Listener): () => void;
}
export function createBridge(): Bridge {
  const listeners = new Set<Listener>();
  const receive = (event: HostEvent) => { for (const listener of listeners) listener(event); };
  window.rhinoClient = { receive };
  const transport = window.eto ?? window.chrome?.webview;
  const preview = !transport && __PREVIEW__ ? createPreview(receive) : undefined;
  return {
    kind: transport ? 'native' : preview ? 'preview' : 'unavailable',
    subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    send(command) {
      if (transport) transport.postMessage(JSON.stringify(command));
      else if (preview) preview(command);
    },
  };
}
