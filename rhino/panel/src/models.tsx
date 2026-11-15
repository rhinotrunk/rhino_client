import { useState } from "react";
import { cn } from "@/lib/utils";
import type { ModelOption } from "./protocol/events";

const OTHER = "__other__";

// Pick a chat model. OpenAI models run through the Codex CLI and Claude models through the Claude Code CLI;
// a model that is not listed can be typed in.
export function ModelSelect({
  value, onChange, models, disabled, className, label = "Model",
}: {
  value: string; onChange: (model: string) => void; models: readonly ModelOption[]; disabled?: boolean; className?: string; label?: string;
}) {
  const [typing, setTyping] = useState(false);
  const [draft, setDraft] = useState("");
  const known = models.some((model) => model.id === value);
  const groups: [string, ModelOption[]][] = [
    ["OpenAI · Codex CLI", models.filter((model) => model.provider === "codex")],
    ["Claude · Claude Code CLI", models.filter((model) => model.provider === "claude")],
  ];
  const field = cn("model-select", className);
  if (typing) {
    const commit = () => { const next = draft.trim(); setTyping(false); if (next && next !== value) onChange(next); };
    return (
      <input
        autoFocus aria-label={label} className={field} value={draft} placeholder="model id, e.g. gpt-5.6-sol" spellCheck={false}
        onChange={(event) => setDraft(event.target.value)} onBlur={commit}
        onKeyDown={(event) => { if (event.key === "Enter") commit(); else if (event.key === "Escape") setTyping(false); }}
      />
    );
  }
  return (
    <select
      aria-label={label} className={field} value={value} disabled={disabled}
      onChange={(event) => { if (event.target.value === OTHER) { setDraft(""); setTyping(true); } else onChange(event.target.value); }}
    >
      {!known ? <option value={value}>{value}</option> : null}
      {groups.map(([name, list]) => list.length ? (
        <optgroup key={name} label={name}>
          {list.map((model) => <option key={model.id} value={model.id} disabled={!model.available && model.id !== value}>{model.label}{model.available ? "" : " (CLI not found)"}</option>)}
        </optgroup>
      ) : null)}
      <option value={OTHER}>Other model…</option>
    </select>
  );
}

export const modelLabel = (models: readonly ModelOption[], id: string) => models.find((model) => model.id === id)?.label ?? id;
