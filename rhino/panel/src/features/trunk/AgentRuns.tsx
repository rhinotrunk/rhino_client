import { GitMerge, Square } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Progress } from '@/components/ui/progress';
import { cn } from '@/lib/utils';
import { formatTokens, shortHash } from '@/engine/map';
import type { BranchInfo } from '@/engine/mock';
import { AgentIcon, RunStatus } from './parts';
import { elapsed, useEngine, useNow, useTrunk } from './store';

const MODEL_LABEL: Record<string, string> = { 'claude-opus-5-5': 'Opus 5.5', 'claude-sonnet-5-5': 'Sonnet 5.5', 'claude-haiku-5-5': 'Haiku 5.5' };

/** Agent branches that are running or waiting for review. Merged ones drop out. */
export function AgentRuns() {
  const { branches } = useTrunk();
  const active = branches.filter((b) => b.run && b.run.status !== 'merged');
  if (!active.length) return null;
  const running = active.filter((b) => b.run?.status === 'running').length;
  return (
    <section aria-label="AI agents" className="space-y-2 border-b p-3">
      <div className="flex items-baseline justify-between">
        <h2 className="text-sm font-medium text-muted-foreground">AI agents</h2>
        {running > 0 && <span className="text-sm text-agent">{running} running</span>}
      </div>
      {active.map((b) => <RunCard key={b.name} branch={b} />)}
    </section>
  );
}

function RunCard({ branch }: { branch: BranchInfo }) {
  const engine = useEngine();
  const { sync } = useTrunk();
  const run = branch.run!;
  const live = run.status === 'running';
  const now = useNow(live);
  const end = run.finishedAt ? new Date(run.finishedAt).getTime() : now;
  const pct = Math.round(run.used * 100);
  const meter = run.status === 'budget' || pct >= 100 ? 'bg-deleted' : pct >= 80 ? 'bg-modified' : 'bg-agent';
  const target = sync.branch.startsWith('agent/') ? 'main' : sync.branch;

  const merge = () => {
    const id = engine.merge(branch.name, target);
    if (id) toast.success(`Merged ${branch.name} into ${target}`, { description: `${run.commits} commits · ${formatTokens(run.total)} tokens · ${shortHash(id)}` });
  };

  return (
    <article className={cn('space-y-2 rounded-lg border bg-card p-3', live && 'border-agent/40')}>
      <div className="flex items-center gap-2">
        <AgentIcon />
        <button type="button" className="min-w-0 flex-1 truncate text-left font-mono text-[12px] font-medium hover:underline" onClick={() => engine.select(branch.head)}>
          {branch.name}
        </button>
        <RunStatus status={run.status} />
        <span className="font-mono text-xs tabular-nums text-muted-foreground">{elapsed(run.startedAt, end)}</span>
      </div>

      <p className="line-clamp-2 text-sm text-muted-foreground">“{run.prompt}”</p>

      <div className="space-y-1">
        <div className="flex items-baseline justify-between gap-2 text-sm">
          <span className={cn('truncate', live ? 'text-foreground' : 'text-muted-foreground')}>{run.step}{live && '…'}</span>
          <span className="shrink-0 font-mono tabular-nums">
            <b className="font-semibold">{formatTokens(run.total)}</b>
            <span className="text-muted-foreground"> / {formatTokens(run.budget)} tokens</span>
          </span>
        </div>
        <Progress value={pct} className="h-1.5 bg-muted" indicatorClassName={meter} aria-label={`${pct}% of token budget used`} />
        <div className="flex flex-wrap gap-x-3 font-mono text-xs tabular-nums text-muted-foreground">
          <span>in {formatTokens(run.inputTokens)}</span>
          <span>out {formatTokens(run.outputTokens)}</span>
          <span>{run.commits} commit{run.commits === 1 ? '' : 's'}</span>
          <span>{MODEL_LABEL[run.model] ?? run.model}</span>
        </div>
      </div>

      <div className="flex justify-end gap-1.5">
        {live ? (
          <Button variant="outline" size="sm" onClick={() => engine.stopAgent(branch.name)}><Square className="fill-current" />Stop</Button>
        ) : (
          <>
            <Button variant="ghost" size="sm" onClick={() => engine.select(branch.head)}>Review</Button>
            <Button size="sm" disabled={!run.commits} onClick={merge}><GitMerge />Merge into {target}</Button>
          </>
        )}
      </div>
    </article>
  );
}
