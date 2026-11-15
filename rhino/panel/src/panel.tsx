import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ScrollArea } from "@/components/ui/scroll-area";
import { renderHistory } from "./graph/render";
import { shortHash } from "./graph/story";
import { createBridge } from "./protocol/bridge";
import type { CommitSummary, GraphCommit } from "./protocol/events";
import { cn } from "@/lib/utils";

type Pin = { id: string; message: string };
type History = {
  repo: string;
  commits: GraphCommit[];
  truncated: boolean;
  error: string;
  loaded: boolean;
};

const emptyHistory: History = { repo: "", commits: [], truncated: false, error: "", loaded: false };

export function Panel() {
  const bridge = useMemo(() => createBridge(), []);
  const connected = bridge.kind !== "unavailable";
  const [server, setServer] = useState("http://localhost:5280/");
  const [repo, setRepo] = useState("");
  const [branch, setBranch] = useState("main");
  const [commit, setCommit] = useState("");
  const [pin, setPin] = useState<Pin | null>(null);
  const [message, setMessage] = useState("Update geometry");
  const [commitChoices, setCommitChoices] = useState<CommitSummary[] | null>(null);
  const [commitOpen, setCommitOpen] = useState(false);
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
  const query = useRef({ server: "http://localhost:5280/", repo: "" });
  const hostRef = useRef<HTMLDivElement>(null);
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
      } else if (event.type === "commits") {
        if (event.error || event.commits.length === 0) {
          setCommitChoices(null);
          if (event.error) {
            setError(true);
            setStatus(event.message);
          }
          return;
        }
        setCommitChoices(event.commits);
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
    renderHistory(host, history.commits, scheme);
  }, [history.commits, scheme]);

  function snapshot() {
    const next = { server: server.trim(), repo: repo.trim() };
    query.current = next;
    return next;
  }

  function remember() {
    const next = { server: server.trim(), repo: repo.trim() };
    const changed = next.server !== query.current.server || next.repo !== query.current.repo;
    query.current = next;
    if (changed) setTick((value) => value + 1);
  }

  function chosen() {
    const text = commit.trim();
    if (pin && (text === pin.id || text === pin.message)) return pin.id;
    return text;
  }

  function onPull() {
    const spec = snapshot();
    setBusy(true);
    setError(false);
    setStatus("Pulling…");
    setCommitOpen(false);
    bridge.send({ type: "pull", ...spec, branch: branch.trim(), commit: chosen() });
  }

  function onPush() {
    const spec = snapshot();
    setBusy(true);
    setError(false);
    setStatus("Pushing…");
    bridge.send({ type: "push", ...spec, branch: branch.trim(), message: message.trim() });
  }

  const note = historyNote(history, requestedRepo, ready);
  const showChoices = commitOpen && commitChoices !== null && commitChoices.length > 0;

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
            <Field label="Server" value={server} onChange={setServer} onBlur={remember} />
            <Field label="Repository" value={repo} onChange={setRepo} onBlur={remember} />
            <Field label="Branch" value={branch} onChange={setBranch} />
            <Field
              label="Commit"
              value={commit}
              placeholder="Branch tip"
              onChange={(value) => {
                setCommit(value);
                setPin((current) => {
                  if (!current) return null;
                  const text = value.trim();
                  return text === current.id || text === current.message ? current : null;
                });
              }}
              onFocus={() => {
                setCommitOpen(true);
                if (!repo.trim()) return;
                bridge.send({ type: "commits", server: server.trim(), repo: repo.trim(), branch: branch.trim() });
              }}
              onBlur={() => setCommitOpen(false)}
            >
              {showChoices ? (
                <ScrollArea
                  type="auto"
                  className="rounded-md border"
                  style={{ height: Math.min(commitChoices.length * 48, 180) }}
                >
                  <ul>
                    {commitChoices.map((item) => (
                      <li key={item.id}>
                        <Button
                          type="button"
                          variant="ghost"
                          className="h-auto w-full justify-start rounded-none px-2 py-1.5 text-left font-normal"
                          onMouseDown={(event) => event.preventDefault()}
                          onClick={() => {
                            setCommit(item.message);
                            setPin({ id: item.id, message: item.message });
                            setCommitOpen(false);
                          }}
                        >
                          <span className="flex min-w-0 flex-1 flex-col items-start">
                            <span className="w-full truncate">{item.message || "(no message)"}</span>
                            <span className="font-mono text-xs text-muted-foreground">{shortHash(item.id)}</span>
                          </span>
                        </Button>
                      </li>
                    ))}
                  </ul>
                </ScrollArea>
              ) : null}
            </Field>
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

function historyNote(history: History, requestedRepo: string, ready: boolean): string | null {
  if (!ready) return null;
  if (!requestedRepo) return "Enter a repository to see its history.";
  if (!history.loaded || history.repo !== requestedRepo) return "Loading history…";
  if (history.error) return history.error;
  if (history.commits.length === 0) return "No commits yet.";
  return null;
}

function Field({
  label,
  value,
  onChange,
  onBlur,
  onFocus,
  placeholder,
  children,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  onBlur?: () => void;
  onFocus?: () => void;
  placeholder?: string;
  children?: ReactNode;
}) {
  const id = useId();
  return (
    <div className="grid gap-1">
      <Label htmlFor={id} className="text-muted-foreground">{label}</Label>
      <Input
        id={id}
        className="h-8"
        value={value}
        placeholder={placeholder}
        onChange={(event) => onChange(event.target.value)}
        onBlur={onBlur}
        onFocus={onFocus}
      />
      {children}
    </div>
  );
}
