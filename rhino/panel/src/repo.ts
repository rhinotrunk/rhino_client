import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { Bridge } from "./protocol/bridge";
import type { AgentTask, ControlState, GraphCommit, PanelCommand, UnpushedChoice } from "./protocol/events";

export type History = {
  repo: string;
  head: string;
  commits: GraphCommit[];
  truncated: boolean;
  error: string;
  loaded: boolean;
};
const emptyHistory: History = { repo: "", head: "", commits: [], truncated: false, error: "", loaded: false };

type Options = { loading: boolean; options: string[]; note: string; server: string; repo: string };

// Everything the sticky header and the graph share: the connection, the git commands, history,
// and the control state that tells which agent tasks have a pull request.
export function useRepo(bridge: Bridge) {
  const connected = bridge.kind !== "unavailable";
  const [server, setServer] = useState("http://localhost:5280/");
  const [repo, setRepo] = useState("");
  const [branch, setBranch] = useState("main");
  const [message, setMessage] = useState("");
  const [status, setStatus] = useState(connected ? "Connecting…" : "Could not connect to Rhino.");
  const [error, setError] = useState(false);
  const [busy, setBusy] = useState(true);
  const [ready, setReady] = useState(false);
  const [scheme, setScheme] = useState<"light" | "dark">(() =>
    window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light",
  );
  const [history, setHistory] = useState<History>(emptyHistory);
  const [requestedRepo, setRequestedRepo] = useState("");
  const [tick, setTick] = useState(0);
  const [control, setControl] = useState<ControlState>();
  const [fresh, setFresh] = useState("");
  const [unpushed, setUnpushed] = useState<{ commit: string; changes: number; branch?: string } | null>(null);
  const [detached, setDetached] = useState<{ commit: string } | null>(null);
  const [newBranch, setNewBranch] = useState("");
  const [conflict, setConflict] = useState<{ id: string; title: string; branch: string; into: string; conflicts: number } | null>(null);
  // Commits picked in the graph, in click order: one previews it in the viewport, two or more pick a merge in Rhino.
  const [picked, setPicked] = useState<string[]>([]);
  const branchRef = useRef("main");
  const checkoutTarget = useRef<string | undefined>(undefined);
  const [repos, setRepos] = useState<Options>({ loading: false, options: [], note: "", server: "", repo: "" });
  const [branches, setBranches] = useState<Options>({ loading: false, options: [], note: "", server: "", repo: "" });
  const query = useRef({ server: "http://localhost:5280/", repo: "" });
  const repoRequest = useRef("");
  const branchRequest = useRef({ server: "", repo: "" });

  useLayoutEffect(() => {
    document.documentElement.classList.toggle("dark", scheme === "dark");
    document.documentElement.style.colorScheme = scheme;
  }, [scheme]);

  useEffect(() => {
    return bridge.subscribe((event) => {
      if (event.type === "theme") {
        setScheme(event.scheme);
      } else if (event.type === "control.state") {
        setControl(event);
      } else if (event.type === "session") {
        setServer(event.server);
        setRepo(event.repo);
        setBranch(event.branch);
        query.current = { server: event.server.trim(), repo: event.repo.trim() };
        setTick((value) => value + 1);
      } else if (event.type === "repos") {
        if (event.server !== repoRequest.current) return;
        setRepos({ loading: false, options: event.error ? [] : event.repos, note: event.error ? event.message : "", server: event.server, repo: "" });
      } else if (event.type === "branches") {
        if (event.server !== branchRequest.current.server || event.repo !== branchRequest.current.repo) return;
        setBranches({ loading: false, options: event.error ? [] : event.branches, note: event.error ? event.message : "", server: event.server, repo: event.repo });
      } else if (event.type === "graph") {
        if (event.repo !== query.current.repo) return;
        setHistory({
          repo: event.repo,
          head: event.head ?? "",
          commits: event.error ? [] : event.commits,
          truncated: event.error ? false : event.truncated,
          error: event.error ? event.message : "",
          loaded: true,
        });
      } else if (event.type === "checkout.unpushed") {
        setUnpushed({ commit: event.commit, changes: event.changes, branch: checkoutTarget.current });
      } else if (event.type === "merge.conflict") {
        setConflict({ id: event.id, title: event.title, branch: event.branch, into: event.into, conflicts: event.conflicts });
      } else if (event.type === "push.detached") {
        setDetached({ commit: event.commit });
        setNewBranch(`${branchRef.current.trim() || "branch"}-${event.commit.replace(/^sha256:/, "").slice(0, 7)}`);
      } else if (event.type === "selection.clear") {
        setPicked([]);
      } else if (event.type === "head") {
        setHistory((existing) => (existing.repo === event.repo ? { ...existing, head: event.commit ?? "" } : existing));
      } else if (event.type === "status") {
        setReady(connected);
        setBusy(event.busy);
        setError(event.error);
        setStatus(event.message);
      }
    });
  }, [bridge, connected]);

  useEffect(() => {
    bridge.send({ type: "ready" });
  }, [bridge]);

  useEffect(() => {
    if (!ready || busy) return;
    const current = query.current;
    setRequestedRepo(current.repo);
    if (!current.repo) {
      setHistory({ ...emptyHistory, loaded: true });
      return;
    }
    setHistory((existing) =>
      existing.repo === current.repo ? existing : { ...emptyHistory, repo: current.repo },
    );
    bridge.send({ type: "graph", server: current.server, repo: current.repo });
  }, [bridge, ready, busy, tick]);

  function snapshot() {
    const next = { server: server.trim(), repo: repo.trim() };
    query.current = next;
    return next;
  }

  function remember(nextServer = server, nextRepo = repo) {
    const next = { server: nextServer.trim(), repo: nextRepo.trim() };
    const changed = next.server !== query.current.server || next.repo !== query.current.repo;
    query.current = next;
    if (changed) setTick((value) => value + 1);
  }

  function requestRepos(serverValue = server) {
    if (!connected) return;
    const next = serverValue.trim();
    repoRequest.current = next;
    if (!next) {
      setRepos({ loading: false, options: [], note: "", server: "", repo: "" });
      return;
    }
    setRepos((current) => ({ ...current, loading: true, note: "" }));
    bridge.send({ type: "repos", server: next });
  }

  function requestBranches(serverValue = server, repoValue = repo) {
    if (!connected) return;
    const nextServer = serverValue.trim();
    const nextRepo = repoValue.trim();
    branchRequest.current = { server: nextServer, repo: nextRepo };
    if (!nextServer || !nextRepo) {
      setBranches({ loading: false, options: [], note: nextServer && !nextRepo ? "Enter a repository first." : "", server: nextServer, repo: nextRepo });
      return;
    }
    setBranches((current) => ({ ...current, loading: true, note: "" }));
    bridge.send({ type: "branches", server: nextServer, repo: nextRepo });
  }

  function chooseRepo(value: string, created = false) {
    setFresh(created ? value.trim() : "");
    setRepo(value);
    if (value.trim() !== branches.repo) setBranches({ loading: false, options: [], note: "", server: "", repo: "" });
    remember(server, value);
  }

  function begin(text: string) {
    setBusy(true);
    setError(false);
    setStatus(text);
  }

  branchRef.current = branch;
  const idle = ready && !busy;
  const clearPrompts = () => { setUnpushed(null); setDetached(null); };
  const sendPush = (target: string) => {
    const spec = snapshot();
    clearPrompts();
    begin("Pushing…");
    bridge.send({ type: "push", ...spec, branch: target, message: message.trim() || "Update geometry" });
    setMessage("");
  };
  const sendCheckout = (commit: string, target: string, choice: UnpushedChoice) => {
    const spec = snapshot();
    clearPrompts();
    checkoutTarget.current = target;
    begin("Checking out…");
    bridge.send({ type: "checkout", ...spec, branch: target, commit, message: message.trim(), unpushed: choice });
  };
  return {
    bridge, server, repo, branch, message, unpushed, detached, conflict, newBranch, setNewBranch, fresh, status, error, busy, ready, scheme, history, requestedRepo, control, repos, branches,
    idle, picked,
    // A plain click previews one commit (clicking it again clears); shift-click adds or removes a commit, and two
    // or more start merge picking in Rhino.
    pickCommit(id: string, extend: boolean) {
      if (!ready) return;
      const next = extend
        ? picked.includes(id) ? picked.filter((other) => other !== id) : [...picked, id]
        : picked.length === 1 && picked[0] === id ? [] : [id];
      setPicked(next);
      const spec = snapshot();
      if (next.length === 0) bridge.send({ type: "commit.preview.clear" });
      else if (next.length === 1) bridge.send({ type: "commit.preview", ...spec, commit: next[0]! });
      else bridge.send({ type: "merge.pick", ...spec, branch: branch.trim(), commits: next });
    },
    // A click away from the graph ends a single-commit preview. Merge picking is changed by clicking its commits.
    clearPreview() {
      if (picked.length !== 1) return;
      setPicked([]);
      bridge.send({ type: "commit.preview.clear" });
    },
    // The pointer is on a commit: a good moment to load its preview so the click is immediate.
    prefetch(id: string) {
      if (!idle) return;
      const spec = snapshot();
      bridge.send({ type: "commit.prefetch", ...spec, commit: id });
    },
    setServer, setRepo, setBranch, setMessage, remember, requestRepos, requestBranches, chooseRepo,
    pull() {
      const spec = snapshot();
      clearPrompts();
      begin("Pulling…");
      bridge.send({ type: "pull", ...spec, branch: branch.trim() });
    },
    push() {
      sendPush(branch.trim());
    },
    // Checkout replaces the model. With unpushed edits the host stops and the panel asks what to do.
    checkout(commit: string, onBranch?: string) {
      if (!idle) return;
      const target = onBranch ?? branch.trim();
      if (onBranch) setBranch(onBranch);
      sendCheckout(commit, target, "ask");
    },
    resolveCheckout(choice: UnpushedChoice) {
      if (!unpushed) return;
      sendCheckout(unpushed.commit, unpushed.branch ?? branch.trim(), choice);
    },
    cancelCheckout() {
      setUnpushed(null);
      setError(false);
      setStatus("Checkout cancelled. The model was not changed.");
    },
    // The model is not at the tip of its branch, so a push has to go to another branch.
    pushToNewBranch() {
      const name = newBranch.trim();
      if (!name || name === branch.trim()) return;
      setBranch(name);
      sendPush(name);
    },
    cancelDetached() {
      setDetached(null);
      setError(false);
      setStatus("Push cancelled. Nothing was pushed and the model was not changed.");
    },
    refresh() {
      if (!idle) return;
      setTick((value) => value + 1);
    },
    openPr(id: string) {
      bridge.send({ type: "task.pr.open", id });
    },
    closePr(id: string) {
      bridge.send({ type: "task.pr.close", id });
    },
    mergePr(id: string) {
      setConflict(null);
      bridge.send({ type: "task.merge", id } as PanelCommand);
    },
    retargetPr(id: string, target: string) {
      bridge.send({ type: "task.pr.base", id, branch: target });
    },
    // Merge any branch into another. The host asks which side wins when objects conflict.
    mergeBranch(theirs: string, into: string) {
      if (!idle) return;
      setConflict(null);
      begin(`Merging ${theirs} into ${into}…`);
      bridge.send({ type: "branch.merge", theirs, into });
    },
    resolveMerge(resolve: "ours" | "theirs") {
      if (!conflict) return;
      const { id, branch: theirs, into } = conflict;
      setConflict(null);
      if (id) bridge.send({ type: "task.merge", id, resolve });
      else bridge.send({ type: "branch.merge", theirs, into, resolve });
    },
    cancelMerge() {
      setConflict(null);
    },
  };
}

export type Repo = ReturnType<typeof useRepo>;

export function openPrs(control: ControlState | undefined): AgentTask[] {
  return (control?.tasks ?? []).filter((task) => !task.deleted && !task.archived && task.prBranch && !task.mergedBranch);
}

export function readyTasks(control: ControlState | undefined): AgentTask[] {
  return (control?.tasks ?? []).filter(
    (task) => !task.deleted && !task.archived && task.status === "ready" && !task.prBranch && !task.mergedBranch,
  );
}

// Mirrors Names.IsValidRepo in the git engine.
const validRepo = /^[a-z0-9][a-z0-9._-]{0,63}$/;

export function toRepoName(value: string): string {
  const text = value.trim();
  if (validRepo.test(text)) return text;
  const name = text
    .normalize("NFKD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, "-")
    .replace(/^[._-]+/, "")
    .slice(0, 64)
    .replace(/[._-]+$/, "");
  return name || text;
}
