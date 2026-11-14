import { useState } from 'react';
import { ArrowDown, ArrowUp, CircleAlert, PencilLine, RefreshCw, Users } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Textarea } from '@/components/ui/textarea';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { describeError, shortHash, syncLine, type DiffEntryWithInfo } from '@/engine/map';
import { BranchPicker } from './BranchPicker';
import { NewBranch } from './NewBranch';
import { OpLetter } from './parts';
import { useEngine, useNow, useTrunk } from './store';

export function Header() {
  const engine = useEngine();
  const { sync, localChanges } = useTrunk();
  const now = useNow(true, 30_000);
  const [pushOpen, setPushOpen] = useState(false);
  const [blocked, setBlocked] = useState(false);
  const [message, setMessage] = useState('');
  // Pull first when behind: highlight Pull so the 409 never happens.
  const pullFirst = sync.behind > 0;

  const pull = () => {
    const { count, names } = engine.pull();
    setBlocked(false);
    if (count) toast.success(`Pulled ${count} commit${count === 1 ? '' : 's'}`, { description: `${listNames(names)} updated in the document` });
  };
  const openPush = (open: boolean) => {
    if (open) setMessage(suggestMessage(localChanges));
    setPushOpen(open);
    if (!open) setBlocked(false);
  };
  const fetch = () => {
    const behind = engine.fetch();
    toast(behind ? `${sync.branch} has ${behind} new commits` : 'Up to date');
  };
  const push = () => {
    const result = engine.push(message);
    if (!result.ok) {
      if (result.code === 'ref_conflict') setBlocked(true);
      else toast.error(describeError({ code: result.code, message: '' }).title);
      return;
    }
    setPushOpen(false);
    toast.success(`Pushed to ${sync.branch}`, { description: `${result.count} objects committed · ${shortHash(result.id)}` });
  };
  const conflict = describeError({ code: 'ref_conflict', message: '' });

  return (
    <header className="space-y-2 border-b p-3">
      <div className="flex items-center justify-between gap-2">
        <div className="min-w-0 leading-tight">
          <div className="text-base font-semibold">{sync.repo}</div>
          <div className="flex items-center gap-1.5 text-sm text-muted-foreground">
            <span className={`size-1.5 rounded-full ${sync.connected ? 'bg-added' : 'bg-destructive'}`} aria-hidden="true" />
            {sync.server}
          </div>
        </div>
        <div className="flex items-center gap-1">
          <BranchPicker />
          <NewBranch />
        </div>
      </div>

      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-1.5">
          <Button variant={pullFirst ? 'default' : 'outline'} size="sm" disabled={!sync.behind} onClick={pull}>
            <ArrowDown />Pull{sync.behind > 0 && <Count inverted={pullFirst}>{sync.behind}</Count>}
          </Button>
          <Popover open={pushOpen} onOpenChange={openPush}>
            <PopoverTrigger asChild>
              <Button variant={pullFirst ? 'outline' : 'default'} size="sm" disabled={!sync.localChanges}>
                <ArrowUp />Push{sync.localChanges > 0 && <Count inverted={!pullFirst}>{sync.localChanges}</Count>}
              </Button>
            </PopoverTrigger>
            <PopoverContent align="start" className="w-80 space-y-3">
              <div className="space-y-0.5">
                <h3 className="text-base font-semibold">Push to {sync.branch}</h3>
                <p className="text-sm text-muted-foreground">Commits these changes on top of {sync.branch} · {sync.head ? shortHash(sync.head) : '—'}.</p>
              </div>
              {blocked && (
                <div role="alert" className="grid grid-cols-[16px_1fr] gap-x-2 gap-y-1 rounded-md border border-destructive/50 p-2.5 text-sm text-destructive">
                  <CircleAlert className="mt-0.5 size-4" aria-hidden="true" />
                  <b className="font-medium">{conflict.title}</b>
                  <span className="col-start-2 text-foreground/80">{conflict.detail}</span>
                  <Button variant="outline" size="sm" className="col-start-2 mt-1 justify-self-start" onClick={pull}><ArrowDown />Pull now</Button>
                </div>
              )}
              <ul className="space-y-1">
                {localChanges.map((c) => (
                  <li key={c.id} className="grid grid-cols-[14px_minmax(0,1fr)_auto] items-center gap-2 text-sm">
                    <OpLetter change={c.change} />
                    <span className="truncate font-medium">{c.name}</span>
                    <span className="text-muted-foreground">{c.subtype} · {c.components?.join(', ')}</span>
                  </li>
                ))}
              </ul>
              <div className="space-y-1">
                <label htmlFor="push-message" className="text-sm text-muted-foreground">Message</label>
                <Textarea id="push-message" rows={2} value={message} onChange={(e) => setMessage(e.target.value)} />
              </div>
              <div className="flex justify-end gap-1.5">
                <Button variant="ghost" size="sm" onClick={() => setPushOpen(false)}>Cancel</Button>
                <Button size="sm" onClick={push}>Commit &amp; push</Button>
              </div>
            </PopoverContent>
          </Popover>
        </div>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button variant="ghost" size="icon" className="size-8" aria-label="Fetch from server" onClick={fetch}><RefreshCw /></Button>
          </TooltipTrigger>
          <TooltipContent>Fetch</TooltipContent>
        </Tooltip>
      </div>

      <p className="text-sm text-muted-foreground" role="status">
        {syncLine(sync, new Date(now))}
        {sync.branch !== 'main' && ' · pull works on main'}
      </p>

      {engine.isMock && (
        <div className="flex flex-wrap items-center gap-1 rounded-md border border-dashed px-1.5 py-1 text-xs text-muted-foreground">
          <span className="px-1 font-medium">Demo</span>
          <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => toast(`Edited ${engine.simulateEdit()}`, { description: 'Push is ready.' })}>
            <PencilLine className="size-3" />Simulate my edit
          </Button>
          <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => toast(`jlee pushed “${engine.simulateTeammatePush()}”`, { description: sync.branch === 'main' ? 'Pull is ready.' : 'Switch to main to pull it.' })}>
            <Users className="size-3" />Simulate teammate push
          </Button>
        </div>
      )}
    </header>
  );
}

function Count({ children, inverted }: { children: React.ReactNode; inverted?: boolean }) {
  return (
    <span className={`ml-0.5 min-w-4 rounded-full px-1 font-mono text-xs tabular-nums ${inverted ? 'bg-primary-foreground text-primary' : 'bg-muted text-foreground'}`}>
      {children}
    </span>
  );
}

function listNames(names: string[]) {
  if (names.length <= 2) return names.join(' and ');
  return `${names.slice(0, 2).join(', ')} and ${names.length - 2} more`;
}

/** "Edit Column C2, Level 03 slab and 1 more" */
function suggestMessage(changes: DiffEntryWithInfo[]) {
  if (!changes.length) return '';
  const verb = changes.every((c) => c.change === 'added') ? 'Add' : changes.every((c) => c.change === 'deleted') ? 'Delete' : 'Edit';
  return `${verb} ${listNames(changes.map((c) => c.name ?? c.id))}`;
}
