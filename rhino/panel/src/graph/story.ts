import type { GraphCommit } from "../protocol/events";

export type StoryStep = {
  branch: string;
  subject: string;
  hash: string;
  kind: string;
  tag?: string;
  merges: string[];
  forks: string[];
};

const hashPrefix = "sha256:";

export function graphHash(id: string): string {
  const hex = id.startsWith(hashPrefix) ? id.slice(hashPrefix.length) : id;
  return hex || id || "commit";
}

export function shortHash(id: string, length = 8): string {
  return graphHash(id).slice(0, length);
}

// The server lists commits newest first, each one before its parents.
// Reversing that list walks oldest first. The first child of a commit stays
// on that commit's branch. Each later child opens a branch, named from a
// branch ref on the new line or from the short hash of the commit that opened
// it. A merge stays on its first parent's branch and joins the other parents.
export function toStory(commits: readonly GraphCommit[]): StoryStep[] {
  const seen = new Set<string>();
  const ordered = [...commits].reverse().filter((commit) => {
    if (!commit.id || seen.has(commit.id)) return false;
    seen.add(commit.id);
    return true;
  });
  const byId = new Map(ordered.map((commit) => [commit.id, commit]));
  const primary = new Map<string, string>();
  const children = new Map<string, string[]>();

  for (const commit of ordered) {
    const parent = (commit.parents ?? []).find((id) => id !== commit.id && byId.has(id));
    if (!parent) continue;
    primary.set(commit.id, parent);
    const list = children.get(parent) ?? [];
    list.push(commit.id);
    children.set(parent, list);
  }

  const lanes: string[][] = [];
  const laneOf = new Map<string, number>();
  for (const commit of ordered) {
    const parent = primary.get(commit.id);
    const parentLane = parent === undefined ? undefined : laneOf.get(parent);
    const siblings = parent === undefined ? undefined : children.get(parent);
    const continues = parentLane !== undefined && siblings?.[0] === commit.id;
    const lane = continues && parentLane !== undefined ? parentLane : lanes.length;
    if (!continues || parentLane === undefined) lanes.push([]);
    laneOf.set(commit.id, lane);
    lanes[lane]!.push(commit.id);
  }

  const used = new Set<string>();
  const names = lanes.map((ids) => claim(laneLabel(ids, byId), used));
  const branchOf = new Map<string, string>();
  lanes.forEach((ids, index) => {
    const name = names[index];
    if (!name) return;
    for (const id of ids) branchOf.set(id, name);
  });

  return ordered.map((commit) => {
    const mine = branchOf.get(commit.id) ?? "branch";
    const kids = children.get(commit.id) ?? [];
    const forks: string[] = [];
    for (const childId of kids.slice(1)) {
      const name = branchOf.get(childId);
      if (name && name !== mine && !forks.includes(name)) forks.push(name);
    }
    const known = (commit.parents ?? []).filter((id) => id !== commit.id && byId.has(id));
    const merges: string[] = [];
    for (const parentId of known.slice(1)) {
      const name = branchOf.get(parentId);
      if (name && name !== mine && !merges.includes(name)) merges.push(name);
    }
    const step: StoryStep = {
      branch: mine,
      subject: subjectFor(commit),
      hash: graphHash(commit.id),
      kind: kindFor(commit),
      merges,
      forks,
    };
    const tag = tagFor(commit, mine);
    if (tag) step.tag = tag;
    return step;
  });
}

function laneLabel(ids: readonly string[], byId: ReadonlyMap<string, GraphCommit>): string {
  for (let index = ids.length - 1; index >= 0; index -= 1) {
    const id = ids[index];
    const commit = id ? byId.get(id) : undefined;
    const ref = commit ? firstBranchRef(commit.refs ?? []) : undefined;
    if (ref) return ref;
  }
  return ids[0] ? shortHash(ids[0]) : "branch";
}

function firstBranchRef(refs: readonly string[]): string | undefined {
  return refs.find((ref) => ref.length > 0 && !ref.startsWith("tag:"));
}

function claim(name: string, used: Set<string>): string {
  const base = name.trim() || "branch";
  if (!used.has(base)) {
    used.add(base);
    return base;
  }
  let index = 2;
  while (used.has(`${base}-${index}`)) index += 1;
  const unique = `${base}-${index}`;
  used.add(unique);
  return unique;
}

function kindFor(commit: GraphCommit): string {
  return commit.author?.kind?.trim() || "human";
}

function subjectFor(commit: GraphCommit): string {
  const line = commit.message.split(/\r?\n/, 1)[0]?.trim() || "(no message)";
  const clipped = line.length > 72 ? `${line.slice(0, 69)}…` : line;
  return `${clipped} · ${kindFor(commit)}`;
}

function tagFor(commit: GraphCommit, branch: string): string | undefined {
  const tags: string[] = [];
  for (const ref of commit.refs ?? []) {
    if (ref.startsWith("tag:")) {
      const name = ref.slice("tag:".length);
      if (name) tags.push(name);
    } else if (ref && ref !== branch) {
      tags.push(ref);
    }
  }
  return tags.length > 0 ? tags.join(", ") : undefined;
}
