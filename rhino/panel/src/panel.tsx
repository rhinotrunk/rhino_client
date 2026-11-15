import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ScrollArea } from "@/components/ui/scroll-area";
import { renderHistory } from "./graph/render";
import type { Bridge } from "./protocol/bridge";
import type { GraphCommit } from "./protocol/events";
import { cn } from "@/lib/utils";

type History = {
  repo: string;
  commits: GraphCommit[];
  truncated: boolean;
  error: string;
  loaded: boolean;
};

const emptyHistory: History = { repo: "", commits: [], truncated: false, error: "", loaded: false };

export function Panel({ bridge }: { bridge: Bridge }) {
  const connected = bridge.kind !== "unavailable";
  const [server, setServer] = useState("http://localhost:5280/");
  const [repo, setRepo] = useState("");
  const [branch, setBranch] = useState("main");
  const [message, setMessage] = useState("Update geometry");
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
  const [repos, setRepos] = useState<string[]>([]);
  const [repoNote, setRepoNote] = useState("");
  const [reposLoading, setReposLoading] = useState(false);
  const [listedRepoServer, setListedRepoServer] = useState("");
  const [branches, setBranches] = useState<string[]>([]);
  const [branchNote, setBranchNote] = useState("");
  const [branchesLoading, setBranchesLoading] = useState(false);
  const [listedBranchServer, setListedBranchServer] = useState("");
  const [listedBranchRepo, setListedBranchRepo] = useState("");
  const query = useRef({ server: "http://localhost:5280/", repo: "" });
  const repoRequest = useRef("");
  const branchRequest = useRef({ server: "", repo: "" });
  const hostRef = useRef<HTMLDivElement>(null);
  const checkoutRef = useRef<(id: string) => void>(() => {});
  const disabled = !ready || busy;

  useLayoutEffect(() => {
    document.documentElement.classList.toggle("dark", scheme === "dark");
    document.documentElement.style.colorScheme = scheme;
  }, [scheme]);

  useEffect(() => {
    return bridge.subscribe((event) => {
      if (event.type === "theme") {
        setScheme(event.scheme);
      } else if (event.type === "session") {
        setServer(event.server);
        setRepo(event.repo);
        setBranch(event.branch);
        query.current = { server: event.server.trim(), repo: event.repo.trim() };
        setTick((value) => value + 1);
      } else if (event.type === "repos") {
        if (event.server !== repoRequest.current) return;
        setReposLoading(false);
        setRepos(event.error ? [] : event.repos);
        setRepoNote(event.error ? event.message : "");
        setListedRepoServer(event.server);
      } else if (event.type === "branches") {
        if (event.server !== branchRequest.current.server || event.repo !== branchRequest.current.repo) return;
        setBranchesLoading(false);
        setBranches(event.error ? [] : event.branches);
        setBranchNote(event.error ? event.message : "");
        setListedBranchServer(event.server);
        setListedBranchRepo(event.repo);
      } else if (event.type === "graph") {
        if (event.repo !== query.current.repo) return;
        setHistory({
          repo: event.repo,
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
      setHistory({ repo: "", commits: [], truncated: false, error: "", loaded: true });
      return;
    }
    setHistory((currentHistory) =>
      currentHistory.repo === current.repo
        ? currentHistory
        : { repo: current.repo, commits: [], truncated: false, error: "", loaded: false },
    );
    bridge.send({ type: "graph", server: current.server, repo: current.repo });
  }, [bridge, ready, busy, tick]);

  useEffect(() => {
    const host = hostRef.current;
    if (!host || history.commits.length === 0) return;
    renderHistory(host, history.commits, scheme, (id) => checkoutRef.current(id));
  }, [history.commits, scheme]);

  useEffect(() => {
    hostRef.current?.classList.toggle("is-busy", disabled);
  }, [disabled, history.commits]);

  function snapshot() {
    const next = { server: server.trim(), repo: toRepoName(repo) };
    query.current = next;
    return next;
  }

  function remember(nextServer = server, nextRepo = repo) {
    const next = { server: nextServer.trim(), repo: toRepoName(nextRepo) };
    const changed = next.server !== query.current.server || next.repo !== query.current.repo;
    query.current = next;
    if (changed) setTick((value) => value + 1);
  }

  function requestRepos(serverValue = server) {
    if (bridge.kind === "unavailable") return;
    const next = serverValue.trim();
    repoRequest.current = next;
    if (!next) {
      setRepos([]);
      setRepoNote("");
      setReposLoading(false);
      return;
    }
    setReposLoading(true);
    setRepoNote("");
    bridge.send({ type: "repos", server: next });
  }

  function requestBranches(serverValue = server, repoValue = repo) {
    if (bridge.kind === "unavailable") return;
    const nextServer = serverValue.trim();
    const nextRepo = toRepoName(repoValue);
    branchRequest.current = { server: nextServer, repo: nextRepo };
    if (!nextServer || !nextRepo) {
      setBranches([]);
      setBranchNote(nextServer && !nextRepo ? "Enter a repository first." : "");
      setBranchesLoading(false);
      setListedBranchServer(nextServer);
      setListedBranchRepo(nextRepo);
      return;
    }
    setBranchesLoading(true);
    setBranchNote("");
    bridge.send({ type: "branches", server: nextServer, repo: nextRepo });
  }

  function chooseRepo(value: string) {
    const name = toRepoName(value);
    setRepo(name);
    if (name !== listedBranchRepo) {
      setBranches([]);
      setBranchNote("");
      setListedBranchRepo("");
    }
    remember(server, name);
  }

  function onPull() {
    const spec = snapshot();
    setBusy(true);
    setError(false);
    setStatus("Pulling…");
    bridge.send({ type: "pull", ...spec, branch: branch.trim() });
  }

  checkoutRef.current = (id: string) => {
    if (!ready || busy) return;
    const spec = snapshot();
    setBusy(true);
    setError(false);
    setStatus("Checking out…");
    bridge.send({ type: "checkout", ...spec, branch: branch.trim(), commit: id });
  };

  function onPush() {
    const spec = snapshot();
    setBusy(true);
    setError(false);
    setStatus("Pushing…");
    bridge.send({ type: "push", ...spec, branch: branch.trim(), message: message.trim() });
  }

  const note = historyNote(history, requestedRepo, ready);

  return (
    <div className="h-full overflow-y-auto bg-background p-2 text-foreground">
      <Card className="min-w-0">
        <CardHeader className="space-y-2 p-3">
          <div className="flex items-center justify-between gap-2">
            <CardTitle className="text-base">Repository</CardTitle>
            {branch.trim() ? (
              <Badge variant="secondary" className="max-w-[50%] truncate font-mono font-normal">
                {branch.trim()}
              </Badge>
            ) : null}
          </div>
          <div className="grid min-w-0 gap-2">
            <Field label="Server" value={server} onChange={setServer} onBlur={() => remember()} />
            <SuggestField
              label="Repository"
              value={repo}
              onChange={setRepo}
              onCommit={chooseRepo}
              options={listedRepoServer === server.trim() ? repos : []}
              note={listedRepoServer === server.trim() ? repoNote : ""}
              loading={reposLoading}
              empty="No repositories yet."
              onOpen={() => requestRepos()}
            />
            <SuggestField
              label="Branch"
              value={branch}
              onChange={setBranch}
              options={listedBranchServer === server.trim() && listedBranchRepo === toRepoName(repo) ? branches : []}
              note={listedBranchServer === server.trim() && listedBranchRepo === toRepoName(repo) ? branchNote : ""}
              loading={branchesLoading}
              empty="No branches yet."
              onOpen={() => requestBranches()}
            />
            <Field label="Message" value={message} onChange={setMessage} />
          </div>
          <div className="flex gap-2">
            <Button type="button" className="h-8 flex-1 text-base" disabled={disabled} onClick={onPull}>
              Pull
            </Button>
            <Button type="button" variant="secondary" className="h-8 flex-1 text-base" disabled={disabled} onClick={onPush}>
              Push
            </Button>
          </div>
          <p role="status" className={cn("text-sm wrap-anywhere", error ? "text-destructive" : "text-muted-foreground")}>
            {status}
          </p>
        </CardHeader>
        <CardContent className="border-t p-3">
          <div className="mb-2 flex items-center justify-between gap-2">
            <h2 className="text-sm font-medium">History</h2>
            {history.truncated && history.loaded && history.commits.length > 0 ? (
              <Badge variant="outline" className="font-normal">Truncated</Badge>
            ) : null}
          </div>
          {note ? (
            <p className={cn("text-sm wrap-anywhere", history.error ? "text-destructive" : "text-muted-foreground")}>{note}</p>
          ) : (
            <ScrollArea type="auto" className="h-72">
              <div ref={hostRef} className="history w-max px-1 py-2" />
            </ScrollArea>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

// Mirrors Names.IsValidRepo in the git engine.
const validRepo = /^[a-z0-9][a-z0-9._-]{0,63}$/;

function toRepoName(value: string): string {
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

function historyNote(history: History, requestedRepo: string, ready: boolean): string | null {
  if (!ready) return null;
  if (!requestedRepo) return "Enter a repository to see its history.";
  if (!history.loaded || history.repo !== requestedRepo) return "Loading history…";
  if (history.error) return history.error;
  if (history.commits.length === 0) return "No commits yet.";
  return null;
}

function SuggestField({
  label,
  value,
  onChange,
  onCommit,
  options,
  note,
  loading,
  empty,
  onOpen,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onCommit?: (value: string) => void;
  options: readonly string[];
  note: string;
  loading: boolean;
  empty: string;
  onOpen: () => void;
}) {
  const id = useId();
  const listId = useId();
  const [open, setOpen] = useState(false);
  const [filtering, setFiltering] = useState(false);
  const [active, setActive] = useState(0);
  const [place, setPlace] = useState({ top: 0, left: 0, width: 0, maxHeight: 160 });
  const openRef = useRef(false);
  const moved = useRef(false);
  const choosing = useRef(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLUListElement>(null);
  const activeRef = useRef<HTMLButtonElement>(null);

  const shown = useMemo(() => {
    if (!filtering) return options;
    const query = value.trim().toLowerCase();
    if (!query) return options;
    return options.filter((name) => name.toLowerCase().includes(query));
  }, [filtering, options, value]);
  const shownKey = shown.join("\n");

  useEffect(() => {
    if (!open) return;
    if (moved.current) {
      setActive((index) => (shown.length === 0 ? 0 : Math.min(index, shown.length - 1)));
      return;
    }
    const match = shown.findIndex((name) => name === value.trim());
    setActive(match >= 0 ? match : 0);
  }, [open, shown, shownKey, value]);

  useLayoutEffect(() => {
    if (!open) return;
    function placeMenu() {
      const box = inputRef.current?.getBoundingClientRect();
      if (!box) return;
      const margin = 4;
      const below = window.innerHeight - box.bottom - margin;
      const above = box.top - margin;
      const openUp = below < 120 && above > below;
      const maxHeight = Math.max(80, Math.min(160, openUp ? above : below));
      const top = openUp ? Math.max(margin, box.top - margin - maxHeight) : box.bottom + margin;
      setPlace((current) =>
        current.top === top && current.left === box.left && current.width === box.width && current.maxHeight === maxHeight
          ? current
          : { top, left: box.left, width: box.width, maxHeight },
      );
    }
    placeMenu();
    window.addEventListener("resize", placeMenu);
    window.addEventListener("scroll", placeMenu, true);
    return () => {
      window.removeEventListener("resize", placeMenu);
      window.removeEventListener("scroll", placeMenu, true);
    };
  }, [open, shownKey]);

  useEffect(() => {
    const list = listRef.current;
    const item = activeRef.current;
    if (!list || !item) return;
    const top = item.offsetTop;
    const bottom = top + item.offsetHeight;
    if (top < list.scrollTop) list.scrollTop = top;
    else if (bottom > list.scrollTop + list.clientHeight) list.scrollTop = bottom - list.clientHeight;
  }, [active, open, shownKey]);

  function setMenu(next: boolean) {
    openRef.current = next;
    setOpen(next);
  }

  function openMenu() {
    const already = openRef.current;
    moved.current = false;
    setFiltering(false);
    setMenu(true);
    if (!already) onOpen();
  }

  function closeMenu() {
    setMenu(false);
  }

  function choose(name: string) {
    onChange(name);
    onCommit?.(name);
    closeMenu();
  }

  function move(delta: number) {
    if (shown.length === 0) return;
    moved.current = true;
    setActive((index) => (index + delta + shown.length) % shown.length);
  }

  const showChoices = shown.length > 0;
  const showStatus = !filtering && !showChoices && (loading || note !== "" || options.length === 0);
  const menu = open && (showChoices || showStatus);

  return (
    <div className="relative grid gap-1">
      <Label htmlFor={id} className="text-muted-foreground">{label}</Label>
      <Input
        id={id}
        ref={inputRef}
        className="h-8"
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={open}
        aria-controls={listId}
        autoComplete="off"
        autoCorrect="off"
        spellCheck={false}
        value={value}
        onChange={(event) => {
          moved.current = false;
          setFiltering(true);
          const already = openRef.current;
          setMenu(true);
          onChange(event.target.value);
          if (!already) onOpen();
        }}
        onFocus={openMenu}
        onClick={openMenu}
        onBlur={() => {
          closeMenu();
          if (choosing.current) {
            choosing.current = false;
            return;
          }
          onCommit?.(value);
        }}
        onKeyDown={(event) => {
          if (event.key === "ArrowDown") {
            event.preventDefault();
            if (!openRef.current) openMenu();
            else move(1);
          } else if (event.key === "ArrowUp") {
            event.preventDefault();
            if (openRef.current) move(-1);
          } else if (event.key === "Enter" && openRef.current && shown[active]) {
            event.preventDefault();
            choose(shown[active]);
          } else if (event.key === "Escape") {
            closeMenu();
          }
        }}
      />
      {menu ? createPortal(
        <ul
          id={listId}
          ref={listRef}
          role="listbox"
          style={{ top: place.top, left: place.left, width: place.width, maxHeight: place.maxHeight }}
          className="fixed z-30 overflow-auto rounded-md border bg-card p-1 shadow-md"
          onMouseDown={(event) => event.preventDefault()}
        >
          {showChoices ? shown.map((name, index) => (
            <li key={name} role="presentation">
              <button
                type="button"
                role="option"
                aria-selected={index === active}
                ref={index === active ? activeRef : undefined}
                className={cn(
                  "flex w-full rounded-sm px-2 py-1 text-left text-base",
                  index === active ? "bg-accent text-accent-foreground" : "hover:bg-accent hover:text-accent-foreground",
                  name === value.trim() && "font-medium",
                )}
                onMouseEnter={() => {
                  moved.current = true;
                  setActive(index);
                }}
                onMouseDown={(event) => {
                  event.preventDefault();
                  choosing.current = true;
                  window.setTimeout(() => {
                    choosing.current = false;
                  }, 0);
                }}
                onClick={() => choose(name)}
              >
                <span className="truncate">{name}</span>
              </button>
            </li>
          )) : (
            <li className={cn("px-2 py-1 text-sm", note ? "text-destructive" : "text-muted-foreground")}>
              {loading ? "Loading…" : note || empty}
            </li>
          )}
        </ul>,
        document.body,
      ) : null}
    </div>
  );
}

function Field({
  label,
  value,
  onChange,
  onBlur,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onBlur?: () => void;
}) {
  const id = useId();
  return (
    <div className="grid gap-1">
      <Label htmlFor={id} className="text-muted-foreground">{label}</Label>
      <Input id={id} className="h-8" value={value} onChange={(event) => onChange(event.target.value)} onBlur={onBlur} />
    </div>
  );
}
