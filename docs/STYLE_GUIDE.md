# RhinoTrunk panel style guide

How the RhinoTrunk panel looks and reads. It builds on the shadcn/ui setup in `rhino/panel` (style `new-york`, Tailwind, lucide icons) and the panel concept mockup. Where this guide and the code disagree, fix one of them.

## 1. Principles

1. **Rhino first.** The panel sits beside the viewport in Rhino's dock. It uses Rhino's neutral greys and one blue, follows Rhino's light/dark theme, and never competes with the model for attention.
2. **Every change shows twice.** The viewport shows *where* (colour overlay), and the panel shows *what* (list or text). Selecting one selects the other.
3. **Objects, not tables.** The panel lists geometry objects only. Layers, materials, settings and other tables are versioned by the engine but hidden in the UI.
4. **Plain words.** Pull, Push, Fetch, Check out. Errors say what happened and what to do next. No hashes in sentences, only in metadata lines.

## 2. Colour tokens

All colours are HSL CSS variables in `src/styles/panel.css`, exposed to Tailwind in `tailwind.config.mjs`. Light values sit on `:root`, dark values on `:root.dark`, which the bridge's `theme` event sets.

### Base (shadcn, existing)

| Token | Use | Tailwind |
| --- | --- | --- |
| `background` / `foreground` | Panel ground and body text | `bg-background text-foreground` |
| `card` | Commit detail card, popovers | `bg-card` |
| `primary` | Rhino blue. Primary action (Push), focus ring | `bg-primary text-primary-foreground` |
| `secondary`, `muted`, `accent` | Branch badges, selected row, hover | `bg-secondary`, `bg-muted`, `hover:bg-accent` |
| `muted-foreground` | Metadata: author, time, hashes, sync line | `text-muted-foreground` |
| `destructive` | Errors only, never for "deleted" | `text-destructive` |
| `border`, `input`, `ring` | Dividers, control outlines, focus | `border`, `border-input`, `ring-ring` |

### Change states (RhinoTrunk)

| Token | Meaning | Light | Dark | Tailwind |
| --- | --- | --- | --- | --- |
| `added` | Object added | `152 60% 34%` | `152 50% 55%` | `text-added`, `bg-added-soft` |
| `modified` | Object changed | `35 85% 40%` | `40 85% 60%` | `text-modified`, `bg-modified-soft` |
| `deleted` | Object removed | `0 65% 47%` | `0 70% 65%` | `text-deleted`, `bg-deleted-soft` |
| `agent` | Commit authored by an agent | `265 55% 52%` | `265 70% 72%` | `text-agent`, `bg-agent-soft` |

Rules:

- Each state colour comes as a pair: the strong colour for text, letters and strokes, and `-soft` for badge and fill backgrounds. Never put strong text on a strong fill.
- `deleted` and `destructive` look similar on purpose but mean different things. `deleted` describes history; `destructive` means something failed now.
- Colour never carries meaning alone. Always pair it with a letter (A/M/D), a sign (+ ~ −) or an icon.

### Viewport overlay (C# display conduit)

The conduit cannot read CSS, so use these RGB values. They match the light tokens and stay legible on Rhino's grey viewport in both themes.

| State | Colour | Stroke | Fill |
| --- | --- | --- | --- |
| Added | `#2B8A57` | solid, 2.5 px | 15 % fill for closed shapes |
| Modified, new | `#BC7A0F` | solid, 2.5 px | 15 % fill |
| Modified, previous | Rhino's locked-object grey | dashed 4/3, 1.5 px | none |
| Deleted | `#C73B3B` | dashed 6/4, 2.5 px | none |
| Selected in panel | viewport foreground | dashed 3/3, 1.25 px box, 8 px outside the object's bounds | none |

Unchanged objects draw as Rhino normally draws them. A moved object gets a thin `modified`-coloured arrow from its previous position to its new one.

## 3. Typography

- **Faces:** the system UI face from `panel.css` (Segoe UI on Windows, SF on macOS). Hashes, short ids and the text view use Tailwind's `font-mono`.
- **Scale:** the root font size is **13 px**, so Tailwind's rem sizes are smaller than on the web. `text-xs` ≈ 10 px is the floor, for metadata only.

| Role | Classes | Size at 13 px root |
| --- | --- | --- |
| Commit message, object name, card titles | `text-base font-medium` / `font-semibold` | 13 px |
| Section label, metadata, sync line, help text | `text-sm text-muted-foreground` | 11.4 px |
| Badges, counts, token chips, hashes | `text-xs` (`font-mono` for numbers and hashes) | 9.75 px |
| Branch names | `font-mono text-[12px]` | 12 px |
| Text view | `font-mono text-xs leading-relaxed` | 9.75 px |

