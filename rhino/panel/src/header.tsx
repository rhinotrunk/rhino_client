import { useEffect, useRef, useState } from "react";
import { ArrowDown, ArrowUp, Bot, ChevronDown, ChevronRight, GitBranch, GitPullRequest, Loader2, RefreshCw, Settings2, Database, Check, Plus } from "lucide-react";
import { cn } from "@/lib/utils";
import { openPrs, type Repo } from "./repo";

export type TabId = "graph" | "agents";

// One sticky header for both tabs: where the model is synced to, the sync actions, and what is waiting on you.
export function Header({
  repo, tab, onTab, onPrs, onSettings,
}: {
  repo: Repo; tab: TabId; onTab: (tab: TabId) => void; onPrs: () => void; onSettings: () => void;
}) {
  const [open, setOpen] = useState<"repo" | "branch" | null>(null);
  const anchor = useRef<HTMLDivElement>(null);
  const prs = openPrs(repo.control);
  const running = (repo.control?.tasks ?? []).filter((task) => ["running", "queued", "stopping"].includes(task.status) && !task.deleted && !task.archived).length;
  const disabled = !repo.idle;
  const connected = repo.repo.trim() !== "";

  useEffect(() => {
    if (!open) return;
    const away = (event: PointerEvent) => {
      const target = event.target as Element | null;
      if (anchor.current?.contains(target) || target?.closest("[role=listbox]")) return;
      setOpen(null);
    };
    const escape = (event: KeyboardEvent) => { if (event.key === "Escape") setOpen(null); };
    document.addEventListener("pointerdown", away);
    document.addEventListener("keydown", escape);
    return () => { document.removeEventListener("pointerdown", away); document.removeEventListener("keydown", escape); };
  }, [open]);

  return (
    <header className="relative z-20 shrink-0 border-b bg-card">
      <div className="flex h-16 items-center gap-1 px-2.5">
        <div ref={anchor} className="relative flex min-w-0 items-center gap-0.5">
          <div className="relative min-w-0">
          <Picker label="Repository" value={connected ? repo.repo : "Select"} icon={<Database size={18} />} active={open === "repo"} muted={!connected}
            onClick={() => setOpen(open === "repo" ? null : "repo")} title={repo.server} />
                    {open === "repo" ? (
            <ListMenu
              label="Repository"
              value={repo.repo}
              options={repo.repos.server === repo.server.trim() ? repo.repos.options : []}
              note={repo.repos.server === repo.server.trim() ? repo.repos.note : ""}
              loading={repo.repos.loading}
              empty="No repositories on this server."
              onOpen={() => repo.requestRepos()}
              onChoose={(name) => { repo.chooseRepo(name); setOpen(null); }}
              onCreate={(name) => { repo.chooseRepo(name, true); setOpen(null); }}
              sanitize={(name) => name.trim().toLowerCase().replace(/[^a-z0-9._-]+/g, "-").replace(/^[^a-z0-9]+/, "")}
              hint="Lowercase letters, digits, . _ -. The repository is created on your first push."
            />
          ) : null}
          </div>
          <ChevronRight size={16} className="shrink-0 text-muted-foreground/60" />
          <div className="relative min-w-0">
          <Picker label="Branch" value={repo.branch || "main"} icon={<GitBranch size={18} />} active={open === "branch"} muted={!connected}
            onClick={() => setOpen(open === "branch" ? null : "branch")} mono />
          {open === "branch" ? (
            <ListMenu
              label="Branch"
              value={repo.branch}
              options={repo.branches.server === repo.server.trim() && repo.branches.repo === repo.repo.trim() ? repo.branches.options : []}
              note={repo.branches.server === repo.server.trim() && repo.branches.repo === repo.repo.trim() ? repo.branches.note : ""}
              loading={repo.branches.loading}
              empty="No branches yet."
             
              onOpen={() => repo.requestBranches()}
              onChoose={(name) => { repo.setBranch(name); setOpen(null); }}
              onCreate={(name) => { repo.setBranch(name); setOpen(null); }}
              hint="Starts from the checked-out commit and is created on your next push."
            />
          ) : null}
          </div>
        </div>

        <div className="ml-auto flex items-center gap-1">
          <Action label="Pull" icon={<ArrowDown size={16} />} disabled={disabled} onClick={repo.pull} text />
          <Action label="Push" icon={<ArrowUp size={16} />} disabled={disabled} onClick={repo.push} text primary />
          <Action label="Refresh history" icon={<RefreshCw size={16} className={cn(repo.busy && "animate-spin")} />} disabled={disabled} onClick={repo.refresh} />
          <span className="mx-1 h-4 w-px bg-border" />
          <Action
            label={prs.length ? `${prs.length} open pull request${prs.length === 1 ? "" : "s"}` : "No open pull requests"}
            icon={<GitPullRequest size={17} />}
            disabled={prs.length === 0}
            onClick={onPrs}
            badge={prs.length}
          />
          <Action label="Worker settings" icon={<Settings2 size={17} />} onClick={onSettings} />
        </div>
      </div>

      <div className="flex h-9 items-end gap-1 px-2.5">
        <div role="tablist" aria-label="RhinoClient" className="flex gap-0.5">
          <TabButton id="graph" tab={tab} onTab={onTab} icon={<GitBranch size={13} />}>Graph</TabButton>
          <TabButton id="agents" tab={tab} onTab={onTab} icon={<Bot size={13} />} badge={running}>Agents</TabButton>
        </div>
        <p
          role="status"
          title={repo.status}
          className={cn("mb-2 ml-auto flex min-w-0 items-center gap-1.5 truncate text-[12px]", repo.error ? "text-destructive" : "text-muted-foreground")}
        >
          {repo.busy ? <Loader2 size={11} className="shrink-0 animate-spin" /> : null}
          <span className="truncate">{repo.status}</span>
        </p>
      </div>
    </header>
  );
}

