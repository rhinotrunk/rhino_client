import { previewHistory } from '../graph/fixture';
import type { HostEvent, PanelCommand } from './events';

// Browser-only stand-in for the git host. Returns true when it handled the command.
export function repoPreview(command: PanelCommand, receive: (event: HostEvent) => void): boolean {
  switch (command.type) {
    case 'ready':
      queueMicrotask(() => {
        receive({ type: 'session', server: 'http://localhost:5280/', repo: 'demo', branch: 'main' });
        receive({ type: 'status', message: 'Browser preview', error: false, busy: false });
      });
      return false; // the agent preview also answers 'ready'
    case 'repos':
      queueMicrotask(() => receive({ type: 'repos', server: command.server, error: false, message: '', repos: ['demo', 'site', 'tower'] }));
      return true;
    case 'branches':
      queueMicrotask(() => receive({
        type: 'branches', server: command.server, repo: command.repo, error: false, message: '',
        branches: command.repo === 'site' ? ['main', 'north'] : command.repo === 'tower' ? ['main'] : ['main', 'design', 'review'],
      }));
      return true;
    case 'graph':
      queueMicrotask(() => receive({ type: 'graph', repo: command.repo, truncated: false, error: false, message: '', commits: previewHistory() }));
      return true;
    case 'pull':
    case 'push':
    case 'checkout': {
      const detail = command.type === 'checkout'
        ? `checkout ${command.commit.replace(/^sha256:/, '').slice(0, 8)}`
        : `${command.type} ${command.repo} ${command.branch}`;
      queueMicrotask(() => receive({ type: 'status', message: `Preview ${detail}`, error: false, busy: false }));
      return true;
    }
    default:
      return false;
  }
}
