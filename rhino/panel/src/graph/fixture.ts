import type { GraphCommit } from "../protocol/events";

const human = { id: "edwin", name: "Edwin", kind: "human" };
const agent = { id: "box", name: "box", kind: "agent" };

function id(digit: string): string {
  return `sha256:${digit.repeat(64)}`;
}

// Newest first, matching GraphAsync. main runs through the older child of
// "Update geometry"; agent/box is the later child and merges back at the tip.
export function previewHistory(): GraphCommit[] {
  const initial = id("1");
  const update = id("2");
  const move = id("3");
  const box = id("4");
  const merge = id("5");
  const facade = id("6");
  const tip = id("7");
  return [
    {
      id: tip,
      parents: [merge],
      generation: 5,
      timestamp: "2026-04-06T12:00:00Z",
      message: "Tidy layers",
      author: human,
      refs: ["main"],
    },
    {
      id: facade,
      parents: [merge],
      generation: 5,
      timestamp: "2026-04-06T09:00:00Z",
      message: "Agent task: Facade option A",
      author: agent,
      refs: ["agent/facade-a"],
    },
    {
      id: merge,
      parents: [move, box],
      generation: 4,
      timestamp: "2026-04-05T12:00:00Z",
      message: "Merge agent/box into main",
      author: human,
      refs: ["tag:v1"],
    },
    {
      id: box,
      parents: [update],
      generation: 3,
      timestamp: "2026-04-04T12:00:00Z",
      message: "Add a box",
      author: agent,
      refs: ["agent/box"],
    },
    {
      id: move,
      parents: [update],
      generation: 3,
      timestamp: "2026-04-03T12:00:00Z",
      message: "Move the layer",
      author: human,
      refs: [],
    },
    {
      id: update,
      parents: [initial],
      generation: 2,
      timestamp: "2026-04-02T12:00:00Z",
      message: "Update geometry",
      author: human,
      refs: [],
    },
    {
      id: initial,
      parents: [],
      generation: 1,
      timestamp: "2026-04-01T12:00:00Z",
      message: "Initial commit",
      author: human,
      refs: [],
    },
  ];
}
