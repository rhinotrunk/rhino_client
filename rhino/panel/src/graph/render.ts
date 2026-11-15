import { createGitgraph, templateExtend, TemplateName, type Branch } from "@gitgraph/js";
import type { GraphCommit } from "../protocol/events";
import { toStory } from "./story";

const font = 'normal 12px -apple-system, "Segoe UI", sans-serif';
let fitted: MutationObserver | undefined;

// Leave orientation unset. Gitgraph then draws the last commit at the top,
// which is the newest one after the oldest-first walk.
export function renderHistory(container: HTMLElement, commits: readonly GraphCommit[], scheme: "light" | "dark"): void {
  fitted?.disconnect();
  container.replaceChildren();
  const steps = toStory(commits);
  if (steps.length === 0) return;

  const ink = token("--foreground", scheme === "dark" ? "#ededed" : "#252525");
  const paper = token("--card", scheme === "dark" ? "#303030" : "#ffffff");
  const line = token("--border", scheme === "dark" ? "#525252" : "#cccccc");
  const gitgraph = createGitgraph(container, {
    author: "human",
    branchLabelOnEveryCommit: false,
    template: templateExtend(TemplateName.Metro, {
      colors: ["#4d7399", "#c47b3a", "#2f8f6b", "#8b4d8b", "#b4534d", "#3d7ea6", "#6b7280"],
      branch: {
        lineWidth: 2,
        spacing: 24,
        label: {
          font,
          color: ink,
          strokeColor: line,
          bgColor: paper,
          borderRadius: 4,
        },
      },
      commit: {
        spacing: 40,
        message: {
          font,
          color: ink,
          displayAuthor: false,
          displayHash: false,
        },
        dot: { size: 5 },
      },
      tag: {
        font,
        color: ink,
        strokeColor: line,
        bgColor: paper,
      },
    }),
  });

  const branches = new Map<string, Branch>();
  const open = (name: string): Branch => {
    const existing = branches.get(name);
    if (existing) return existing;
    // A name that is not a commit keeps this branch off the current HEAD,
    // so a second root does not fork from the previous line.
    const created = gitgraph.branch({ name, from: `root:${name}` });
    branches.set(name, created);
    return created;
  };

  for (const step of steps) {
    const branch = open(step.branch);
    const commitOptions = {
      subject: step.subject,
      hash: step.hash,
      author: step.kind,
      ...(step.tag ? { tag: step.tag } : {}),
      ...(step.kind === "agent"
        ? { style: { dot: { color: scheme === "dark" ? "#d6bcfa" : "#6d28d9" } } }
        : {}),
    };
    const merged = step.merges.find((name) => branches.has(name));
    if (merged) branch.merge({ branch: merged, fastForward: false, commitOptions });
    else branch.commit(commitOptions);

    // Fork after the commit. Gitgraph branches from the current HEAD, so a
    // later child has to be opened before the next commit moves that HEAD.
    for (const fork of step.forks) {
      if (branches.has(fork)) continue;
      branches.set(fork, branch.branch(fork));
    }
  }

  const svg = container.querySelector("svg");
  if (!(svg instanceof SVGSVGElement)) return;
  // Gitgraph measures the picture before branch labels are placed, so the
  // text can run past the svg. Grow it to the text once those labels settle.
  const fit = () => growSvg(svg);
  fitted = new MutationObserver(fit);
  fitted.observe(svg, { subtree: true, childList: true, attributes: true });
  fit();
}

function growSvg(svg: SVGSVGElement): void {
  const origin = svg.getBoundingClientRect();
  if (origin.width === 0 && origin.height === 0) return;
  let right = 0;
  let bottom = 0;
  const nodes = svg.querySelectorAll("text, rect, circle, path");
  for (let index = 0; index < nodes.length; index += 1) {
    const node = nodes.item(index);
    if (!node) continue;
    const box = node.getBoundingClientRect();
    right = Math.max(right, box.right - origin.left);
    bottom = Math.max(bottom, box.bottom - origin.top);
  }
  const width = Math.ceil(right + 12);
  const height = Math.ceil(bottom + 8);
  if (width > Number(svg.getAttribute("width") ?? 0) + 1) svg.setAttribute("width", String(width));
  if (height > Number(svg.getAttribute("height") ?? 0) + 1) svg.setAttribute("height", String(height));
}

function token(name: string, fallback: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}
