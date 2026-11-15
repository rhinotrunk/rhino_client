import { useEffect, useMemo, useRef, useState } from "react";
import { Bot, Check, GitMerge, GitPullRequest, Loader2, Tag, User, X } from "lucide-react";
import { cn } from "@/lib/utils";
import type { AgentTask, GraphCommit } from "../protocol/events";
import { readyTasks, type Repo } from "../repo";
import { hashOf, layoutLanes, shortHash, type Edge, type LaneRow } from "./lanes";

const ROW = 30;
const LANE = 16;
const PAD = 12;
// Lane colours read on both themes; agent work always uses the violet accent.
const palette = ["#5b8def", "#e0953f", "#3fb58a", "#c26bd1", "#e0605a", "#38b2d6", "#9aa3b2"];
const AGENT = "var(--agent)";
const x = (lane: number) => PAD + lane * LANE;

function curve(edge: Edge, top: number, bottom: number): string {
  const x1 = x(edge.from);
  const x2 = x(edge.to);
  if (x1 === x2) return `M${x1} ${top}V${bottom}`;
  const mid = (top + bottom) / 2;
  return `M${x1} ${top}C${x1} ${mid} ${x2} ${mid} ${x2} ${bottom}`;
}

function ago(timestamp: string): string {
  const seconds = Math.max(0, (Date.now() - new Date(timestamp).getTime()) / 1000);
  if (!Number.isFinite(seconds)) return "";
  const steps: [number, string][] = [[60, "s"], [3600, "m"], [86400, "h"], [604800, "d"], [2629800, "w"], [31557600, "mo"]];
  let divisor = 1;
  for (const [limit, unit] of steps) {
    if (seconds < limit) return `${Math.max(1, Math.floor(seconds / divisor))}${unit}`;
    divisor = limit;
  }
  return `${Math.floor(seconds / 31557600)}y`;
}

type Pill = { name: string; kind: "branch" | "tag" | "agent"; head: boolean };

