import { createGitgraph, templateExtend, TemplateName, type Branch } from "@gitgraph/js";
import type { GraphCommit } from "../protocol/events";
import { graphHash, shortHash, toStory } from "./story";

const font = 'normal 12px -apple-system, "Segoe UI", sans-serif';
const svgNs = "http://www.w3.org/2000/svg";
let fitted: MutationObserver | undefined;
// Commits the user has clicked, in click order. Shift-click adds or removes one; a plain click selects only it.
let selection: string[] = [];
let shiftHeld = false;

export type GraphHandlers = {
  onCheckout: (id: string) => void;
  /** The selection changed: one id previews that commit, two or more start merge picking, none clears. */
  onSelect: (ids: string[]) => void;
  /** The pointer is on a commit: a good moment to load its preview. */
  onHover: (id: string) => void;
};

export function clearSelection(container: HTMLElement): void {
  selection = [];
  markSelection(container, new Map());
}

type DrawnCommit = {
  hash: string;
  message: string;
  style: { dot: { size: number }; message: { color?: string; font?: string } };
};

// Leave orientation unset. Gitgraph then draws the last commit at the top,
// which is the newest one after the oldest-first walk.
export function renderHistory(
  container: HTMLElement,
  commits: readonly GraphCommit[],
  scheme: "light" | "dark",
  handlers: GraphHandlers,
): void {
  const { onCheckout } = handlers;
  fitted?.disconnect();
  container.replaceChildren();
  const known = new Set(commits.map((commit) => commit.id));
  selection = selection.filter((id) => known.has(id));
  // Gitgraph's click callbacks do not carry the event, so note the shift key on the way down.
  container.onpointerdown = (event) => {
    shiftHeld = event.shiftKey;
  };
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

  const ids = new Map(commits.map((commit) => [graphHash(commit.id), commit.id]));
  const hashes = new Map(commits.map((commit) => [commit.id, graphHash(commit.id)]));
  const select = (id: string) => {
    if (!id) return;
    if (shiftHeld) selection = selection.includes(id) ? selection.filter((other) => other !== id) : [...selection, id];
    else selection = selection.length === 1 && selection[0] === id ? [] : [id];
    markSelection(container, hashes);
    handlers.onSelect([...selection]);
  };
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
    const commitId = ids.get(step.hash) ?? "";
    const commitOptions = {
      subject: step.subject,
      hash: step.hash,
      author: step.kind,
      ...(step.tag ? { tag: step.tag } : {}),
      ...(step.kind === "agent"
        ? { style: { dot: { color: scheme === "dark" ? "#d6bcfa" : "#6d28d9" } } }
        : {}),
      renderMessage: (drawn: DrawnCommit) => commitMessage(drawn, commitId, shortHash(step.hash), onCheckout, select, handlers.onHover),
      onClick: () => select(commitId),
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
  const fit = () => {
    placeCheckouts(svg);
    growSvg(svg);
    markSelection(container, hashes);
  };
  fitted = new MutationObserver(fit);
  fitted.observe(svg, { subtree: true, childList: true, attributes: true });
  fit();
}

// The checkout control sits after the message and stays invisible until the
// pointer is on that commit. Measure once the text has a width, then stop
// writing attributes so the resize observer can settle.
function commitMessage(
  commit: DrawnCommit,
  commitId: string,
  label: string,
  onCheckout: (id: string) => void,
  onSelect: (id: string) => void,
  onHover: (id: string) => void,
): SVGGElement {
  const row = document.createElementNS(svgNs, "g");
  row.setAttribute("class", "commit-row");
  row.setAttribute("transform", `translate(0, ${commit.style.dot.size})`);
  row.setAttribute("data-commit", commit.hash);
  row.addEventListener("click", (event) => {
    shiftHeld = event.shiftKey;
    onSelect(commitId);
  });
  row.addEventListener("pointerenter", () => {
    if (commitId) onHover(commitId);
  });

  const hit = document.createElementNS(svgNs, "rect");
  hit.setAttribute("class", "commit-hit");
  row.appendChild(hit);

  const text = document.createElementNS(svgNs, "text");
  text.setAttribute("class", "commit-subject");
  text.setAttribute("alignment-baseline", "central");
  text.setAttribute("dominant-baseline", "central");
  text.setAttribute("fill", commit.style.message.color ?? "");
  text.setAttribute("style", `font: ${commit.style.message.font || font}`);
  text.textContent = commit.message;
  row.appendChild(text);

  const action = document.createElementNS(svgNs, "g");
  action.setAttribute("class", "commit-checkout");
  action.setAttribute("role", "button");
  action.setAttribute("tabindex", "0");
  action.setAttribute("aria-label", `Checkout ${label}`);
  const button = document.createElementNS(svgNs, "rect");
  button.setAttribute("height", "18");
  button.setAttribute("y", "-9");
  button.setAttribute("rx", "4");
  const caption = document.createElementNS(svgNs, "text");
  caption.textContent = "Checkout";
  caption.setAttribute("y", "0");
  caption.setAttribute("text-anchor", "middle");
  caption.setAttribute("dominant-baseline", "central");
  caption.setAttribute("alignment-baseline", "central");
  action.append(button, caption);
  const trigger = (event: Event) => {
    event.preventDefault();
    event.stopPropagation();
    if (commitId) onCheckout(commitId);
  };
  action.addEventListener("click", trigger);
  action.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") trigger(event);
  });
  row.appendChild(action);
  return row;
}

