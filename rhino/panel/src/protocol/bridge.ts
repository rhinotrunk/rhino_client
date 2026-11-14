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
  return {
    kind: transport ? 'native' : __PREVIEW__ ? 'preview' : 'unavailable',
    subscribe(listener) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    send(command) {
      if (transport) {
        transport.postMessage(JSON.stringify(command));
        return;
      }
      if (!__PREVIEW__) return;
      queueMicrotask(() => {
        if (command.type === 'ready') {
          receive({ type: 'session', server: 'http://localhost:5280/', repo: 'demo', branch: 'main' });
          receive({ type: 'status', message: 'Browser preview', error: false, busy: false });
          return;
        }
        const verb = command.type === 'pull' ? 'Pull' : 'Push';
        receive({
          type: 'status',
          message: `Preview ${verb.toLowerCase()} ${command.repo} ${command.branch}`,
          error: false,
          busy: false,
        });
      });
    },
  };
}
