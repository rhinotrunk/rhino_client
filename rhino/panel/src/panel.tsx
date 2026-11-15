import { useEffect, useId, useLayoutEffect, useMemo, useState, type ReactNode } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ScrollArea } from "@/components/ui/scroll-area";
import { createBridge } from "./protocol/bridge";
import type { CommitSummary } from "./protocol/events";
import { cn } from "@/lib/utils";

type Pin = { id: string; message: string };

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

  function chosen() {
    const text = commit.trim();
    if (pin && (text === pin.id || text === pin.message)) return pin.id;
    return text;
  }

  function onPull() {
    setBusy(true);
    setError(false);
    setStatus("Pulling…");
    setCommitOpen(false);
    bridge.send({
      type: "pull",
      server: server.trim(),
      repo: repo.trim(),
      branch: branch.trim(),
      commit: chosen(),
    });
  }

  function onPush() {
    setBusy(true);
    setError(false);
    setStatus("Pushing…");
    bridge.send({
      type: "push",
      server: server.trim(),
      repo: repo.trim(),
      branch: branch.trim(),
      message: message.trim(),
    });
  }

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
            <Field label="Server" value={server} onChange={setServer} />
            <Field label="Repository" value={repo} onChange={setRepo} />
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
      </Card>
    </div>
  );
}

function shortHash(id: string) {
  const prefix = "sha256:";
  const hex = id.startsWith(prefix) ? id.slice(prefix.length) : id;
  return hex.slice(0, 8);
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
