import './styles/panel.css';
import { createRoot } from 'react-dom/client';
import { Component, useEffect, useState } from 'react';
import type { ErrorInfo, ReactNode } from 'react';
import { App } from './app';
import { Header, type TabId } from './header';
import { History } from './graph/history';
import { useRepo } from './repo';
import { SettingsDialog } from './settings';
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

const bridge = createBridge();
function report(message: string) {
  bridge.send({ type: 'panel.error', message: message.slice(0, 4000) });
}
window.addEventListener('error', event => report(event.message));
window.addEventListener('unhandledrejection', event => report(String(event.reason)));
class PanelErrorBoundary extends Component<{ children: ReactNode }, { error: string | null }> {
  state: { error: string | null } = { error: null };
  static getDerivedStateFromError(error: Error) { return { error: error.message }; }
  componentDidCatch(error: Error, info: ErrorInfo) { report(`${error.message}\n${info.componentStack}`); }
  render() {
    if (this.state.error) return <div className="connection-screen" role="alert">
      <h1>The panel could not display this session</h1><p>{this.state.error}</p>
      <p>Your tasks and model copies are retained by Rhino.</p>
      <button className="primary" onClick={() => this.setState({ error: null })}>Reconnect panel</button>
    </div>;
    return this.props.children;
  }
}
// One sticky header serves both tabs. Graph (git history and pull requests) comes first; Agents second.
// Both stay mounted so neither loses state when you switch tabs.
function Shell() {
  const repo = useRepo(bridge);
  const [tab, setTab] = useState<TabId>('graph');
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [focus, setFocus] = useState(0);
  // Rhino answers 'ready' with theme/status/state. If that reply is lost (page loaded before the host was
  // listening, or the panel was re-docked), ask again until anything arrives. 'ready' is idempotent.
  useEffect(() => {
    let heard = false;
    const stop = bridge.subscribe(() => { heard = true; });
    let attempts = 0;
    const timer = window.setInterval(() => {
      if (heard || ++attempts > 20) { window.clearInterval(timer); return; }
      bridge.send({ type: 'ready' });
    }, 1500);
    return () => { stop(); window.clearInterval(timer); };
  }, []);
  return <div className="flex h-full flex-col bg-background text-foreground">
    <Header repo={repo} tab={tab} onTab={setTab} onSettings={() => setSettingsOpen(true)} onPrs={() => { setTab('graph'); setFocus(value => value + 1); }} />
    <div className="relative min-h-0 flex-1">
      <div role="tabpanel" id="pane-graph" aria-labelledby="tab-graph" hidden={tab !== 'graph'} className="absolute inset-0"><History repo={repo} focus={focus} /></div>
      <div role="tabpanel" id="pane-agents" aria-labelledby="tab-agents" hidden={tab !== 'agents'} className="agents absolute inset-0"><PanelErrorBoundary><App bridge={bridge} /></PanelErrorBoundary></div>
    </div>
    {settingsOpen && <SettingsDialog bridge={bridge} repo={repo} current={repo.control?.settings} onClose={() => setSettingsOpen(false)} />}
  </div>;
}
createRoot(root).render(<Shell />);
