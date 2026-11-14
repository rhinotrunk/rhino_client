import { useRef, useState } from 'react';
import { GitBranch, Plus, Sparkles } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Popover, PopoverAnchor, PopoverContent } from '@/components/ui/popover';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Textarea } from '@/components/ui/textarea';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { formatTokens, shortHash } from '@/engine/map';
import { AgentIcon } from './parts';
import { useEngine, useTrunk } from './store';

const MODELS = [
  { id: 'claude-opus-5-5', label: 'Opus 5.5' },
  { id: 'claude-sonnet-5-5', label: 'Sonnet 5.5' },
  { id: 'claude-haiku-5-5', label: 'Haiku 5.5' },
];
const BUDGETS = [25_000, 50_000, 100_000, 250_000];

const slug = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').split('-').slice(0, 4).join('-');

export function NewBranch() {
  const engine = useEngine();
  const { sync, branches } = useTrunk();
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLSpanElement>(null);
  const [tab, setTab] = useState<'human' | 'agent'>('agent');
  const [name, setName] = useState('');
  const [prompt, setPrompt] = useState('Add 150 mm aluminium fins to the west facade at 600 mm spacing');
  const [model, setModel] = useState('claude-sonnet-5-5');
  const [budget, setBudget] = useState('100000');

  const agentName = `agent/${slug(name) || slug(prompt) || 'study'}`;
  const humanName = name.trim();
  const taken = (n: string) => branches.some((b) => b.name === n);
  const humanError = humanName && taken(humanName) ? 'A branch with this name exists.' : humanName.startsWith('agent/') ? 'Names starting with agent/ are for AI branches.' : '';
  const agentError = taken(agentName) ? 'An agent branch with this name exists.' : '';

  const createHuman = () => {
    if (!humanName || humanError) return;
    engine.createBranch(humanName);
    toast.success(`Switched to ${humanName}`, { description: `New branch from ${sync.branch}` });
    setOpen(false); setName('');
  };
  const startAgent = () => {
    if (!prompt.trim() || agentError) return;
    const created = engine.startAgent({ name: agentName, prompt: prompt.trim(), model, budget: Number(budget) });
    if (created) toast(`Agent started on ${created}`, { description: 'You can keep working. Token use shows live under AI agents.' });
    setOpen(false); setName('');
  };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      {/* Anchor + manual toggle: a PopoverTrigger nested in a TooltipTrigger swallows the click. */}
      <PopoverAnchor asChild>
        <span ref={anchor} className="inline-flex">
          <Tooltip>
            <TooltipTrigger asChild>
              <Button variant="ghost" size="icon" className="size-8" aria-label="New branch" aria-expanded={open} aria-haspopup="dialog" onClick={() => setOpen((o) => !o)}><Plus /></Button>
            </TooltipTrigger>
            <TooltipContent>New branch</TooltipContent>
          </Tooltip>
        </span>
      </PopoverAnchor>
      <PopoverContent align="end" className="w-80 space-y-3"
        onPointerDownOutside={(e) => { if (anchor.current?.contains(e.target as Node)) e.preventDefault(); }}>
        <div className="space-y-0.5">
          <h3 className="text-base font-semibold">New branch</h3>
          <p className="text-sm text-muted-foreground">From {sync.branch} · {sync.head ? shortHash(sync.head) : '—'}</p>
        </div>
        <Tabs value={tab} onValueChange={(v) => setTab(v as 'human' | 'agent')}>
          <TabsList className="grid w-full grid-cols-2">
            <TabsTrigger value="human" className="gap-1.5"><GitBranch className="size-3.5" />Human</TabsTrigger>
            <TabsTrigger value="agent" className="gap-1.5"><AgentIcon className="text-current" />AI agent</TabsTrigger>
          </TabsList>

          <TabsContent value="human" className="space-y-3">
            <Field label="Branch name" htmlFor="human-name" error={humanError}>
              <Input id="human-name" placeholder="west-core-study" value={name} onChange={(e) => setName(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && createHuman()} autoFocus />
            </Field>
            <p className="text-sm text-muted-foreground">You'll switch to the new branch. Push sends your changes there instead of {sync.branch}.</p>
            <div className="flex justify-end"><Button size="sm" disabled={!humanName || !!humanError} onClick={createHuman}><GitBranch />Create branch</Button></div>
          </TabsContent>

          <TabsContent value="agent" className="space-y-3">
            <Field label="Prompt" htmlFor="agent-prompt">
              <Textarea id="agent-prompt" rows={3} className="resize-none" placeholder="Describe the geometry to generate…" value={prompt} onChange={(e) => setPrompt(e.target.value)} />
            </Field>
            <Field label="Branch" htmlFor="agent-name" error={agentError}>
              <div className="flex items-center rounded-md border border-input shadow-sm focus-within:ring-1 focus-within:ring-ring">
                <span className="pl-2.5 font-mono text-[12px] text-muted-foreground">agent/</span>
                <Input id="agent-name" className="border-0 pl-0.5 font-mono text-[12px] shadow-none focus-visible:ring-0" placeholder={slug(prompt) || 'study'} value={name} onChange={(e) => setName(e.target.value)} />
              </div>
            </Field>
            <div className="grid grid-cols-2 gap-2">
              <Field label="Model" htmlFor="agent-model">
                <Select value={model} onValueChange={setModel}>
                  <SelectTrigger id="agent-model" className="h-8"><SelectValue /></SelectTrigger>
                  <SelectContent>{MODELS.map((m) => <SelectItem key={m.id} value={m.id}>{m.label}</SelectItem>)}</SelectContent>
                </Select>
              </Field>
              <Field label="Token budget" htmlFor="agent-budget">
                <Select value={budget} onValueChange={setBudget}>
                  <SelectTrigger id="agent-budget" className="h-8"><SelectValue /></SelectTrigger>
                  <SelectContent>{BUDGETS.map((b) => <SelectItem key={b} value={String(b)}>{formatTokens(b)}</SelectItem>)}</SelectContent>
                </Select>
              </Field>
            </div>
            <p className="text-sm text-muted-foreground">The agent commits to its own branch. Nothing reaches {sync.branch} until you merge it.</p>
            <div className="flex justify-end"><Button size="sm" disabled={!prompt.trim() || !!agentError} onClick={startAgent}><Sparkles />Start agent</Button></div>
          </TabsContent>
        </Tabs>
      </PopoverContent>
    </Popover>
  );
}

function Field({ label, htmlFor, error, children }: { label: string; htmlFor: string; error?: string; children: React.ReactNode }) {
  return (
    <div className="space-y-1">
      <label htmlFor={htmlFor} className="text-sm font-medium">{label}</label>
      {children}
      {error && <p className="text-sm text-destructive">{error}</p>}
    </div>
  );
}
