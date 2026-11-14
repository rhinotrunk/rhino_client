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

pull.addEventListener('click', () => {
  setBusy(true);
  status.classList.remove('error');
  status.textContent = 'Pulling…';
  bridge.send({ type: 'pull', ...target() });
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
