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

export function layoutLanes(commits: readonly GraphCommit[]): LaneLayout {
  const seen = new Set<string>();
  const list = commits.filter((commit) => {
    if (!commit.id || seen.has(commit.id)) return false;
    seen.add(commit.id);
    return true;
  });
  const known = new Set(list.map((commit) => commit.id));
  const lanes: (string | null)[] = [];
  const colors: number[] = [];
  let nextColor = 0;
  const fresh = () => nextColor++ % laneColors;
  const slot = (): number => {
    const free = lanes.indexOf(null);
    if (free >= 0) return free;
    lanes.push(null);
    return lanes.length - 1;
  };

  let width = 1;
  const rows: LaneRow[] = [];
  for (const commit of list) {
    const above: Edge[] = [];
    const incoming: number[] = [];
    lanes.forEach((id, index) => {
      if (id === commit.id) incoming.push(index);
    });
    // A tip no line leads to starts a lane of its own.
    const lane = incoming[0] ?? slot();
    if (incoming.length === 0) colors[lane] = fresh();
    const color = colors[lane] ?? 0;

    lanes.forEach((id, index) => {
      if (id !== null && id !== commit.id) above.push({ from: index, to: index, color: colors[index] ?? 0, through: true });
    });
    for (const index of incoming) above.push({ from: index, to: lane, color: colors[index] ?? 0 });

    for (const index of incoming) lanes[index] = null;
    lanes[lane] = null;

    const parents = (commit.parents ?? []).filter((id) => id !== commit.id && known.has(id));
    const below: Edge[] = [];
    parents.forEach((parent, order) => {
      let at = lanes.indexOf(parent);
      if (at < 0) {
        at = order === 0 ? lane : slot();
        lanes[at] = parent;
        colors[at] = order === 0 ? color : fresh();
      }
      below.push({ from: lane, to: at, color: colors[at] ?? 0 });
    });
    while (lanes.length > 0 && lanes[lanes.length - 1] === null) lanes.pop();
    width = Math.max(width, lane + 1, ...above.map((edge) => Math.max(edge.from, edge.to) + 1), ...below.map((edge) => edge.to + 1));
    rows.push({ commit, lane, color, above, below, isMerge: parents.length > 1 });
  }
  return { rows, width };
}
