import { Bot, LoaderCircle } from 'lucide-react';
import { cn } from '@/lib/utils';
import { formatTokens, type AgentRunStatus, type DiffEntry } from '@/engine/map';

const OP = { added: ['A', 'text-added'], modified: ['M', 'text-modified'], deleted: ['D', 'text-deleted'] } as const;

export function OpLetter({ change }: { change: DiffEntry['change'] }) {
  const [letter, color] = OP[change];
  return <span className={cn('text-center font-mono text-sm font-semibold', color)} aria-label={change}>{letter}</span>;
}

export function Counts({ added, modified, deleted }: { added: number; modified: number; deleted: number }) {
  return (
    <span className="inline-flex gap-1 font-mono text-xs tabular-nums">
      {added > 0 && <span className="rounded-sm bg-added-soft px-1 text-added">+{added}</span>}
      {modified > 0 && <span className="rounded-sm bg-modified-soft px-1 text-modified">~{modified}</span>}
      {deleted > 0 && <span className="rounded-sm bg-deleted-soft px-1 text-deleted">−{deleted}</span>}
    </span>
  );
}

export function TokenChip({ tokens, live }: { tokens: number; live?: boolean }) {
  return (
    <span className="inline-flex items-center gap-1 rounded-sm bg-agent-soft px-1 font-mono text-xs tabular-nums text-agent">
      {live && <LoaderCircle className="size-2.5 animate-spin" aria-hidden="true" />}
      {formatTokens(tokens)} tok
    </span>
  );
}

const STATUS: Record<AgentRunStatus, [string, string]> = {
  running: ['Running', 'bg-agent-soft text-agent'],
  done: ['Ready to review', 'bg-added-soft text-added'],
  stopped: ['Stopped', 'bg-muted text-muted-foreground'],
  budget: ['Budget reached', 'bg-modified-soft text-modified'],
  failed: ['Failed', 'bg-deleted-soft text-deleted'],
  merged: ['Merged', 'bg-muted text-muted-foreground'],
};

export function RunStatus({ status }: { status: AgentRunStatus }) {
  const [label, color] = STATUS[status];
  return (
    <span className={cn('inline-flex items-center gap-1 rounded-md px-1.5 text-xs font-medium leading-5', color)}>
      {status === 'running' && <LoaderCircle className="size-3 animate-spin" aria-hidden="true" />}
      {label}
    </span>
  );
}

export function AgentIcon({ className }: { className?: string }) {
  return <Bot className={cn('size-3.5 text-agent', className)} aria-hidden="true" />;
}
