import './styles/panel.css';
import { createBridge } from './protocol/bridge';

const root = document.getElementById('root');
if (!root) throw new Error('Missing panel root.');

const bridge = createBridge();
const button = document.createElement('button');
button.type = 'button';
button.textContent = 'Say hello';
button.disabled = true;

const status = document.createElement('p');
status.setAttribute('role', 'status');
status.textContent = bridge.kind === 'unavailable' ? 'Could not connect to Rhino.' : 'Connecting…';

root.append(button, status);
button.addEventListener('click', () => bridge.send({ type: 'button.click' }));
bridge.subscribe((event) => {
  if (event.type === 'theme') {
    document.documentElement.dataset['scheme'] = event.scheme;
  } else if (event.type === 'state') {
    button.disabled = false;
    status.textContent = event.message;
  }
});
bridge.send({ type: 'ready' });
