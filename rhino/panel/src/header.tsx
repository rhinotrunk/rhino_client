import { useEffect, useRef, useState } from "react";
import { ArrowDown, ArrowUp, Bot, ChevronDown, ChevronRight, GitBranch, GitPullRequest, Loader2, RefreshCw, Settings2, Database } from "lucide-react";
import { cn } from "@/lib/utils";
import { Field, SuggestField } from "./fields";
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
      <div className="flex h-14 items-center gap-1 px-2">
        <div ref={anchor} className="relative flex min-w-0 items-center gap-0.5">
          <Picker label="Repository" value={connected ? repo.repo : "Select"} icon={<Database size={15} />} active={open === "repo"} muted={!connected}
            onClick={() => setOpen(open === "repo" ? null : "repo")} title={repo.server} />
          <ChevronRight size={14} className="shrink-0 text-muted-foreground/60" />
          <Picker label="Branch" value={repo.branch || "main"} icon={<GitBranch size={15} />} active={open === "branch"} muted={!connected}
            onClick={() => setOpen(open === "branch" ? null : "branch")} mono />
          {open === "repo" ? (
            <div role="dialog" aria-label="Repository" className="absolute left-0 top-12 z-30 grid w-72 gap-2 rounded-lg border bg-card p-3 shadow-lg">
              <Field label="Server" value={repo.server} onChange={repo.setServer} onBlur={() => repo.remember()} />
              <SuggestField
                label="Repository"
                value={repo.repo}
                onChange={repo.setRepo}
                onCommit={repo.chooseRepo}
                options={repo.repos.server === repo.server.trim() ? repo.repos.options : []}
                note={repo.repos.server === repo.server.trim() ? repo.repos.note : ""}
                loading={repo.repos.loading}
                empty="No repositories yet."
                onOpen={() => repo.requestRepos()}
              />
            </div>
          ) : null}
          {open === "branch" ? (
            <div role="dialog" aria-label="Branch" className="absolute left-0 top-12 z-30 grid w-64 gap-2 rounded-lg border bg-card p-3 shadow-lg">
              <SuggestField
                label="Branch"
                value={repo.branch}
                onChange={repo.setBranch}
                options={repo.branches.server === repo.server.trim() && repo.branches.repo === repo.repo.trim() ? repo.branches.options : []}
                note={repo.branches.server === repo.server.trim() && repo.branches.repo === repo.repo.trim() ? repo.branches.note : ""}
                loading={repo.branches.loading}
                empty="No branches yet."
                onOpen={() => repo.requestBranches()}
              />
            </div>
          ) : null}
        </div>

        <div className="ml-auto flex items-center gap-1">
          <Action label="Pull" icon={<ArrowDown size={14} />} disabled={disabled} onClick={repo.pull} text />
          <Action label="Push" icon={<ArrowUp size={14} />} disabled={disabled} onClick={repo.push} text primary />
          <Action label="Refresh history" icon={<RefreshCw size={13} className={cn(repo.busy && "animate-spin")} />} disabled={disabled} onClick={repo.refresh} />
          <span className="mx-1 h-4 w-px bg-border" />
          <Action
            label={prs.length ? `${prs.length} open pull request${prs.length === 1 ? "" : "s"}` : "No open pull requests"}
            icon={<GitPullRequest size={14} />}
            disabled={prs.length === 0}
            onClick={onPrs}
            badge={prs.length}
          />
          <Action label="Worker settings" icon={<Settings2 size={14} />} onClick={onSettings} />
        </div>
      </div>

      <div className="flex h-8 items-end gap-1 px-2">
        <div role="tablist" aria-label="RhinoClient" className="flex gap-0.5">
          <TabButton id="graph" tab={tab} onTab={onTab} icon={<GitBranch size={13} />}>Graph</TabButton>
          <TabButton id="agents" tab={tab} onTab={onTab} icon={<Bot size={13} />} badge={running}>Agents</TabButton>
        </div>
        <p
          role="status"
          title={repo.status}
          className={cn("mb-1.5 ml-auto flex min-w-0 items-center gap-1.5 truncate text-[11px]", repo.error ? "text-destructive" : "text-muted-foreground")}
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
        "-mb-px flex h-7 items-center gap-1.5 border-b-2 px-2.5 text-[12px]",
        active ? "border-primary font-medium text-foreground" : "border-transparent text-muted-foreground hover:text-foreground",
      )}
    >
      {icon}
      {children}
      {badge ? <span className="rounded-full bg-primary px-1.5 text-[10px] leading-4 text-primary-foreground">{badge}</span> : null}
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
        "relative flex h-7 items-center justify-center gap-1 rounded-md text-[12px] disabled:opacity-40",
        text ? "px-2" : "w-7",
        primary ? "bg-primary text-primary-foreground hover:bg-primary/90" : "hover:bg-accent",
      )}
    >
      {icon}
      {text ? label : null}
      {badge ? (
        <span className="absolute -right-0.5 -top-0.5 min-w-3.5 rounded-full px-1 text-center text-[9px] font-semibold leading-[14px] text-white" style={{ background: "var(--agent)" }}>
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
      className={cn("flex h-11 min-w-0 max-w-[16rem] items-center gap-2 rounded-md px-2 text-left hover:bg-accent", active && "bg-accent")}
    >
      <span className="shrink-0 text-muted-foreground">{icon}</span>
      <span className="min-w-0">
        <span className="block text-[10px] uppercase leading-3 tracking-wide text-muted-foreground">{label}</span>
        <span className={cn("flex items-center gap-1 text-[15px] font-semibold leading-5", muted && "text-primary", mono && "font-mono text-[14px]")}>
          <span className="truncate">{value}</span>
          <ChevronDown size={13} className="shrink-0 text-muted-foreground" />
        </span>
      </span>
    </button>
  );
}
