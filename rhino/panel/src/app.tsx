import { useEffect, useState } from 'react';
import { Hand, PlugZap } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Separator } from '@/components/ui/separator';
import type { Bridge } from './protocol/bridge';

export function App({ bridge }: { bridge: Bridge }) {
  const [state, setState] = useState<{ clicks: number; message: string }>();

  useEffect(() => {
    const unsubscribe = bridge.subscribe((event) => {
      if (event.type === 'theme') {
        document.documentElement.dataset['scheme'] = event.scheme;
        document.documentElement.classList.toggle('dark', event.scheme === 'dark');
      } else if (event.type === 'state') {
        setState(event);
      }
    });
    bridge.send({ type: 'ready' });
    return unsubscribe;
  }, [bridge]);

  return (
    <div className="flex min-h-screen flex-col">
      <header className="flex flex-wrap items-center justify-between gap-2 px-4 py-3">
        <h1 className="text-sm font-semibold">RhinoClient</h1>
        <Badge variant="outline" className="gap-1.5 font-normal">
          <PlugZap className="size-3" aria-hidden="true" />
          {bridge.kind === 'preview' ? 'Preview' : state ? 'Connected' : 'Disconnected'}
        </Badge>
      </header>
      <Separator />
      <section aria-labelledby="hello-title" className="space-y-4 p-4">
        <div className="space-y-1">
          <h2 id="hello-title" className="text-sm font-medium">Say hello to Rhino</h2>
          <p className="text-xs leading-relaxed text-muted-foreground">
            Send a greeting and see Rhino’s reply here.
          </p>
        </div>
        <Button className="w-full" disabled={!state} onClick={() => bridge.send({ type: 'button.click' })}>
          <Hand aria-hidden="true" /> Say hello
        </Button>
        <p role="status" className="break-words text-xs text-muted-foreground">
          {state?.message ?? (bridge.kind === 'unavailable' ? 'Could not connect to Rhino.' : 'Connecting…')}
        </p>
      </section>
    </div>
  );
}