function TabButton({
  id, tab, onTab, icon, badge, children,
}: {
  id: TabId; tab: TabId; onTab: (tab: TabId) => void; icon: React.ReactNode; badge?: number; children: React.ReactNode;
}) {
  const active = tab === id;
  return (
    <button
      type="button"
      role="tab"
      id={`tab-${id}`}
      aria-selected={active}
      aria-controls={`pane-${id}`}
      onClick={() => onTab(id)}
      className={cn(
        "-mb-px flex h-8 items-center gap-1.5 border-b-2 px-3 text-[13px]",
        active ? (id === "agents" ? "border-[var(--agent)]" : "border-primary") + " font-medium text-foreground" : "border-transparent text-muted-foreground hover:text-foreground",
      )}
    >
      {icon}
      {children}
      {badge ? <span className="rounded-full px-1.5 text-[10px] leading-4 text-white" style={{ background: "var(--agent)" }}>{badge}</span> : null}
    </button>
  );
}

function Action({
  label, icon, onClick, disabled, text, primary, badge,
}: {
  label: string; icon: React.ReactNode; onClick: () => void; disabled?: boolean; text?: boolean; primary?: boolean; badge?: number;
}) {
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
      className={cn(
        "relative flex h-9 items-center justify-center gap-1.5 rounded-lg text-[13px] font-medium disabled:opacity-40",
        text ? "px-3" : "w-9",
        primary ? "bg-primary text-primary-foreground hover:bg-primary/90" : "hover:bg-accent",
      )}
    >
      {icon}
      {text ? label : null}
      {badge ? (
        <span className="absolute -right-1 -top-1 min-w-4 rounded-full px-1 text-center text-[9px] font-semibold leading-[14px] text-white" style={{ background: "var(--agent)" }}>
          {badge}
        </span>
      ) : null}
    </button>
  );
}

// GitKraken-style selector: a small caption over a large name.
function Picker({
  label, value, icon, active, muted, mono, title, onClick,
}: {
  label: string; value: string; icon: React.ReactNode; active: boolean; muted?: boolean; mono?: boolean; title?: string; onClick: () => void;
}) {
  return (
    <button
      type="button"
      aria-expanded={active}
      aria-haspopup="dialog"
      title={title ?? value}
      onClick={onClick}
      className={cn("flex h-12 min-w-0 max-w-[18rem] items-center gap-2.5 rounded-lg border border-transparent px-2.5 text-left hover:border-border hover:bg-accent", active && "bg-accent")}
    >
      <span className="shrink-0 text-muted-foreground">{icon}</span>
      <span className="min-w-0">
        <span className="block text-[10.5px] uppercase leading-3 tracking-wider text-muted-foreground">{label}</span>
        <span className={cn("flex items-center gap-1 text-[18px] font-semibold leading-6", muted && "text-primary", mono && "font-mono text-[16px]")}>
          <span className="truncate">{value}</span>
          <ChevronDown size={15} className="shrink-0 text-muted-foreground" />
        </span>
      </span>
    </button>
  );
}

