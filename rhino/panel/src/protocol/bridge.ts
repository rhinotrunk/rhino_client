import type { HostEvent, PanelCommand } from './events';

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
  const receive = (event: HostEvent) => {
    for (const listener of listeners) listener(event);
  };
  window.rhinoClient = { receive };

  // Eto's shim is ready at document start on macOS. WebView2 exists earlier
  // than the shim on Windows; Eto receives its messages on the same channel.
  const transport = window.eto ?? window.chrome?.webview;
  let clicks = 0;
  return {
    kind: transport ? 'native' : __PREVIEW__ ? 'preview' : 'unavailable',
    subscribe(listener) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    send(command) {
      if (transport) {
        transport.postMessage(JSON.stringify(command));
      } else if (__PREVIEW__) {
        if (command.type === 'button.click') clicks++;
        queueMicrotask(() => receive({
          type: 'state',
          clicks,
          message: clicks === 0 ? 'Browser preview' : `Preview received click ${clicks}`,
        }));
      }
    },
  };
}
