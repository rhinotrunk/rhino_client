import { previewHistory } from '../graph/fixture';
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
          const scheme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
          receive({ type: 'theme', scheme });
          receive({ type: 'session', server: 'http://localhost:5280/', repo: 'demo', branch: 'main' });
          receive({ type: 'status', message: 'Browser preview', error: false, busy: false });
          return;
        }
        if (command.type === 'repos') {
          receive({
            type: 'repos',
            server: command.server,
            error: false,
            message: '',
            repos: ['demo', 'site', 'tower'],
          });
          return;
        }
        if (command.type === 'branches') {
          receive({
            type: 'branches',
            server: command.server,
            repo: command.repo,
            error: false,
            message: '',
            branches: command.repo === 'site' ? ['main', 'north'] : command.repo === 'tower' ? ['main'] : ['main', 'design', 'review'],
          });
          return;
        }
        if (command.type === 'graph') {
          receive({
            type: 'graph',
            repo: command.repo,
            truncated: false,
            error: false,
            message: '',
            commits: previewHistory(),
          });
          return;
        }
        const detail =
          command.type === 'checkout'
            ? `checkout ${command.commit.replace(/^sha256:/, '').slice(0, 8)}`
            : `${command.type} ${command.repo} ${command.branch}`;
        receive({
          type: 'status',
          message: `Preview ${detail}`,
          error: false,
          busy: false,
        });
      });
    },
  };
}
