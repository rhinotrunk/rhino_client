import { useState } from 'react';
import { Check, ChevronDown, GitBranch } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { cn } from '@/lib/utils';
import type { BranchInfo } from '@/engine/mock';
import { AgentIcon, RunStatus, TokenChip } from './parts';
import { useEngine, useTrunk } from './store';

export function BranchPicker() {
  const engine = useEngine();
  const { sync, branches } = useTrunk();
  const [open, setOpen] = useState(false);
  const current = branches.find((b) => b.name === sync.branch);
  const humans = branches.filter((b) => b.kind === 'human');
  const agents = branches.filter((b) => b.kind === 'agent');
  const pick = (b: BranchInfo) => { engine.switchBranch(b.name); setOpen(false); };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="max-w-44 gap-1.5 px-2.5">
          {current?.kind === 'agent' ? <AgentIcon /> : <GitBranch />}
          <span className="truncate">{sync.branch}</span>
          <ChevronDown className="opacity-60" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-72 p-1">
        <Group label="Branches">
          {humans.map((b) => <Row key={b.name} branch={b} active={b.name === sync.branch} onPick={pick} />)}
        </Group>
        <Group label="AI agents">
          {agents.length ? agents.map((b) => <Row key={b.name} branch={b} active={b.name === sync.branch} onPick={pick} />)
            : <p className="px-2 py-1.5 text-sm text-muted-foreground">No agent branches yet. Start one with +.</p>}
        </Group>
      </PopoverContent>
    </Popover>
  );
}

function Group({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="py-1">
      <div className="px-2 py-1 text-xs font-medium text-muted-foreground">{label}</div>
      {children}
    </div>
  );
}

function Row({ branch, active, onPick }: { branch: BranchInfo; active: boolean; onPick: (b: BranchInfo) => void }) {
  const run = branch.run;
  return (
    <button
      type="button"
      onClick={() => onPick(branch)}
      className={cn('flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm hover:bg-accent focus-visible:bg-accent focus-visible:outline-none', active && 'font-medium')}
    >
      {branch.kind === 'agent' ? <AgentIcon /> : <GitBranch className="size-3.5 text-muted-foreground" aria-hidden="true" />}
      <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{branch.name}</span>
      {run && <TokenChip tokens={run.total} live={run.status === 'running'} />}
      {run && run.status !== 'running' && <RunStatus status={run.status} />}
      {active && <Check className="size-3.5" aria-label="current branch" />}
    </button>
  );
}
