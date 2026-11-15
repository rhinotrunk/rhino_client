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
    case 'graph':
      queueMicrotask(() => receive({ type: 'graph', repo: command.repo, truncated: false, error: false, message: '', commits: previewHistory() }));
      return true;
    case 'commits':
      queueMicrotask(() => receive({
        type: 'commits', error: false, message: '',
        commits: [
          { id: 'sha256:' + 'a'.repeat(64), message: 'Update geometry' },
          { id: 'sha256:' + 'b'.repeat(64), message: 'Initial commit' },
        ],
      }));
      return true;
    case 'pull':
    case 'push': {
      const verb = command.type === 'pull' ? 'pull' : 'push';
      queueMicrotask(() => receive({ type: 'status', message: `Preview ${verb} ${command.repo} ${command.branch}`, error: false, busy: false }));
      return true;
    }
    default:
      return false;
  }
}
