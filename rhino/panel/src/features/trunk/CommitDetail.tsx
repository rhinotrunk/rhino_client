import { useState } from 'react';
import { Copy } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Separator } from '@/components/ui/separator';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { AGENT_META, countChanges, formatTokens, shortHash, toObjectChanges, tokensFromMeta, type ObjectChangeRow } from '@/engine/map';
import { WORKING } from '@/engine/mock';
import { AgentIcon, OpLetter } from './parts';
import { useTrunk } from './store';

const CHANGE = { A: 'added', M: 'modified', D: 'deleted' } as const;

export function CommitDetail() {
  const { commits, diffs, checks, meta, selected, localChanges, sync } = useTrunk();
  const [mode, setMode] = useState<'list' | 'text'>('list');
  if (selected === WORKING) return <WorkingDetail branch={sync.branch} rows={toObjectChanges({ changes: localChanges })} />;
  const commit = commits.find((c) => c.id === selected);
  if (!commit) return null;

  const rows = toObjectChanges({ changes: diffs[commit.id] ?? [] });
  const n = countChanges(rows), total = rows.length || 1;
  const m = meta[commit.id];
  const tokens = tokensFromMeta(m);
  const check = (checks[commit.id] ?? [])[0];
  const parent = commit.parents[0] ? `${shortHash(commit.parents[0])} → ${shortHash(commit.id)}${commit.parents.length > 1 ? ' (first parent)' : ''}` : 'initial commit';

  const text = [
    `commit ${shortHash(commit.id)}${commit.refs.length ? `  (${commit.refs.join(', ')})` : ''}`,
    `Author: ${commit.author.name ?? commit.author.id} <${commit.author.kind}>   ${new Date(commit.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`,
    ...(tokens ? [`Tokens: ${tokens.total.toLocaleString()} (in ${tokens.input.toLocaleString()} / out ${tokens.output.toLocaleString()}) · ${m?.[AGENT_META.model]}`] : []),
    ...(check ? [`Check:  ${check.namespace} ${check.status === 'success' ? '✓' : '✕'} ${check.summary ?? ''}`] : []),
    '',
    ...rows.map((r) => `  ${r.op}  ${r.subtype.padEnd(12)}${r.name.padEnd(15)}${r.components.length ? `[${r.components.join(' ')}]` : ''}`
      + r.fields.map((f) => `\n       − ${f.field}  ${f.before}\n       + ${f.field}  ${f.after}`).join('')),
    '',
    `${n.added} added · ${n.modified} modified · ${n.deleted} deleted`,
  ].join('\n');

  const copy = async () => {
    try { await navigator.clipboard.writeText(text); toast('Copied commit text'); }
    catch { toast.error('Could not copy', { description: 'Select the text and press Ctrl+C.' }); }
  };

  return (
    <section aria-label="Commit details" className="m-3 space-y-3 rounded-lg border bg-card p-3">
      <div className="space-y-0.5">
        <h3 className="text-base font-semibold leading-snug">{commit.message}</h3>
        <p className="font-mono text-xs text-muted-foreground">{parent}</p>
        {commit.author.kind === 'agent' && (
          <div className="space-y-0.5 pt-1 text-sm">
            <p className="flex items-center gap-1.5 text-agent">
              <AgentIcon />{m?.[AGENT_META.model] ?? 'agent'}
              {tokens && <span className="font-mono tabular-nums">· {formatTokens(tokens.total)} tokens (in {formatTokens(tokens.input)} / out {formatTokens(tokens.output)})</span>}
            </p>
            {m?.[AGENT_META.prompt] && <p className="line-clamp-2 text-muted-foreground">“{m[AGENT_META.prompt]}”</p>}
          </div>
        )}
      </div>

      <div className="flex h-1.5 gap-0.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
        {n.added > 0 && <i className="block bg-added" style={{ flex: n.added / total }} />}
        {n.modified > 0 && <i className="block bg-modified" style={{ flex: n.modified / total }} />}
        {n.deleted > 0 && <i className="block bg-deleted" style={{ flex: n.deleted / total }} />}
      </div>

      <div className="flex flex-wrap items-center justify-between gap-2">
        <Tabs value={mode} onValueChange={(v) => setMode(v as 'list' | 'text')}>
          <TabsList className="h-8">
            <TabsTrigger value="list" className="px-3 text-sm">List</TabsTrigger>
            <TabsTrigger value="text" className="px-3 text-sm">Text</TabsTrigger>
          </TabsList>
        </Tabs>
        <div className="flex gap-1.5">
          {mode === 'text' && <Button variant="ghost" size="sm" onClick={copy}><Copy />Copy</Button>}
          <Button variant="outline" size="sm" onClick={() => toast(`Check out ${shortHash(commit.id)}`, { description: 'Will load this commit into a new document once the plugin is wired.' })}>Check out</Button>
        </div>
      </div>
      <Separator />

      {mode === 'list' ? (
        <ul className="space-y-0.5">{rows.map((r) => <ChangeRow key={r.id} row={r} />)}</ul>
      ) : (
        <pre className="overflow-x-auto whitespace-pre rounded-md bg-muted p-3 font-mono text-xs leading-relaxed">{text}</pre>
      )}
    </section>
  );
}

function ChangeRow({ row }: { row: ObjectChangeRow }) {
  return (
    <li className="grid grid-cols-[14px_minmax(0,1fr)_auto] items-center gap-x-2.5 rounded-md px-1.5 py-1.5 hover:bg-accent">
      <OpLetter change={CHANGE[row.op]} />
      <span className="min-w-0">
        <span className="block truncate text-base font-medium">{row.name}</span>
        <span className="block text-sm text-muted-foreground">{row.subtype}</span>
      </span>
      <span className="flex flex-wrap justify-end gap-1">
        {row.components.map((c) => <span key={c} className="rounded-sm border px-1 font-mono text-xs text-muted-foreground">{c}</span>)}
      </span>
      {row.fields.length > 0 && (
        <span className="col-start-2 col-end-4 grid font-mono text-xs leading-relaxed">
          {row.fields.map((f) => (
            <span key={f.field}><span className="block text-deleted">− {f.field}  {f.before}</span><span className="block text-added">+ {f.field}  {f.after}</span></span>
          ))}
        </span>
      )}
    </li>
  );
}

function WorkingDetail({ branch, rows }: { branch: string; rows: ObjectChangeRow[] }) {
  return (
    <section aria-label="Uncommitted changes" className="m-3 space-y-3 rounded-lg border border-dashed bg-card p-3">
      <div className="space-y-0.5">
        <h3 className="text-base font-semibold leading-snug">{rows.length ? 'Uncommitted changes' : 'No changes yet'}</h3>
        <p className="text-sm text-muted-foreground">
          {rows.length
            ? `Edits in this Rhino document that aren't on ${branch} yet. Push to commit them.`
            : `You're on ${branch}. Edit the model in Rhino and your changes show up here, ready to push.`}
        </p>
      </div>
      {rows.length > 0 && <><Separator /><ul className="space-y-0.5">{rows.map((r) => <ChangeRow key={r.id} row={r} />)}</ul></>}
    </section>
  );
}