- Use `tabular-nums` wherever counts sit side by side (`+3 ~2 −1`).
- Use the real minus sign `−` (U+2212) in counts, not a hyphen.

## 4. Layout

The panel is one column, about 320–400 px wide, with no tabs.

```
┌ Header ────────────────────────────┐  p-3, gap-2, border-b
│ tower · ● localhost:5280   [main ▾]│  repo, server status, branch picker
│ [↓ Pull 2] [↑ Push 3]           [⟳]│  sync buttons, Fetch at the far right
│ 2 behind · 3 local changes · fetched 2 min ago
├ History ───────────────────────────┤  scrolls
│ ● main  Merge agent/facade-study   │  56 px rows
│ │ ◆ Panelize south facade          │
│ …                                  │
├ Commit detail (card) ──────────────┤  m-3 p-3, rounded-lg border
│ title · hash · parent → commit     │
│ change bar                         │
│ [List|Text]          [Copy][Check out]
│ A  Panel 01   Brep   geometry …    │
└────────────────────────────────────┘
```

- Spacing comes from the Tailwind scale: `gap-2` inside groups, `p-3` for header and card padding, `gap-3` between sections.
- Separate regions with borders (`border-b`, `Separator`), not shadows. Only popovers and toasts get a shadow.
- Long text truncates in rows (`truncate`, with `min-w-0` on the flex child) and wraps in the detail card.

## 5. Components

