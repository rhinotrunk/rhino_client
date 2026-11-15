import { useEffect, useState } from "react";
import type { Bridge } from "./protocol/bridge";

// UI state that survives a reload of this page. Rhino can reload the panel's web view (re-docking, or the panel
// losing its host window when you click elsewhere), which would otherwise bring the UI back fresh. Each value is
// saved to Rhino under a key and handed back on 'ready' as 'ui.state'. Browser storage is not used: the embedded
// page has no stable origin for it.

let saved: Record<string, unknown> = {};
let bridge: Bridge | undefined;
let timer: number | undefined;
const listeners = new Set<() => void>();

export function initPersist(target: Bridge): void {
  bridge = target;
  target.subscribe((event) => {
    if (event.type !== "ui.state") return;
    saved = { ...event.state, ...saved };
    for (const listener of listeners) listener();
  });
}

function save(key: string, value: unknown): void {
  saved = { ...saved, [key]: value };
  // Batch rapid changes (typing, scrolling) into one message.
  window.clearTimeout(timer);
  timer = window.setTimeout(() => bridge?.send({ type: "ui.save", state: saved }), 250);
}

/** Like useState, but the value is kept by Rhino and restored if the page reloads. Values must be JSON. */
export function usePersisted<T>(key: string, initial: T): [T, (next: T | ((previous: T) => T)) => void] {
  const [value, setValue] = useState<T>(() => (key in saved ? (saved[key] as T) : initial));

  useEffect(() => {
    const restore = () => {
      if (key in saved) setValue(saved[key] as T);
    };
    listeners.add(restore);
    return () => {
      listeners.delete(restore);
    };
  }, [key]);

  const set = (next: T | ((previous: T) => T)) => {
    setValue((previous) => {
      const resolved = typeof next === "function" ? (next as (previous: T) => T)(previous) : next;
      save(key, resolved);
      return resolved;
    });
  };
  return [value, set];
}
