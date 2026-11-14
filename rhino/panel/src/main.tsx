import './styles/panel.css';
import { createRoot } from 'react-dom/client';
import { App } from './app';
import { createBridge } from './protocol/bridge';

const root = document.getElementById('root');
if (!root) throw new Error('Missing panel root.');

const preferredTheme = window.matchMedia('(prefers-color-scheme: dark)');
const syncTheme = () => {
  if (!document.documentElement.dataset['scheme']) {
    document.documentElement.classList.toggle('dark', preferredTheme.matches);
  }
};
syncTheme();
preferredTheme.addEventListener('change', syncTheme);

createRoot(root).render(<App bridge={createBridge()} />);
