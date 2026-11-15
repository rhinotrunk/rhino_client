import { previewHistory } from '../graph/fixture';
import type { HostEvent, PanelCommand } from './events';

let head: { branch: string; commit: string | null } = { branch: 'main', commit: tipOf('main') };

function tipOf(branch: string): string | null {
  return previewHistory().find((commit) => commit.refs.includes(branch))?.id ?? null;
}

// Browser-only stand-in for the git host. Returns true when it handled the command.
export function repoPreview(command: PanelCommand, receive: (event: HostEvent) => void): boolean {
  switch (command.type) {
    case 'ready':
      queueMicrotask(() => {
        receive({ type: 'session', server: 'http://localhost:5280/', repo: 'demo', branch: head.branch });
        receive({ type: 'status', message: 'Browser preview', error: false, busy: false });
        receive({ type: 'head', repo: 'demo', ...head });
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
    case 'checkout': {
      const short = command.commit.replace(/^sha256:/, '').slice(0, 8);
      queueMicrotask(() => {
        if (command.unpushed === 'ask') {
          receive({ type: 'status', message: `Preview: 2 unpushed changes. Push or discard them to check out ${short}.`, error: false, busy: false });
          receive({ type: 'checkout.unpushed', commit: command.commit, changes: 2 });
          return;
        }
        if (command.unpushed === 'push' && pushStops(command.branch, receive, 'Checkout stopped. ')) return;
        const first = command.unpushed === 'push' ? `push ${command.repo} ${command.branch}, then ` : 'discard, then ';
        head = { branch: command.branch, commit: command.commit };
        receive({ type: 'status', message: `Preview ${first}checkout ${short}`, error: false, busy: false });
        receive({ type: 'head', repo: command.repo, ...head });
      });
      return true;
    }
    case 'push':
      queueMicrotask(() => {
        if (pushStops(command.branch, receive, '')) return;
        head = { ...head, branch: command.branch };
        receive({ type: 'status', message: `Preview push ${command.repo} ${command.branch}`, error: false, busy: false });
        receive({ type: 'head', repo: command.repo, ...head });
      });
      return true;
    case 'branch.merge':
      queueMicrotask(() => receive({ type: 'status', message: `Preview: merged ${command.theirs} into ${command.into}`, error: false, busy: false }));
      return true;
    case 'pull':
      queueMicrotask(() => {
        head = { branch: command.branch, commit: tipOf(command.branch) };
        receive({ type: 'status', message: `Preview pull ${command.repo} ${command.branch}`, error: false, busy: false });
        receive({ type: 'head', repo: command.repo, ...head });
      });
      return true;
    default:
      return false;
  }
}

// Mirrors the host: an existing branch whose tip is not the model's commit cannot take the push.
function pushStops(branch: string, receive: (event: HostEvent) => void, prefix: string): boolean {
  const tip = tipOf(branch);
  if (!head.commit || !tip || tip === head.commit) return false;
  const short = head.commit.replace(/^sha256:/, '').slice(0, 8);
  receive({
    type: 'status',
    message: `${prefix}This model is at ${short}, which is not the latest commit on '${branch}'. A push can only add to the tip of a branch, so push the 2 changes to another branch.`,
    error: false,
    busy: false,
  });
  receive({ type: 'push.detached', commit: head.commit });
  return true;
}
