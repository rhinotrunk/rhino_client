import './styles/panel.css';
import { createBridge } from './protocol/bridge';
const root = document.getElementById('root');
if (!root) throw new Error('Missing panel root.');

const bridge = createBridge();
const connected = bridge.kind !== 'unavailable';

function field(labelText: string, value: string): HTMLInputElement {
  const label = document.createElement('label');
  label.textContent = labelText;
  const input = document.createElement('input');
  input.type = 'text';
  input.value = value;
  label.append(input);
  root!.append(label);
  return input;
}

const server = field('Server', 'http://localhost:5280/');
const repo = field('Repository', '');
const branch = field('Branch', 'main');
const commit = field('Commit', '');
commit.placeholder = 'Branch tip';
const commitList = document.createElement('ul');
commitList.className = 'commits';
commitList.hidden = true;
commit.parentElement?.after(commitList);
const message = field('Message', 'Update geometry');

const actions = document.createElement('div');
actions.className = 'actions';
const pull = document.createElement('button');
pull.type = 'button';
pull.textContent = 'Pull';
const push = document.createElement('button');
push.type = 'button';
push.textContent = 'Push';
actions.append(pull, push);

const status = document.createElement('p');
status.setAttribute('role', 'status');
status.textContent = connected ? 'Connecting…' : 'Could not connect to Rhino.';
root.append(actions, status);

let ready = false;
function setBusy(busy: boolean) {
  pull.disabled = busy || !ready;
  push.disabled = busy || !ready;
}
setBusy(true);

function target() {
  return { server: server.value.trim(), repo: repo.value.trim(), branch: branch.value.trim() };
}

function shortId(id: string) {
  const hex = id.startsWith('sha256:') ? id.slice('sha256:'.length) : id;
  return hex.slice(0, 8);
}

function chosenCommit() {
  const text = commit.value.trim();
  const pinned = commit.dataset['id'] ?? '';
  const pinnedMessage = commit.dataset['message'] ?? '';
  if (pinned && (text === pinned || text === pinnedMessage)) return pinned;
  return text;
}

commit.addEventListener('focus', () => {
  const spec = target();
  if (!spec.repo) return;
  bridge.send({ type: 'commits', ...spec });
});
commit.addEventListener('input', () => {
  const pinnedMessage = commit.dataset['message'] ?? '';
  const pinned = commit.dataset['id'] ?? '';
  if (commit.value.trim() !== pinnedMessage && commit.value.trim() !== pinned) {
    delete commit.dataset['id'];
    delete commit.dataset['message'];
  }
});

pull.addEventListener('click', () => {
  setBusy(true);
  commitList.hidden = true;
  status.classList.remove('error');
  status.textContent = 'Pulling…';
  bridge.send({ type: 'pull', ...target(), commit: chosenCommit() });
});
push.addEventListener('click', () => {
  setBusy(true);
  status.classList.remove('error');
  status.textContent = 'Pushing…';
  bridge.send({ type: 'push', ...target(), message: message.value.trim() });
});

bridge.subscribe((event) => {
  if (event.type === 'theme') {
    document.documentElement.dataset['scheme'] = event.scheme;
  } else if (event.type === 'commits') {
    commitList.replaceChildren();
    if (event.error || event.commits.length === 0) {
      commitList.hidden = true;
      if (event.error) {
        status.classList.add('error');
        status.textContent = event.message;
      }
      return;
    }
    for (const item of event.commits) {
      const row = document.createElement('li');
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = item.message || '(no message)';
      const id = document.createElement('span');
      id.className = 'id';
      id.textContent = shortId(item.id);
      button.append(id);
      button.addEventListener('mousedown', (mouse) => mouse.preventDefault());
      button.addEventListener('click', () => {
        commit.value = item.message;
        commit.dataset['id'] = item.id;
        commit.dataset['message'] = item.message;
        commitList.hidden = true;
      });
      row.append(button);
      commitList.append(row);
    }
    commitList.hidden = false;
  } else if (event.type === 'session') {
    server.value = event.server;
    repo.value = event.repo;
    branch.value = event.branch;
  } else if (event.type === 'status') {
    ready = connected;
    setBusy(event.busy);
    status.classList.toggle('error', event.error);
    status.textContent = event.message;
  }
});
bridge.send({ type: 'ready' });
