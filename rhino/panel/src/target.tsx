import { cn } from "@/lib/utils";

// Which branch a pull request merges into. Only human branches are offered; agent branches are the sources.
export function TargetPicker({ value, options, onChange, className, disabled }: {
  value: string; options: readonly string[]; onChange: (branch: string) => void; className?: string; disabled?: boolean;
}) {
  const list = options.includes(value) ? options : [value, ...options];
  return (
    <select
      aria-label="Merge into" title="Branch this pull request merges into" value={value} disabled={disabled}
      onClick={(event) => event.stopPropagation()} onDoubleClick={(event) => event.stopPropagation()}
      onChange={(event) => onChange(event.target.value)}
      className={cn("target-picker", className)}
    >
      {list.map((name) => <option key={name} value={name}>{name}</option>)}
    </select>
  );
}

export function branchNames(commits: readonly { refs?: string[] }[]): string[] {
  const names = new Set<string>();
  for (const commit of commits) for (const ref of commit.refs ?? []) if (ref && !ref.startsWith("tag:") && !ref.startsWith("agent/")) names.add(ref);
  return [...names].sort((a, b) => (a === "main" ? -1 : b === "main" ? 1 : a.localeCompare(b)));
}
