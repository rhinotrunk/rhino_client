import { useEffect } from 'react';
import { TrunkPanel } from '@/features/trunk/TrunkPanel';
import type { Bridge } from './protocol/bridge';

export function App({ bridge }: { bridge: Bridge }) {
  useEffect(() => {
    const unsubscribe = bridge.subscribe((event) => {
      if (event.type === 'theme') {
        document.documentElement.dataset['scheme'] = event.scheme;
        document.documentElement.classList.toggle('dark', event.scheme === 'dark');
      }
    });
    bridge.send({ type: 'ready' });
    return unsubscribe;
  }, [bridge]);

  return <TrunkPanel />;
}
