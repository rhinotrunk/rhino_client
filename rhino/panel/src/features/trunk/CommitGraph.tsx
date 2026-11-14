import { useMemo } from 'react';
import { Check, X } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { cn } from '@/lib/utils';
import { toCheck, toCommitRows, tokensFromMeta, type CommitRow } from '@/engine/map';
import { WORKING } from '@/engine/mock';
import { AgentIcon, Counts, TokenChip } from './parts';
import { useEngine, useNow, useTrunk } from './store';

const ROW = 56, COL = 40;
const laneX = (lane: number) => 14 + lane * 16;

export function CommitGraph() {
  const engine = useEngine();
  const { commits, diffs, checks, meta, selected, fresh, sync, branches, localChanges } = useTrunk();
  const now = useNow(true, 30_000);
  const mainHead = branches.find((b) => b.name === 'main')?.head;
  const running = new Set(branches.filter((b) => b.run?.status === 'running').map((b) => b.name));

  const rows: CommitRow[] = useMemo(() => toCommitRows({ commits, truncated: false }, new Date(now), mainHead).map((r) => {
    const d = diffs[r.id] ?? [];
    return {
      ...r,
      counts: { added: d.filter((x) => x.change === 'added').length, modified: d.filter((x) => x.change === 'modified').length, deleted: d.filter((x) => x.change === 'deleted').length },
      check: toCheck(checks[r.id] ?? []),
      tokens: tokensFromMeta(meta[r.id])?.total,
    };
  }), [commits, diffs, checks, meta, now, mainHead]);

  const index = new Map(rows.map((r, i) => [r.id, i]));
  const Y = (i: number) => (i + 1) * ROW + ROW / 2; // row 0 is the working copy

  // "You are here": uncommitted changes on the current branch, drawn above its head.
  // A branch with no commits of its own still gets a side lane, so it never looks like main.
  const headIndex = sync.head ? index.get(sync.head) : undefined;
  const headRow = headIndex === undefined ? undefined : rows[headIndex];
  let workLane = headRow?.lane ?? 0;
  if (sync.branch !== 'main' && workLane === 0 && headIndex !== undefined) {
    const used = new Set(rows.slice(0, headIndex + 1).map((r) => r.lane));
    workLane = 1; while (used.has(workLane)) workLane++;
  }
  const workIsAgent = sync.branch.startsWith('agent/');
  const workStroke = workIsAgent ? 'hsl(var(--agent))' : 'hsl(var(--foreground) / 0.45)';
  const work = { added: localChanges.filter((c) => c.change === 'added').length, modified: localChanges.filter((c) => c.change === 'modified').length, deleted: localChanges.filter((c) => c.change === 'deleted').length };
  const lanes = Math.max(1, workLane + 1, ...rows.map((r) => r.lane + 1));

  return (
    <section aria-label="History">
      <h2 className="px-3 pb-1 pt-3 text-sm font-medium text-muted-foreground">History</h2>
      <div className="relative px-1.5">
        <svg width={Math.max(COL, laneX(lanes))} height={(rows.length + 1) * ROW} className="pointer-events-none absolute left-1.5 top-0" aria-hidden="true">
          {rows.flatMap((r, i) => r.parentIds.map((p) => {
            const j = index.get(p); const parent = j === undefined ? undefined : rows[j];
            if (!parent || j === undefined) return null;
            const x1 = laneX(r.lane), y1 = Y(i), x2 = laneX(parent.lane), y2 = Y(j);
            const agentEdge = (r.lane !== 0 ? r : parent).isAgent && Math.max(r.lane, parent.lane) > 0;
            const stroke = agentEdge ? 'hsl(var(--agent))' : 'hsl(var(--foreground) / 0.45)';
            if (x1 === x2) return <line key={r.id + p} x1={x1} y1={y1} x2={x2} y2={y2} stroke={stroke} strokeWidth={2} />;
            // Fork (child on a side lane): run down the side lane, curve into the parent.
            // Merge (parent on a side lane): curve out of the child, run down the side lane.
            const d = r.lane > parent.lane
              ? `M${x1} ${y1} L${x1} ${y2 - ROW} C ${x1} ${y2 - ROW * 0.4}, ${x2} ${y2 - ROW * 0.6}, ${x2} ${y2}`
              : `M${x1} ${y1} C ${x1} ${y1 + ROW * 0.6}, ${x2} ${y1 + ROW * 0.4}, ${x2} ${y1 + ROW} L${x2} ${y2}`;
            return <path key={r.id + p} d={d} fill="none" stroke={stroke} strokeWidth={2} />;
          }))}
          {headRow && headIndex !== undefined && (() => {
            const x1 = laneX(workLane), y1 = ROW / 2, x2 = laneX(headRow.lane), y2 = Y(headIndex);
            const d = x1 === x2 ? `M${x1} ${y1} L${x2} ${y2}` : `M${x1} ${y1} L${x1} ${y2 - ROW} C ${x1} ${y2 - ROW * 0.4}, ${x2} ${y2 - ROW * 0.6}, ${x2} ${y2}`;
            return <path d={d} fill="none" stroke={workStroke} strokeWidth={2} strokeDasharray="4 3" />;
          })()}
          <circle cx={laneX(workLane)} cy={ROW / 2} r={5} fill="hsl(var(--background))" stroke={workIsAgent ? 'hsl(var(--agent))' : 'hsl(var(--foreground))'} strokeWidth={2} strokeDasharray="3 2" />
          {rows.map((r, i) => {
            const x = laneX(r.lane), y = Y(i);
            if (r.isAgent) return <rect key={r.id} x={x - 5} y={y - 5} width={10} height={10} transform={`rotate(45 ${x} ${y})`} fill="hsl(var(--agent))" stroke="hsl(var(--background))" strokeWidth={2} />;
            if (r.isMerge) return <circle key={r.id} cx={x} cy={y} r={6} fill="hsl(var(--background))" stroke="hsl(var(--foreground))" strokeWidth={2.5} />;
            return <circle key={r.id} cx={x} cy={y} r={5} fill="hsl(var(--foreground))" stroke="hsl(var(--background))" strokeWidth={2} />;
          })}
        </svg>

        <button
          type="button"
          aria-pressed={selected === WORKING}
          onClick={() => engine.select(WORKING)}
          style={{ height: ROW, paddingLeft: Math.max(COL, laneX(lanes) + 8) }}
          className={cn('grid w-full grid-cols-1 content-center gap-0.5 rounded-md pr-2 text-left hover:bg-accent focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring', selected === WORKING && 'bg-muted')}
        >
          <span className="flex min-w-0 items-center gap-1.5">
            <Badge variant="outline" className="shrink-0 border-dashed px-1.5 py-0 font-mono text-[10px] font-medium">{sync.branch}</Badge>
            <span className={cn('truncate text-base font-medium', !localChanges.length && 'text-muted-foreground')}>
              {localChanges.length ? 'Uncommitted changes' : 'No changes yet'}
            </span>
          </span>
          <span className="flex min-w-0 items-center gap-1.5 text-sm text-muted-foreground">
            <span className="shrink-0">You are here</span>
            {localChanges.length > 0 && <><span>·</span><span className="shrink-0">not pushed</span><Counts {...work} /></>}
          </span>
        </button>

        {rows.map((r) => (
          <button
            key={r.id}
            type="button"
            aria-pressed={r.id === selected}
            onClick={() => engine.select(r.id)}
            style={{ height: ROW, paddingLeft: Math.max(COL, laneX(lanes) + 8) }}
            className={cn('grid w-full grid-cols-1 content-center gap-0.5 rounded-md pr-2 text-left hover:bg-accent focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
              r.id === selected && 'bg-muted', fresh.includes(r.id) && 'animate-flash')}
          >
            <span className="flex min-w-0 items-center gap-1.5">
              {r.branches.map((b) => (
                <Badge key={b} variant={b === sync.branch ? 'default' : 'secondary'}
                  className={cn('shrink-0 px-1.5 py-0 font-mono text-[10px] font-medium', b.startsWith('agent/') && b !== sync.branch && 'bg-agent-soft text-agent')}>
                  {running.has(b) && '● '}{b}
                </Badge>
              ))}
              {r.tags.map((t) => <Badge key={t} variant="outline" className="shrink-0 border-dashed px-1.5 py-0 font-mono text-[10px] font-medium">{t}</Badge>)}
              <span className="truncate text-base font-medium">{r.message}</span>
            </span>
            <span className="flex min-w-0 items-center gap-1.5 overflow-hidden text-sm text-muted-foreground">
              {r.isAgent ? <span className="inline-flex shrink-0 items-center gap-1 text-agent"><AgentIcon />{r.author}</span> : <span className="shrink-0">{r.author}</span>}
              <span>·</span><span className="shrink-0">{r.time}</span>
              {r.counts && <Counts {...r.counts} />}
              {r.tokens !== undefined && <TokenChip tokens={r.tokens} />}
              {r.check && (
                <Tooltip>
                  <TooltipTrigger asChild>
                    <span className={r.check.ok ? 'text-added' : 'text-deleted'}>{r.check.ok ? <Check className="size-3.5" aria-label="check passed" /> : <X className="size-3.5" aria-label="check failed" />}</span>
                  </TooltipTrigger>
                  <TooltipContent>{r.check.text}</TooltipContent>
                </Tooltip>
              )}
            </span>
          </button>
        ))}
      </div>
    </section>
  );
}
