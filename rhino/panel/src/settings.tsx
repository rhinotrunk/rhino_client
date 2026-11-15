import { useEffect, useState } from "react";
import { X } from "lucide-react";
import type { Bridge } from "./protocol/bridge";
import type { Settings } from "./protocol/events";
import { budgetModel } from "./protocol/events";
import type { Repo } from "./repo";

// Worker settings belong to the whole panel, so the dialog covers header and tabs alike.
export function SettingsDialog({ bridge, repo, current, onClose }: { bridge: Bridge; repo: Repo; current: Settings | undefined; onClose: () => void }) {
  const [settings, setSettings] = useState<Settings>(current ?? { codexPath: "codex", model: budgetModel, concurrency: 2 });
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === "Escape") onClose(); };
    document.addEventListener("keydown", escape);
    return () => document.removeEventListener("keydown", escape);
  }, [onClose]);
  const field = "h-9 w-full rounded-md border bg-background px-2.5 py-0 text-[13px] leading-none outline-none focus:border-ring";
  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center bg-black/50 p-4 pt-16" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
      <section role="dialog" aria-modal="true" aria-labelledby="settings-title" className="w-full max-w-md rounded-xl border bg-card p-5 shadow-2xl">
        <header className="mb-4 flex items-center justify-between">
          <h2 id="settings-title" className="text-[15px] font-semibold">Settings</h2>
          <button type="button" aria-label="Close settings" onClick={onClose} className="flex h-8 w-8 items-center justify-center rounded-md hover:bg-accent"><X size={16} /></button>
        </header>
        <form className="grid gap-3.5" onSubmit={(event) => { event.preventDefault(); repo.remember(); bridge.send({ type: "settings.save", ...settings }); onClose(); }}>
          <label className="grid gap-1.5 text-[12px] text-muted-foreground">Repository server
            <input className={field} value={repo.server} spellCheck={false} onChange={(event) => repo.setServer(event.target.value)} onBlur={() => repo.remember()} />
          </label>
          <hr className="my-1" />
          <label className="grid gap-1.5 text-[12px] text-muted-foreground">Codex executable
            <input required className={field} value={settings.codexPath} onChange={(event) => setSettings({ ...settings, codexPath: event.target.value })} />
          </label>
          <label className="grid gap-1.5 text-[12px] text-muted-foreground">Model
            <select className={field} value={settings.model} onChange={(event) => setSettings({ ...settings, model: event.target.value })}>
              <option value="gpt-6-luna">GPT-6 Luna — budget default</option>
              <option value="gpt-6.1-terra">GPT-6.1 Terra — lower-cost 6.1</option>
            </select>
          </label>
          <label className="grid gap-1.5 text-[12px] text-muted-foreground">Concurrent task workers
            <select className={field} value={settings.concurrency} onChange={(event) => setSettings({ ...settings, concurrency: Number(event.target.value) })}>
              {[1, 2, 3, 4].map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          </label>
          <p className="text-[11px] leading-relaxed text-muted-foreground">Uses your existing Codex CLI sign-in. Low reasoning effort; no automatic upgrade to a pricier model.</p>
          <footer className="flex justify-end">
            <button type="submit" className="h-8 rounded-md bg-primary px-4 text-[13px] font-medium text-primary-foreground hover:bg-primary/90">Save</button>
          </footer>
        </form>
      </section>
    </div>
  );
}
