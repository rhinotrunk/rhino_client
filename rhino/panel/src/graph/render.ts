import { createGitgraph, templateExtend, TemplateName, type Branch } from "@gitgraph/js";
import type { GraphCommit } from "../protocol/events";
import { graphHash, shortHash, toStory } from "./story";

const font = 'normal 12px -apple-system, "Segoe UI", sans-serif';
const currentFont = '600 12px -apple-system, "Segoe UI", sans-serif';
const svgNs = "http://www.w3.org/2000/svg";
let fitted: MutationObserver | undefined;

type DrawnCommit = {
  hash: string;
  message: string;
  style: { dot: { size: number }; message: { color?: string; font?: string } };
};

// Leave orientation unset. Gitgraph then draws the last commit at the top,
// which is the newest one after the oldest-first walk. `current` is the
// commit the model is on; it is tagged HEAD.
export function renderHistory(
  container: HTMLElement,
  commits: readonly GraphCommit[],
  scheme: "light" | "dark",
  current: string | null,
  onCheckout: (id: string) => void,
): void {
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

  const ids = new Map(commits.map((commit) => [graphHash(commit.id), commit.id]));
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

  const accent = token("--primary", scheme === "dark" ? "#7aa7d9" : "#335f8c");
  for (const step of steps) {
    const branch = open(step.branch);
    const commitId = ids.get(step.hash) ?? "";
    const isCurrent = current !== null && commitId === current;
    const dot = {
      ...(step.kind === "agent" ? { color: scheme === "dark" ? "#d6bcfa" : "#6d28d9" } : {}),
      ...(isCurrent ? { size: 7, strokeWidth: 3, strokeColor: accent } : {}),
    };
    const commitOptions = {
      subject: step.subject,
      hash: step.hash,
      author: step.kind,
      ...(step.tag ? { tag: step.tag } : {}),
      ...(Object.keys(dot).length > 0 ? { style: { dot } } : {}),
      renderMessage: (drawn: DrawnCommit) => commitMessage(drawn, commitId, shortHash(step.hash), isCurrent, onCheckout),
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
  current: boolean,
  onCheckout: (id: string) => void,
): SVGGElement {
  const row = document.createElementNS(svgNs, "g");
  row.setAttribute("class", current ? "commit-row is-current" : "commit-row");
  row.setAttribute("transform", `translate(0, ${commit.style.dot.size})`);
  row.setAttribute("data-commit", commit.hash);

  const hit = document.createElementNS(svgNs, "rect");
  hit.setAttribute("class", "commit-hit");
  row.appendChild(hit);
  if (current) row.appendChild(headPill());

  const text = document.createElementNS(svgNs, "text");
  text.setAttribute("class", "commit-subject");
  text.setAttribute("alignment-baseline", "central");
  text.setAttribute("dominant-baseline", "central");
  text.setAttribute("fill", commit.style.message.color ?? "");
  text.setAttribute("style", `font: ${current ? currentFont : commit.style.message.font || font}`);
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

function headPill(): SVGGElement {
  const pill = document.createElementNS(svgNs, "g");
  pill.setAttribute("class", "commit-head");
  const box = document.createElementNS(svgNs, "rect");
  box.setAttribute("height", "18");
  box.setAttribute("y", "-9");
  box.setAttribute("rx", "4");
  const caption = document.createElementNS(svgNs, "text");
  caption.textContent = "HEAD";
  caption.setAttribute("y", "0");
  caption.setAttribute("text-anchor", "middle");
  caption.setAttribute("dominant-baseline", "central");
  caption.setAttribute("alignment-baseline", "central");
  pill.append(box, caption);
  return pill;
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
    let start = 0;
    const head = row.querySelector(".commit-head");
    const headBox = head?.querySelector("rect");
    const headCaption = head?.querySelector("text");
    if (headBox instanceof SVGRectElement && headCaption instanceof SVGTextElement) {
      const headWidth = Math.ceil(headCaption.getComputedTextLength()) + 12;
      if (headWidth <= 12) continue;
      setAttr(headBox, "width", String(headWidth));
      setAttr(headCaption, "x", String(headWidth / 2));
      start = headWidth + 6;
    }
    setAttr(subject, "x", String(start));
    const buttonWidth = Math.ceil(labelWidth) + 16;
    const x = start + Math.ceil(subject.getComputedTextLength()) + 10;
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
