import { AlertTriangle, GitBranch } from "lucide-react";
import { cn } from "@/lib/utils";
import { shortHash } from "./graph/lanes";
import type { Repo } from "./repo";

// Questions the host asks before it changes the model. They sit under the header so they are never missed.
export function Prompts({ repo }: { repo: Repo }) {
  const { unpushed, detached, conflict } = repo;
  if (!unpushed && !detached && !conflict) return null;
  const branch = repo.branch.trim() || "the branch";
  const button = "h-8 rounded-md px-3 text-[12px] font-medium disabled:opacity-40";
  return (
    <div role="alertdialog" aria-live="assertive" className="shrink-0 border-b bg-card px-3 py-2.5">
      {conflict ? (
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
          <p className="flex min-w-0 flex-1 basis-64 items-start gap-2 text-[13px]">
            <AlertTriangle size={16} className="mt-0.5 shrink-0 text-modified" />
            <span>
              Merging <b className="font-medium">{conflict.title}</b> into <b className="font-medium">{conflict.into}</b> conflicts on {conflict.conflicts === 1 ? "1 object" : `${conflict.conflicts} objects`}: both sides changed the same fields. Which version should win?
            </span>
          </p>
          <div className="flex gap-1.5">
            <button type="button" disabled={!repo.idle} onClick={() => repo.resolveMerge("ours")} className={cn(button, "border hover:bg-accent")} title={`Keep what ${conflict.into} has`}>Keep {conflict.into}</button>
            <button type="button" disabled={!repo.idle} onClick={() => repo.resolveMerge("theirs")} className={cn(button, conflict.id ? "bg-[var(--agent)] text-white hover:brightness-110" : "bg-primary text-primary-foreground hover:bg-primary/90")} title={conflict.id ? "Take the agent's version" : `Take what ${conflict.branch} has`}>{conflict.id ? "Use agent's" : `Use ${conflict.branch}`}</button>
            <button type="button" onClick={repo.cancelMerge} className={cn(button, "hover:bg-accent")}>Cancel</button>
          </div>
        </div>
      ) : unpushed ? (
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
          <p className="flex min-w-0 flex-1 basis-64 items-start gap-2 text-[13px]">
            <AlertTriangle size={16} className="mt-0.5 shrink-0 text-modified" />
            <span>Checking out <b className="font-mono font-medium">{shortHash(unpushed.commit)}</b> replaces the model, which has {unpushed.changes === 1 ? "1 unpushed change" : `${unpushed.changes} unpushed changes`}.</span>
          </p>
          <div className="flex gap-1.5">
            <button type="button" disabled={!repo.idle} onClick={() => repo.resolveCheckout("push")} className={cn(button, "bg-primary text-primary-foreground hover:bg-primary/90")}>Push first</button>
            <button type="button" disabled={!repo.idle} onClick={() => repo.resolveCheckout("discard")} className={cn(button, "border border-deleted text-deleted hover:bg-deleted/10")}>Discard</button>
            <button type="button" disabled={!repo.idle} onClick={repo.cancelCheckout} className={cn(button, "hover:bg-accent")}>Cancel</button>
          </div>
        </div>
      ) : detached ? (
        <div className="grid gap-2">
          <p className="flex items-start gap-2 text-[13px]">
            <AlertTriangle size={16} className="mt-0.5 shrink-0 text-modified" />
            <span>The model is at <b className="font-mono font-medium">{shortHash(detached.commit)}</b>, not the latest commit on {branch}. A push can only go to a branch tip, so push to a new branch instead.</span>
          </p>
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="flex h-8 min-w-0 flex-1 basis-48 items-center gap-1.5 rounded-md border bg-background px-2.5 focus-within:border-ring">
              <GitBranch size={14} className="shrink-0 text-muted-foreground" />
              <input
                value={repo.newBranch} spellCheck={false} autoComplete="off" aria-label="New branch name"
                onChange={(event) => repo.setNewBranch(event.target.value)}
                onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); repo.pushToNewBranch(); } }}
                className="min-w-0 flex-1 bg-transparent font-mono text-[12.5px] outline-none"
              />
            </span>
            <button type="button" disabled={!repo.idle || !repo.newBranch.trim() || repo.newBranch.trim() === branch} onClick={repo.pushToNewBranch}
              className={cn(button, "bg-primary text-primary-foreground hover:bg-primary/90")}>Push to new branch</button>
            <button type="button" disabled={!repo.idle} onClick={repo.cancelDetached} className={cn(button, "hover:bg-accent")}>Cancel</button>
          </div>
        </div>
      ) : null}
    </div>
  );
}