/** Marks selected commits: the row and the dot's entry get "is-selected" and their position in the selection. */
function markSelection(container: HTMLElement, hashes: Map<string, string>): void {
  const wanted = new Map(selection.map((id, index) => [hashes.get(id) ?? "", index]));
  for (const row of Array.from(container.querySelectorAll(".commit-row"))) {
    const index = wanted.get(row.getAttribute("data-commit") ?? "");
    const entry = row.parentElement?.parentElement?.parentElement;
    for (const node of [row, entry]) {
      if (!(node instanceof Element)) continue;
      const on = index !== undefined;
      if (node.classList.contains("is-selected") !== on) node.classList.toggle("is-selected", on);
      const order = on ? String(index + 1) : "";
      if ((node.getAttribute("data-order") ?? "") !== order) {
        if (order) node.setAttribute("data-order", order);
        else node.removeAttribute("data-order");
      }
    }
  }
}

function placeCheckouts(svg: SVGSVGElement): void {
  const rows = svg.querySelectorAll(".commit-row");
  for (let index = 0; index < rows.length; index += 1) {
    const row = rows.item(index);
    if (!(row instanceof SVGGElement)) continue;
    const entry = row.parentElement?.parentElement?.parentElement;
    if (entry instanceof SVGGElement && !entry.classList.contains("commit-entry")) {
      entry.classList.add("commit-entry");
    }
    const subject = row.querySelector(".commit-subject");
    const action = row.querySelector(".commit-checkout");
    const hit = row.querySelector(".commit-hit");
    const caption = action?.querySelector("text");
    const button = action?.querySelector("rect");
    if (!(subject instanceof SVGTextElement) || !(action instanceof SVGGElement) || !(hit instanceof SVGRectElement) || !(caption instanceof SVGTextElement) || !(button instanceof SVGRectElement)) continue;
    const labelWidth = caption.getComputedTextLength();
    if (labelWidth <= 0) continue;
    const buttonWidth = Math.ceil(labelWidth) + 16;
    const x = Math.ceil(subject.getComputedTextLength()) + 10;
    const msgOffset = Math.max(0, Math.ceil(translateX(row.parentElement)));
    setAttr(action, "transform", `translate(${x}, 0)`);
    setAttr(button, "width", String(buttonWidth));
    setAttr(caption, "x", String(buttonWidth / 2));
    setAttr(hit, "x", String(-msgOffset));
    setAttr(hit, "y", "-12");
    setAttr(hit, "width", String(msgOffset + x + buttonWidth + 8));
    setAttr(hit, "height", "24");
    if (!row.classList.contains("is-placed")) row.classList.add("is-placed");
  }
}

function translateX(node: Element | null): number {
  const match = node?.getAttribute("transform")?.match(/translate\(([-\d.]+)/);
  return match?.[1] ? Number(match[1]) || 0 : 0;
}

function setAttr(node: Element, name: string, value: string): void {
  if (node.getAttribute(name) !== value) node.setAttribute(name, value);
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