In `src/components/ui`: `Button`, `Badge`, `Separator`, `Popover`, `Tabs`, `Textarea`, `Input`, `Progress` (extended with `indicatorClassName`), `Tooltip`, `ScrollArea`, `Select`, `Sonner` (follows the bridge's `dark` class instead of next-themes). Add more from `rhino/panel` with:

```
npx shadcn@2.3.0 add <component>     # 2.x matches this repo's Tailwind v3 setup
```

| UI element | Component | Variant / size | Icon (lucide) |
| --- | --- | --- | --- |
| Push | `Button` | `default`, size `sm`, count badge inside | `ArrowUp` |
| Pull | `Button` | `outline`, size `sm`, count badge inside | `ArrowDown` |
| Fetch | `Button` | `ghost`, size `icon` + `Tooltip` "Fetch" | `RefreshCw` |
| Branch picker | `Button` `outline` `sm` (dropdown later) | | `GitBranch`, `ChevronDown` |
| Server status | 6 px dot in `added` (connected) or `destructive` (offline) | | |
| Branch label in a row | `Badge` | `secondary` | |
| Tag label | `Badge` | `outline` with `border-dashed` | |
| Change counts | `Badge` | custom: `bg-added-soft text-added`, etc. | |
| Check result | icon only, with `Tooltip` for the summary | | `Check` (`text-added`), `X` (`text-deleted`) |
| Agent author | `text-agent` | | `Bot` |
| List / Text switch | `Tabs` (`TabsList` h-8) | | |
| New branch | `Popover` anchored to a ghost **+** icon button, `Tabs` Human / AI agent | | `Plus`, `GitBranch`, `Bot`, `Sparkles` |
| Model, token budget | `Select` (h-8) | | |
| Token meter | `Progress` with `indicatorClassName` | h-1.5 | |
| Copy text | `Button` | `ghost`, `sm` | `Copy` |
| Check out | `Button` | `outline`, `sm` | |
| Push dialog | `Popover` anchored to Push, with `Textarea` | | |
| Push blocked / rejected | `Alert` | `destructive` | `CircleAlert` |
| Result messages | `sonner` toast | | |

Button rules: one `default` (filled) button per view: Push in the header, Merge on a finished agent card, the submit button in a popover.

Do not nest a `PopoverTrigger` inside a `TooltipTrigger`: with React 19 the tooltip swallows the click. Anchor the popover (`PopoverAnchor`) and toggle it from the button's `onClick`, as `NewBranch.tsx` does. Disabled buttons stay visible: Pull is disabled when up to date and Push when there are no local changes.

## 6. Commit graph

| Element | Spec |
| --- | --- |
| Row height | 56 px; the graph column is 40 px wide |
| Lanes | 16 px apart, starting 16 px from the left; computed by `toCommitRows` in `src/engine/map.ts` |
| Edges | 2 px; `foreground` at 55 % opacity on lane 0, `agent` on agent lanes; cross-lane edges are cubic curves |
| Human commit | filled circle, r = 5, `foreground`, 2 px ring in `background` |
| Merge commit | hollow circle, r = 6, 2.5 px `foreground` stroke |
| Agent commit | 10 px diamond (a square rotated 45°), `agent` |
| Selected row | `bg-muted`; hover is `bg-accent` |
| Newly pulled rows | brief `bg-added-soft` flash (1.2 s), skipped under `prefers-reduced-motion` |

Row content, left to right: branch/tag badges, message (truncated); then on the second line author, `·`, time, change counts, check icon.

## 7. Branches and AI agents

There are two kinds of branch. The name decides the kind: anything starting with `agent/` is an AI branch.

| | Human branch | AI agent branch |
| --- | --- | --- |
| Created from | **+** → *Human* tab: name only | **+** → *AI agent* tab: prompt, branch name (`agent/` prefix fixed), model, token budget |
| Icon | `GitBranch`, muted | `Bot`, `text-agent` |
| Graph | grey side lane, round dots | purple side lane, diamonds |
| Branch badge | `secondary` (`default` when it is the current branch) | `bg-agent-soft text-agent`, `● ` prefix while running |
| Who commits | you, with Push | the agent; you review and merge |

Lane 0 always belongs to main's own history, so a new branch never looks like main moving.

**New branch popover.** The AI tab opens by default. The prompt box is 3 rows and does not resize. When the branch name is left empty, it is generated from the first four words of the prompt. Help text under the fields says what will happen: "The agent commits to its own branch. Nothing reaches main until you merge it."

**Agent run card** (the *AI agents* section above History, shown while any agent branch is not yet merged):

```
[bot] agent/west-fins          [● Running] 0:12
"Add 150 mm aluminium fins to the west facade…"
Generating geometry…            48.3k / 100k tokens
[██████████░░░░░░░░░]
in 30.2k  out 17.9k  2 commits  Sonnet 5.5          [■ Stop]
```

- Status badge: Running (`agent`, spinner), Ready to review (`added`), Budget reached (`modified`), Stopped (muted), Failed (`deleted`).
- Token meter: `Progress` at 6 px. The fill is `bg-agent`, turning `bg-modified` at 80 % of the budget and `bg-deleted` when the budget is reached.
- Numbers use `formatTokens()` (`950`, `48.3k`, `1.2M`) in `font-mono tabular-nums`. The total is bold; the budget is muted.
- Actions: **Stop** (`outline`) while running; **Review** (`ghost`) and **Merge into main** (`default`) when finished. Merged runs leave the section.
- Agent commits in the graph carry a token chip (`bg-agent-soft text-agent`, e.g. `30.9k tok`). The commit detail shows the model, tokens (in / out) and the prompt.

## 8. Change list and text view

**List row:** op letter (`A`/`M`/`D`, mono, semibold, in its state colour), then name and subtype, then component badges (`geometry`, `attributes`) on the right. Expanded rows show attribute field diffs as `− field  old` and `+ field  new` in mono.

Object names: `attributes.name`, falling back to `<Subtype> <first 8 of GUID>`.

**Text view** (copyable, for pull requests and chat):

```
commit 9f2c41e  (main)
Author: Facade bot <agent>   19:42
Check:  ci/geometry-valid ✓ 142 breps valid

  A  Brep        Panel 01    [geometry attributes]
  M  Extrusion   Column C4   [geometry]
  D  NurbsCurve  Setout line

1 added · 1 modified · 1 deleted
```

Columns are padded with spaces, the header lines are muted, and op lines are coloured by state.

## 9. Words

| Situation | Text |
| --- | --- |
| Sync line | `2 behind · 3 local changes · fetched 2 min ago` / `Up to date · no local changes` |
| Push dialog title | `Push to main` |
| Push button in dialog | `Commit & push` |
| After pull | Toast `Pulled 2 commits`, detail lists what changed in the document |
| After push | Toast `Pushed to main`, detail `3 objects committed · 9f2c41e` |
| Not connected | `Can't reach the RhinoTrunk server at localhost:5280.` |
| Agent started | Toast `Agent started on agent/west-fins`, detail `You can keep working. Token use shows live under AI agents.` |
| Agent merged | Toast `Merged agent/west-fins into main`, detail `3 commits · 48.3k tokens · 9f2c41e` |

### Errors

Use `describeError()` in `src/engine/map.ts` so every error reads the same way.

| Engine code | Title | Detail / action |
| --- | --- | --- |
| `409 ref_conflict` | main has new commits | Pull them first, then push. Your changes stay in the document. → **Pull now** |
| `422 missing_ref` | Something this change needs is not included | Engine message names the object → **Include and push** |
| `422 dangling_ref` | Still in use | Engine message names what still references it |
| `422 no_changes` | Nothing to push | The document matches the last commit. |
| `422 object_missing` | Upload incomplete | Some geometry did not reach the server. Push again. |

## 10. Accessibility

- Every icon-only button has an `aria-label` and a `Tooltip`.
- Focus uses the shadcn ring (`focus-visible:ring-1 ring-ring`); never remove it.
- Selected rows set `aria-pressed`, and status messages use `role="status"`.
- Animations respect `prefers-reduced-motion`.
