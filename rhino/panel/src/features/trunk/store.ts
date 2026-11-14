import { createContext, useContext, useEffect, useState, useSyncExternalStore } from 'react';
import type { MockEngine } from '@/engine/mock';

export const EngineContext = createContext<MockEngine | null>(null);

export function useEngine() {
  const engine = useContext(EngineContext);
  if (!engine) throw new Error('useEngine needs <EngineContext.Provider>.');
  return engine;
}

export function useTrunk() {
  const engine = useEngine();
  return useSyncExternalStore(engine.subscribe, engine.getState);
}

/** Re-renders every `ms` while `active`, for live clocks. */
export function useNow(active: boolean, ms = 1000) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const t = setInterval(() => setNow(Date.now()), ms);
    return () => clearInterval(t);
  }, [active, ms]);
  return now;
}

export const elapsed = (fromIso: string, toMs: number) => {
  const s = Math.max(0, Math.round((toMs - new Date(fromIso).getTime()) / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
};