// Opens straight onto the list: type to filter, click or press Enter to choose. A name that is not listed
// can still be used, which is how a new branch is started.
function ListMenu({
  label, value, options, note, loading, empty, onOpen, onChoose, onCreate, sanitize, hint,
}: {
  onCreate?: (name: string) => void;
  /** Turns what was typed into a valid name; the hint under the field says what it needs. */
  sanitize?: (name: string) => string;
  hint?: string;
  label: string; value: string; options: readonly string[]; note: string; loading: boolean; empty: string; offset?: string;
  onOpen: () => void; onChoose: (name: string) => void;
}) {
  const [filter, setFilter] = useState("");
  const [active, setActive] = useState(0);
  const [creating, setCreating] = useState(false);
  useEffect(onOpen, []);
  const create = () => {
    const name = sanitize ? sanitize(filter) : filter.trim().replace(/\s+/g, "-");
    if (name && onCreate) onCreate(name);
  };
  const query = filter.trim().toLowerCase();
  const shown = query ? options.filter((name) => name.toLowerCase().includes(query)) : options;
  const custom = filter.trim() && !options.includes(filter.trim()) ? filter.trim() : "";
  const total = shown.length + (custom ? 1 : 0);
  const pick = (index: number) => {
    if (index < shown.length) onChoose(shown[index]!);
    else if (custom) onChoose(custom);
  };
  return (
    <div role="dialog" aria-label={label} className="absolute left-0 top-14 z-30 w-72 rounded-lg border bg-card p-1.5 shadow-lg">
      <div className="mb-1 flex gap-1">
      <input
        autoFocus
        value={filter}
        placeholder={creating ? `New ${label.toLowerCase()} name` : `Find ${label.toLowerCase()}…`}
        spellCheck={false}
        autoComplete="off"
        onChange={(event) => { setFilter(event.target.value); setActive(0); }}
        onKeyDown={(event) => {
          if (event.key === "ArrowDown") { event.preventDefault(); setActive((i) => (total ? (i + 1) % total : 0)); }
          else if (event.key === "ArrowUp") { event.preventDefault(); setActive((i) => (total ? (i - 1 + total) % total : 0)); }
          else if (event.key === "Enter") { event.preventDefault(); if (creating) create(); else pick(active); }
          else if (event.key === "Escape" && creating) { event.stopPropagation(); setCreating(false); }
        }}
        className="h-8 min-w-0 flex-1 rounded-md border bg-background px-2.5 text-[13px] outline-none focus:border-ring"
      />
      {onCreate ? (
        creating ? (
          <button type="button" onClick={create} disabled={!filter.trim()} className="h-8 rounded-md bg-primary px-3 text-[12px] font-medium text-primary-foreground disabled:opacity-40">Create</button>
        ) : (
          <button type="button" title={`New ${label.toLowerCase()}`} aria-label={`New ${label.toLowerCase()}`} onClick={() => { setCreating(true); setFilter(""); }} className="flex h-8 w-8 items-center justify-center rounded-md border hover:bg-accent"><Plus size={16} /></button>
        )
      ) : null}
      </div>
      {creating ? <p className="px-1 pb-1 text-[11px] text-muted-foreground">{hint}</p> : null}
      <ul role="listbox" className={cn("max-h-64 overflow-auto", creating && "hidden")}>
        {shown.map((name, index) => (
          <li key={name}>
            <button
              type="button"
              role="option"
              aria-selected={name === value.trim()}
              onMouseEnter={() => setActive(index)}
              onClick={() => onChoose(name)}
              className={cn("flex h-8 w-full items-center gap-2 rounded-md px-2.5 text-left text-[13px]", index === active && "bg-accent")}
            >
              <span className="truncate">{name}</span>
              {name === value.trim() ? <Check size={14} className="ml-auto shrink-0 text-primary" /> : null}
            </button>
          </li>
        ))}
        {custom ? (
          <li>
            <button type="button" onClick={() => onChoose(custom)} onMouseEnter={() => setActive(shown.length)}
              className={cn("flex h-8 w-full items-center gap-2 rounded-md px-2.5 text-left text-[13px]", active === shown.length && "bg-accent")}>
              <span className="text-muted-foreground">Use</span><span className="truncate font-medium">{custom}</span>
            </button>
          </li>
        ) : null}
        {total === 0 ? (
          <li className={cn("px-2.5 py-2 text-[12px]", note ? "text-destructive" : "text-muted-foreground")}>{loading ? "Loading…" : note || empty}</li>
        ) : loading ? (
          <li className="px-2.5 py-1.5 text-[11px] text-muted-foreground">Loading…</li>
        ) : null}
      </ul>
    </div>
  );
}