export function History({ repo, focus }: { repo: Repo; focus: number }) {
  const { history, control, idle, requestedRepo, ready, message } = repo;
  const layout = useMemo(() => layoutLanes(history.commits), [history.commits]);
  const gutter = PAD * 2 + Math.max(1, layout.width - 1) * LANE;
  const [selected, setSelected] = useState("");
  const listRef = useRef<HTMLDivElement>(null);

  const tasks = control?.tasks ?? [];
  const byBranch = useMemo(() => {
    const map = new Map<string, AgentTask>();
    for (const task of tasks) if (task.prBranch && !task.mergedBranch && !task.deleted) map.set(task.prBranch, task);
    return map;
  }, [tasks]);
  const pending = readyTasks(control);

  // Picking a branch in the header brings its tip into view and selects it.
  const branchName = repo.branch.trim();
  useEffect(() => {
    const tip = history.commits.find((commit) => commit.refs?.includes(branchName));
    if (!tip) return;
    setSelected(tip.id);
    listRef.current?.querySelector(`[data-branch="${CSS.escape(branchName)}"]`)?.scrollIntoView({ block: "nearest", behavior: "smooth" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [branchName]);

  useEffect(() => {
    if (focus === 0) return;
    listRef.current?.querySelector("[data-pr]")?.scrollIntoView({ block: "center", behavior: "smooth" });
  }, [focus]);

  const note = historyNote(repo, requestedRepo, ready);
  const headBranch = repo.branch.trim();

  return (
    <div ref={listRef} className="h-full overflow-auto" role="tree" aria-label="History">
      <div className="min-w-max pb-6">
        <WipRow repo={repo} gutter={gutter} next={pending.length === 0 && layout.rows.length === 0} />
        {pending.map((task, index) => (
          <ReadyRow key={task.id} task={task} gutter={gutter} last={index === pending.length - 1 && layout.rows.length === 0} onOpen={() => repo.openPr(task.id)} />
        ))}
        {note ? (
          <p className={cn("px-4 py-3 text-[12px]", history.error ? "text-destructive" : "text-muted-foreground")}>{note}</p>
        ) : (
          layout.rows.map((row) => {
            const pills = pillsFor(row.commit, headBranch, byBranch);
            const pr = row.commit.refs.map((ref) => byBranch.get(ref)).find(Boolean);
            const head = hashOf(row.commit.id) === hashOf(history.head);
            return (
              <Row
                key={row.commit.id}
                row={row}
                gutter={gutter}
                pills={pills}
                pr={pr}
                head={head}
                busy={!idle}
                selected={selected === row.commit.id}
                onSelect={() => setSelected(row.commit.id)}
                onCheckout={() => repo.checkout(row.commit.id, row.commit.refs.find((ref) => pickable(ref)))}
                onPickBranch={(name) => repo.setBranch(name)}
                current={branchName}
                onMerge={() => pr && repo.mergePr(pr.id)}
                onClose={() => pr && repo.closePr(pr.id)}
              />
            );
          })
        )}
        {history.truncated && history.loaded && layout.rows.length > 0 ? (
          <p className="px-4 py-2 text-[11px] text-muted-foreground">Older history is not shown.</p>
        ) : null}
      </div>
      <span className="sr-only">{message}</span>
    </div>
  );
}

function historyNote(repo: Repo, requestedRepo: string, ready: boolean): string | null {
  const { history } = repo;
  if (!ready) return null;
  if (!requestedRepo) return "Choose a repository above to see its history.";
  if (!history.loaded || history.repo !== requestedRepo) return "Loading history…";
  if (history.error) return repo.fresh && repo.fresh === requestedRepo ? "New repository. Describe your changes and push to create it." : history.error;
  if (history.commits.length === 0) return "No commits yet. Push to create the first one.";
  return null;
}

const pickable = (ref: string) => !!ref && !ref.startsWith("tag:") && !ref.startsWith("agent/");

function pillsFor(commit: GraphCommit, branch: string, prs: Map<string, AgentTask>): Pill[] {
  const pills: Pill[] = [];
  for (const ref of commit.refs ?? []) {
    if (ref.startsWith("tag:")) pills.push({ name: ref.slice(4), kind: "tag", head: false });
    else if (ref) pills.push({ name: ref, kind: prs.has(ref) || ref.startsWith("agent/") ? "agent" : "branch", head: ref === branch });
  }
  // The branch checked out is listed first; tags last.
  pills.sort((a, b) => Number(b.head) - Number(a.head) || Number(a.kind === "tag") - Number(b.kind === "tag"));
  return pills;
}

function Gutter({ gutter, children }: { gutter: number; children: React.ReactNode }) {
  return (
    <svg className="shrink-0" width={gutter} height={ROW} viewBox={`0 0 ${gutter} ${ROW}`} aria-hidden="true">
      {children}
    </svg>
  );
}

function Row({
  row, gutter, pills, pr, head, busy, selected, onSelect, onCheckout, onPickBranch, onMerge, onClose,
}: {
  row: LaneRow; gutter: number; pills: Pill[]; pr: AgentTask | undefined; head: boolean; busy: boolean; selected: boolean;
  onSelect: () => void; onCheckout: () => void; onPickBranch: (name: string) => void; current?: string; onMerge: () => void; onClose: () => void;
}) {
  const { commit, lane } = row;
  const agent = commit.author?.kind === "agent";
  const color = palette[row.color % palette.length]!;
  const mid = ROW / 2;
  const first = commit.message.split(/\r?\n/, 1)[0]?.trim() || "(no message)";
  const nodeX = x(lane);
  return (
    <div
      role="treeitem"
      aria-selected={selected}
      tabIndex={0}
      data-pr={pr ? "" : undefined}
      onClick={onSelect}
      onDoubleClick={onCheckout}
      onKeyDown={(event) => {
        if (event.key === "Enter") onCheckout();
        else if (event.key === " ") { event.preventDefault(); onSelect(); }
      }}
      className={cn(
        "group flex items-center pr-3 text-[12px] outline-none",
        "hover:bg-accent/60 focus-visible:bg-accent/60",
        selected && "bg-accent",
      )}
      style={{ height: ROW }}
    >
      <Gutter gutter={gutter}>
        {row.above.map((edge, index) => (
          <path key={`a${index}`} d={curve(edge, 0, edge.through ? ROW : mid)} stroke={palette[edge.color % palette.length]} strokeWidth={2} fill="none" />
        ))}
        {row.below.map((edge, index) => (
          <path key={`b${index}`} d={curve(edge, mid, ROW)} stroke={palette[edge.color % palette.length]} strokeWidth={2} fill="none" />
        ))}
        {head ? <circle cx={nodeX} cy={mid} r={8} fill="none" stroke={color} strokeOpacity={0.45} strokeWidth={1.5} /> : null}
        {row.isMerge ? (
          <circle cx={nodeX} cy={mid} r={4} fill="var(--card)" stroke={color} strokeWidth={2} />
        ) : (
          <circle cx={nodeX} cy={mid} r={agent ? 4.5 : 4} fill={agent ? AGENT : color} stroke="var(--background)" strokeWidth={1.5} />
        )}
      </Gutter>
      <div className="flex min-w-0 items-center gap-1.5">
        {pills.map((pill) => (
          <RefPill key={pill.name} pill={pill} color={color} onPick={() => onPickBranch(pill.name)} />
        ))}
        {pr ? <PrBadge task={pr} onMerge={onMerge} onClose={onClose} /> : null}
        <span className={cn("max-w-[34ch] truncate", head ? "font-medium" : "")} title={commit.message}>{first}</span>
      </div>
      <div className="ml-auto flex shrink-0 items-center gap-2 pl-4 text-[11px] text-muted-foreground">
        <button
          type="button"
          disabled={busy}
          onClick={(event) => { event.stopPropagation(); onCheckout(); }}
          title="Check out this commit"
          aria-label={`Check out ${shortHash(commit.id)}`}
          className="invisible flex h-5 items-center gap-1 rounded px-1.5 text-foreground hover:bg-secondary group-hover:visible group-focus-visible:visible disabled:opacity-40"
        >
          <Check size={12} /> Checkout
        </button>
        <span className="font-mono">{shortHash(commit.id)}</span>
        <span title={commit.author?.name ?? commit.author?.kind} className="flex w-3.5 justify-center">
          {agent ? <Bot size={13} style={{ color: AGENT }} /> : <User size={13} />}
        </span>
        <time className="w-7 text-right" dateTime={commit.timestamp} title={new Date(commit.timestamp).toLocaleString()}>{ago(commit.timestamp)}</time>
      </div>
    </div>
  );
}

function RefPill({ pill, color, onPick }: { pill: Pill; color: string; onPick: () => void }) {
  const tint = pill.kind === "agent" ? AGENT : pill.kind === "tag" ? "var(--muted-foreground)" : color;
  const pickable = pill.kind === "branch";
  return (
    <span
      data-branch={pill.kind === "tag" ? undefined : pill.name}
      role={pickable ? "button" : undefined}
      onClick={pickable ? (event) => { event.stopPropagation(); onPick(); } : undefined}
      className={cn(
        "inline-flex h-[18px] max-w-[26ch] shrink-0 items-center gap-1 rounded-[5px] border px-1.5 text-[11px] leading-none",
        pill.head ? "font-semibold" : "font-normal",
        pickable && "cursor-pointer hover:brightness-125",
      )}
      style={{
        color: tint,
        borderColor: `color-mix(in srgb, ${tint} ${pill.head ? 80 : 45}%, transparent)`,
        background: pill.head ? `color-mix(in srgb, ${tint} 16%, transparent)` : "transparent",
      }}
      title={pickable ? `Switch to ${pill.name}` : pill.name}
    >
      {pill.kind === "tag" ? <Tag size={10} /> : pill.kind === "agent" ? <Bot size={10} /> : null}
      <span className="truncate">{pill.name.replace(/^agent\//, "")}</span>
    </span>
  );
}

// An open pull request: the agent branch tip waiting for approval, with its decisions right on the row.
function PrBadge({ task, onMerge, onClose }: { task: AgentTask; onMerge: () => void; onClose: () => void }) {
  return (
    <span
      className="inline-flex h-[28px] shrink-0 items-center gap-1 rounded-[7px] border pl-2 text-[12px] leading-none"
      style={{ color: AGENT, borderColor: `color-mix(in srgb, ${AGENT} 55%, transparent)`, background: `color-mix(in srgb, ${AGENT} 12%, transparent)` }}
    >
      <GitPullRequest size={14} />
      <span className="pr-1">Awaiting approval</span>
      {task.merging ? (
        <Loader2 size={15} className="mx-1.5 animate-spin" />
      ) : (
        <>
          <IconAction label={`Merge into ${task.prBase ?? "main"}`} onClick={onMerge}><GitMerge size={16} /></IconAction>
          <IconAction label="Close pull request" onClick={onClose}><X size={15} /></IconAction>
        </>
      )}
    </span>
  );
}

function IconAction({ label, onClick, children }: { label: string; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      onClick={(event) => { event.stopPropagation(); onClick(); }}
      onDoubleClick={(event) => event.stopPropagation()}
      className="flex h-[24px] w-[24px] items-center justify-center rounded-md hover:bg-foreground/15"
    >
      {children}
    </button>
  );
}

// The line above the first commit: staged work waiting for a message, like GitKraken's WIP row.
function WipRow({ repo, gutter, next }: { repo: Repo; gutter: number; next: boolean }) {
  const mid = ROW / 2;
  return (
    <div className="flex items-center pr-3" style={{ height: ROW }}>
      <Gutter gutter={gutter}>
        {!next ? <path d={`M${x(0)} ${mid}V${ROW}`} stroke="var(--border)" strokeWidth={2} strokeDasharray="3 3" fill="none" /> : null}
        <circle cx={x(0)} cy={mid} r={4} fill="var(--background)" stroke="var(--muted-foreground)" strokeWidth={1.5} strokeDasharray="2.5 2" />
      </Gutter>
      <input
        value={repo.message}
        onChange={(event) => repo.setMessage(event.target.value)}
        onKeyDown={(event) => { if (event.key === "Enter" && repo.idle) repo.push(); }}
        placeholder="Describe your changes, then push"
        aria-label="Commit message"
        spellCheck={false}
        className="h-6 w-[30ch] min-w-0 rounded-[5px] border border-dashed bg-transparent px-2 text-[12px] outline-none placeholder:text-muted-foreground focus:border-solid focus:border-ring"
      />
    </div>
  );
}

// A finished agent task with no pull request yet; sits above history like staged work.
function ReadyRow({ task, gutter, last, onOpen }: { task: AgentTask; gutter: number; last: boolean; onOpen: () => void }) {
  const mid = ROW / 2;
  return (
    <div className="group flex items-center pr-3 text-[12px] hover:bg-accent/60" style={{ height: ROW }}>
      <Gutter gutter={gutter}>
        <path d={`M${x(0)} 0V${last ? mid : ROW}`} stroke="var(--border)" strokeWidth={2} strokeDasharray="3 3" fill="none" />
        <circle cx={x(0)} cy={mid} r={4.5} fill="var(--background)" stroke={AGENT} strokeWidth={1.5} strokeDasharray="2.5 2" />
      </Gutter>
      <Bot size={12} style={{ color: AGENT }} className="mr-1.5 shrink-0" />
      <span className="max-w-[34ch] truncate" title={task.title}>{task.title}</span>
      <button
        type="button"
        disabled={task.opening}
        onClick={onOpen}
        className="ml-2 inline-flex h-[26px] shrink-0 items-center gap-1.5 rounded-[7px] border px-2.5 text-[12px] font-medium leading-none hover:bg-foreground/10 disabled:opacity-50"
        style={{ color: AGENT, borderColor: `color-mix(in srgb, ${AGENT} 55%, transparent)` }}
        title="Commit this result to its own branch and open a pull request"
      >
        {task.opening ? <Loader2 size={14} className="animate-spin" /> : <GitPullRequest size={14} />}
        Open PR
      </button>
    </div>
  );
}
