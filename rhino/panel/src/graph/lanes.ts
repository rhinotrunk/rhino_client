import type { GraphCommit } from "../protocol/events";

// One row per commit, newest first. Lanes are columns; a line occupies the same lane
// from the commit that opens it to the commit that ends it, so rows can be drawn on their own.
export type Edge = { from: number; to: number; color: number; through?: boolean };
export type LaneRow = {
  commit: GraphCommit;
  lane: number;
  color: number;
  /** Lines drawn from the top of the row: pass-through lanes and lanes converging on the node. */
  above: Edge[];
  /** Lines drawn from the node to the bottom of the row, one per known parent. */
  below: Edge[];
  isMerge: boolean;
};
export type LaneLayout = { rows: LaneRow[]; width: number };

export const laneColors = 7;

export function hashOf(id: string): string {
  return id.startsWith("sha256:") ? id.slice(7) : id;
}

export function shortHash(id: string, length = 7): string {
  return hashOf(id).slice(0, length);
}

const mainBranch = "main";

/**
 * Which branch each commit is the work of. The main branch claims its first-parent line; every other branch then
 * claims the commits on its own first-parent line that no branch has claimed yet, oldest tip first, so a branch made
 * from another branch's tip owns only the commits made after it. Commits no ref leads to (a deleted branch's) are
 * grouped the same way under a made-up name.
 */
function owners(list: readonly GraphCommit[]): Map<string, string> {
  const byId = new Map(list.map((commit) => [commit.id, commit]));
  const tips: { name: string; id: string; generation: number }[] = [];
  for (const commit of list)
    for (const ref of commit.refs ?? []) if (ref) tips.push({ name: ref, id: commit.id, generation: commit.generation });
  const rank = (name: string) => (name === mainBranch ? 0 : name.startsWith("tag:") ? 2 : 1);
  tips.sort((a, b) => rank(a.name) - rank(b.name) || a.generation - b.generation || a.name.localeCompare(b.name));

  const owner = new Map<string, string>();
  const claim = (start: string, name: string) => {
    let id: string | undefined = start;
    while (id && byId.has(id) && !owner.has(id)) {
      owner.set(id, name);
      id = byId.get(id)?.parents?.[0];
    }
  };
  for (const tip of tips) claim(tip.id, tip.name);
  for (const commit of list) if (!owner.has(commit.id)) claim(commit.id, `#${commit.id}`);
  return owner;
}

/**
 * Lays commits out in branch columns: main in the first, every other branch in a column of its own for the rows it
 * spans (from its newest commit, or the merge that took it in, down to the commit it started from). Columns are
 * reused once a branch's span has ended. A branch's first commit leaves its column and joins the commit it was
 * made from, so a branch made from a tip still shows as a fork even when history itself is a straight line.
 */
export function layoutLanes(commits: readonly GraphCommit[]): LaneLayout {
  const seen = new Set<string>();
  const list = commits.filter((commit) => {
    if (!commit.id || seen.has(commit.id)) return false;
    seen.add(commit.id);
    return true;
  });
  const known = new Set(list.map((commit) => commit.id));
  const rowOf = new Map(list.map((commit, row) => [commit.id, row]));
  const parentsOf = (commit: GraphCommit) => (commit.parents ?? []).filter((id) => id !== commit.id && known.has(id));
  const owner = owners(list);

  // The rows each branch's lines occupy in its own column.
  const spans = new Map<string, { start: number; end: number }>();
  const widen = (name: string, row: number) => {
    const span = spans.get(name);
    if (!span) spans.set(name, { start: row, end: row });
    else { span.start = Math.min(span.start, row); span.end = Math.max(span.end, row); }
  };
  list.forEach((commit, row) => {
    const name = owner.get(commit.id)!;
    widen(name, row);
    parentsOf(commit).forEach((parent, order) => {
      const parentRow = rowOf.get(parent)!;
      if (order === 0) widen(name, parentRow); // the line to a first parent runs in the child's column
      else widen(owner.get(parent)!, row); // a merged-in line runs in the merged branch's column
    });
  });

  // Main is always column 0; other branches take the lowest column free for their whole span, the branch that
  // forked earliest first, so a branch made from another branch sits to the right of it.
  const column = new Map<string, number>();
  const taken: { start: number; end: number }[][] = [[]];
  const order = [...spans.entries()].sort(([a, x], [b, y]) => Number(b === mainBranch) - Number(a === mainBranch) || y.end - x.end || x.start - y.start);
  for (const [name, span] of order) {
    let index = name === mainBranch ? 0 : 1;
    if (name !== mainBranch) {
      while (taken[index]?.some((other) => !(span.end < other.start || span.start > other.end))) index++;
    }
    (taken[index] ??= []).push(span);
    column.set(name, index);
  }
  const columns = Math.max(1, taken.length);
  const colorOf = new Map([...column.keys()].map((name, index) => [name, name === mainBranch ? 0 : (index % (laneColors - 1)) + 1]));

  const lanes: (string | null)[] = [];
  const colors: number[] = [];
  // A line that cannot use its branch's column goes to a spare one to the right of all branch columns.
  const spare = (): number => {
    for (let index = columns; index < lanes.length; index++) if (lanes[index] === null || lanes[index] === undefined) return index;
    return Math.max(lanes.length, columns);
  };
  const free = (index: number, id?: string) => lanes[index] === null || lanes[index] === undefined || lanes[index] === id;

  let width = 1;
  const rows: LaneRow[] = [];
  for (const commit of list) {
    const name = owner.get(commit.id)!;
    const color = colorOf.get(name) ?? 0;
    const above: Edge[] = [];
    const incoming: number[] = [];
    lanes.forEach((id, index) => {
      if (id === commit.id) incoming.push(index);
    });
    const wanted = column.get(name) ?? 0;
    const lane = free(wanted, commit.id) ? wanted : incoming[0] ?? spare();

    lanes.forEach((id, index) => {
      if (id !== null && id !== undefined && id !== commit.id) above.push({ from: index, to: index, color: colors[index] ?? 0, through: true });
    });
    for (const index of incoming) above.push({ from: index, to: lane, color: colors[index] ?? 0 });

    for (const index of incoming) lanes[index] = null;
    lanes[lane] = null;

    const parents = parentsOf(commit);
    const below: Edge[] = [];
    parents.forEach((parent, index) => {
      let at: number;
      if (index === 0) {
        // The first parent continues in this commit's column, even when it belongs to another branch: the line
        // turns into that branch's column at the parent's row, which is where the fork shows.
        at = lane;
        colors[at] = color;
      } else {
        at = lanes.indexOf(parent);
        if (at < 0) {
          const target = column.get(owner.get(parent)!) ?? 0;
          at = free(target) ? target : spare();
          colors[at] = colorOf.get(owner.get(parent)!) ?? 0;
        }
      }
      lanes[at] = parent;
      below.push({ from: lane, to: at, color: colors[at] ?? 0 });
    });
    while (lanes.length > 0 && (lanes[lanes.length - 1] === null || lanes[lanes.length - 1] === undefined)) lanes.pop();
    width = Math.max(width, lane + 1, ...above.map((edge) => Math.max(edge.from, edge.to) + 1), ...below.map((edge) => edge.to + 1));
    rows.push({ commit, lane, color, above, below, isMerge: parents.length > 1 });
  }
  return { rows, width };
}
