import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { Bridge } from "./protocol/bridge";
import type { AgentTask, ControlState, GraphCommit } from "./protocol/events";

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

  const idle = ready && !busy;
  return {
    bridge, server, repo, branch, message, fresh, status, error, busy, ready, scheme, history, requestedRepo, control, repos, branches,
    idle,
    setServer, setRepo, setBranch, setMessage, remember, requestRepos, requestBranches, chooseRepo,
    pull() {
      const spec = snapshot();
      begin("Pulling…");
      bridge.send({ type: "pull", ...spec, branch: branch.trim() });
    },
    push() {
      const spec = snapshot();
      begin("Pushing…");
      bridge.send({ type: "push", ...spec, branch: branch.trim(), message: message.trim() || "Update geometry" });
      setMessage("");
    },
    checkout(commit: string, onBranch?: string) {
      if (!idle) return;
      const spec = snapshot();
      const target = onBranch ?? branch.trim();
      if (onBranch) setBranch(onBranch);
      begin("Checking out…");
      bridge.send({ type: "checkout", ...spec, branch: target, commit });
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
      bridge.send({ type: "task.merge", id });
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
