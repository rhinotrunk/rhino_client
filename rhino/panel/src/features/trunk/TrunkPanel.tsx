import { useState } from 'react';
import { ScrollArea } from '@/components/ui/scroll-area';
import { Toaster } from '@/components/ui/sonner';
import { TooltipProvider } from '@/components/ui/tooltip';
import { createMockEngine } from '@/engine/mock';
import { AgentRuns } from './AgentRuns';
import { CommitDetail } from './CommitDetail';
import { CommitGraph } from './CommitGraph';
import { Header } from './Header';
import { ErrorBoundary } from './ErrorBoundary';
import { EngineContext } from './store';

/** The RhinoTrunk panel. Runs on the mock engine until the C# bridge serves engine data. */
export function TrunkPanel() {
  return <ErrorBoundary><Panel /></ErrorBoundary>;
}

function Panel() {
  const [engine] = useState(createMockEngine);
  return (
    <EngineContext.Provider value={engine}>
      <TooltipProvider delayDuration={300}>
        <div className="flex h-screen flex-col">
          <Header />
          <ScrollArea className="min-h-0 flex-1">
            <AgentRuns />
            <CommitGraph />
            <CommitDetail />
          </ScrollArea>
        </div>
        <Toaster position="bottom-center" />
      </TooltipProvider>
    </EngineContext.Provider>
  );
}
