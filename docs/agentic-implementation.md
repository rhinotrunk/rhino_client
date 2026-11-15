# Agentic implementation

## Intended workflow

RhinoClient will provide a chat interface and a control plane for launching and managing one or multiple geometry-editing tasks. Humans and agents can both contribute changes to a Rhino model.

An agent finishing a task should produce a proposal for human review, analogous to a pull request. Multiple proposals may present alternative solutions. The human reviews the alternatives and decides which changes to accept into the shared model; task completion does not itself imply acceptance.

## Future Git engine dependency

The intended workflow relies on the sibling `git_engine` project for document versioning and eventual reconciliation of agent and human changes. The control plane should anticipate proposals and human acceptance without implementing against the engine in this phase.

The engine's existing README describes entity history, commits, branches, validation, and merges. A pull-request-style review workflow is a product requirement here, not a claim that the engine already implements that workflow.

## Current scope

Implement the task control plane and external Codex CLI workers, using `RhinoAI-rhino8-intel` as an architectural reference. Real geometry work targets isolated headless document copies. Git history and PR acceptance remain deferred; browser preview explicitly simulates execution.

## Agreed initial execution model

Each task works in a separate document copy. Its edits remain separate from the shared model. A completed task can raise a proposal (called a "PR" in the intended UI); acceptance and applying the changes to the shared model are deferred for the initial version.

The initial implementation uses headless document copies within the Rhino process. Document tools execute on the UI thread; external agent reasoning can run concurrently.

## Task planning and launch

Support both directly requested tasks and agent-proposed task plans. For example, "Give me three facade variations" can produce a plan containing three tasks, each with its instructions and proposed geometry scope. The user reviews the plan before launching agent-proposed tasks.

Reviewed tasks may be launched concurrently or individually. Each launched task has its own conversation, document copy, status, and resulting proposal. Approval to launch a task is distinct from acceptance of its resulting proposal.

## Orchestrator and task dependencies

An orchestrator agent decomposes a user request into a reviewable task plan with explicit scopes and dependencies. It proposes the plan; the task supervisor enforces launch, dependency readiness, concurrency, and cancellation. The orchestrator does not replace deterministic task management or human review.

The interface should expose an expandable task tree. Each node is individually inspectable and stoppable, with access to its conversation, activity, scope, document copy, and PR. Users can branch out from a specific task result to explore further alternatives. Main may be shown as a root context or omitted from the task list, but the source snapshot must remain identifiable.

Examples:

```text
Main snapshot
└── A: Create grid
    ├── A1: Facade option 1
    ├── A2: Facade option 2
    └── A3: Facade option 3

Main snapshot
├── A: Grid option A
│   └── A1: Facade option 1
└── B: Grid option B
    ├── B1: Facade option 1
    └── B2: Facade option 2
```

A child starts from a separate copy of a pinned parent result, including the parent's changes. Siblings have independent copies and can run concurrently after their shared input is ready. They do not edit the same mutable parent document. Each task edits only its authorized fragment; a document snapshot can contain wider read-only context.

Task dependency, alternative choice, and version ancestry are related but distinct. The displayed tree describes work and input lineage, not individual commits. A task can eventually create multiple commits. Acceptance of a facade proposal must account for its unaccepted grid prerequisite; it cannot silently imply approval of that prerequisite.

Recommended initial boundary: one upstream result per task, supporting trees without multi-parent reconciliation. A future task consuming multiple upstream results requires an explicit reconciliation step and a graph view or cross-links. No circular dependencies. Parent revisions must not silently update already-started children.

Launching reviewed tasks authorizes queued children to start automatically when their pinned upstream results are ready. Individual tasks can be launched separately. This authorizes use of upstream results as inputs; it does not accept any proposal into main.

## Task geometry scope

For the initial workflow trial, scope selection is optional: an empty selection authorizes edits throughout the isolated task copy. Users may restrict a task to selected objects or layers under optional context settings; in that case, the rest of the copy provides read-only context. Scope is an enforced boundary, not merely an instruction to the agent. Expanding it requires human approval before edits outside the existing scope occur.

For example, a facade task may inspect the structural grid but cannot modify it unless its editable scope is explicitly expanded. The enforcement mechanism and how layer membership changes affect scope remain implementation questions.

## Agreed worker ownership

RhinoClient owns the control plane and launches external agent workers, one per running task. Workers use MCP to access their assigned document copies. Model requests execute outside Rhino. The initial agent runtime has not yet been selected.

Each task owns its worker lifecycle, conversation, document copy, editable scope, status, and resulting proposal. Ownership is keyed by task identity rather than document and agent type. A worker is launched when its task runs; creating or reviewing a task plan does not itself start workers.

Adapt the external-runner pattern from `RhinoAI-rhino8-intel`: a common runner interface, external process lifecycle management, provider-specific stream adapters, and normalized events for the panel. This is an agreed architectural direction; the specific runtime and amount of source reuse remain undecided.

## Lessons from RhinoAI-rhino8-intel

Local source inspection identifies two complementary paths:

- The in-Rhino agent host launches external CLI agents. `rhino/plugin/Agents/AgentRunner.ai.cs` owns conversations and turns behind a common interface; `StreamJsonAgent.ai.cs` owns process lifecycle and streamed output; provider-specific parsers adapt Claude and Codex output. `AgentHost.ai.cs` pools runners by document serial and agent name, and disposes them when documents close.
- The external stdio MCP router routes calls to document slots, can launch Rhino instances, and shares slot discovery through SQLite across router processes. `rhino/router/Tools/SpawnSlotTool.ai.cs` exposes spawn/list/close operations. A slot need not be a separate OS process: host code also handles multiple documents sharing a Rhino process on macOS.

`rhino/plugin/McpServer.ai.cs` constructs services for an explicit document and exposes a separate `/agent` tool surface for panel agents. `rhino/plugin/Server/ToolRegistry.ai.cs` marshals document tools onto Rhino's UI thread. `AgentDispatch.ai.cs` gates turns per document. These are useful patterns for explicit document targeting, tool execution, cancellation, and cleanup; they do not constitute the proposed task/PR control plane.

Recommended adaptation, pending implementation planning: key worker ownership by task identity, bind each task's MCP access to its document copy, normalize worker events for the TypeScript panel, and retain explicit document targeting and lifecycle cleanup. A multi-slot router can be added if document hosting requires it; it is not automatically necessary for direct worker-to-document MCP access.

Important gap: arbitrary C#/Python execution and command tools in the reference can bypass object/layer scopes. Tool descriptions and document routing alone cannot enforce our editable-scope requirement. Initial tool access must enforce scope at the mutation boundary; unrestricted execution needs a separate design before it can satisfy that requirement.

The reference's `rhino/panel/README.md` describes a TypeScript panel prototype whose C# wiring is not implemented. Treat its streaming transcript, tool previews, context selection, and plan strip as UI references rather than proof of end-to-end integration.

## Git engine capability check

Inspection of the current sibling repository's API documentation, server endpoints, client SDK, and merge service shows that it already provides branches, commits, entity/component diffs, three-way merges, conflict reporting and ours/theirs resolution, and commit annotations that can carry review notes.

It does not provide a first-class PR lifecycle or enforce human approval before merging. Authentication and branch protection are listed as future work. Its Rhino client guide also leaves document import, checkout, dirty tracking, and applying merges to the client implementation. These primitives can support a future PR workflow, but that workflow is not available end to end today.

References: `../git_engine/docs/API.md`, `../git_engine/docs/OPEN_QUESTIONS.md`, `../git_engine/docs/PROTOTYPE_PLAN.md`, `../git_engine/src/RhinoTrunk.Server/Endpoints.cs`, and `../git_engine/src/RhinoTrunk.Core/RhinoTrunkService.Write.cs`.

## Unresolved

- Full native geometry execution and preview verification inside Rhino.
- Future branch/commit integration and human PR acceptance.

## Control-plane functionality and dependencies

The supplied Ellipsis screenshots are now both a functional and visual reference, following the later UI direction. RhinoAI's UI is also a reference rather than the chosen interface.

Required direction: users can add tasks, inspect each task's conversation and activity, and stop tasks independently. Task details should expose scope, document copy, runtime/model, lifecycle events, tool calls and results, and the resulting PR. Show usage and cost when the selected runtime provides them; unavailable metrics must remain explicitly unavailable.

Support user-configured provider credentials where the selected runtime supports API-key authentication. Credential setup remains undecided. Keys must stay outside the panel's persistent state, transcripts, task logs, and source control; tasks should refer to a credential profile rather than store a key. Per-task worker ownership does not require a separate provider key for every task. CLI account sign-in and API-key authentication are different capabilities and need runtime-specific support.

Recommendation, not yet a confirmed implementation choice: begin with a local task supervisor owned by RhinoClient, a concurrency limit and queue, persisted task records and an append-only activity log, plus external workers. No separate orchestration platform is needed solely to add, inspect, or stop tasks. A local store can preserve inspection history without promising automatic execution recovery.

Stopping a task should prevent further tool dispatch, request worker cancellation, and report when it actually stops. An in-flight Rhino operation may need to finish; stop is not an automatic rollback. Retain the document copy and activity history for inspection. After a crash, mark uncertain runs interrupted and reconcile the worker/document state before offering continuation; do not blindly replay geometry mutations.

Consider Inngest or another durable orchestration service later if requirements include distributed execution, durable waits, scheduling, or automatic recovery across service restarts. Such a service cannot itself restore a Rhino document or guarantee that a timed-out geometry mutation did not execute. Relevant official references: https://www.inngest.com/docs/durable-execution and https://www.inngest.com/docs/durable-execution/guides-and-advanced/error-handling.

## Initial working interface

Use the Ellipsis screenshots as the visual reference: session navigation and listing, session chat and records, nested task trees, and an overview rail when space permits.

New session opens an in-panel page asking for a description. Starting the session launches its orchestrator, which inspects context and proposes manageable tasks with dependencies. Planning is the beginning of the session; there is no separate planning feature or orchestrator navigation entry. A simple request may produce a single task. Workers start only when the user runs reviewed tasks, individually or for that session. Follow-up conversation can refine or extend the session. Tasks retain separate conversations, scopes, workers, document copies, and proposals.

Session membership, task dependency, and commit ancestry are different relationships. A session can contain several independent roots and alternative branches. Launching one session's plan must not launch drafts in another session. Dependencies are currently restricted to tasks in the same session.

Scope selection remains optional for this trial: an empty scope permits the whole isolated task copy, while explicit object/layer selections restrict edits. Git history and PR acceptance remain mocked/deferred.

## Budget runtime defaults

Planning and task workers default to `gpt-6-luna` with low reasoning effort. The settings also offer `gpt-6.1-terra`, the lower-cost GPT-6.1 option. Both are in the installed CLI catalog. An empty or older expensive model selection is normalized to Luna. The runner always passes an explicit model and low reasoning; it does not silently fall back to a more expensive model.

Pricing was checked against the official Codex/Work credit rate card: https://learn.chatgpt.com/docs/pricing. Luna is cheaper than Terra and Sol; subscription usage and API pricing are distinct.

Existing pre-session history is migrated into a previous modeling session. Prior CLI thread IDs are preserved separately as worker thread IDs. Saved document history persists locally; interrupted execution is not automatically replayed.

Verification: the actual Codex runner completed initial and resumed live turns using the budget model and MCP test endpoint. Browser execution is simulated and labeled. Full native geometry execution still needs verification inside Rhino.

## Task alternatives and viewport inspection

Every requested design alternative should be a separate sibling task. A shared grid plus three facade options has four task nodes; do not bundle three alternatives into one worker's task. Existing completed combined studies are retained rather than rewritten.

Selecting a task previews its isolated copy in the source Rhino viewport through a transient display conduit. The preview includes upstream geometry and refreshes after edits when that task is selected. Return to main model removes the preview without changing source objects. Preview display currently covers curves, Breps, meshes and points; unsupported geometry is represented by bounds. Branch-from-result actions are available on each left-tree task row once a saved result exists.

Native logs confirmed real creation: the inspected grid result had nine geometry creation calls and the combined facade study had 123, with a saved 3dm result of about 1.28 MB. The earlier empty viewport reflected hidden headless copies rather than absent worker geometry.

## Adding work at any depth

Each task row exposes Add task, available before or after execution, and Branch out, available once the task has a completed result. Add task creates a dependent child that waits for its parent result when launched. Branch out pins the already-produced result immediately as a new exploration's input. Both can be repeated at arbitrary depth and remain within the same session.

The UI distinguishes Not launched, Queued for worker, Waiting for a named parent to launch or finish, and Blocked by a failed/stopped/interrupted parent. A queued dependency is not represented as if it were actively computing.

## Reviewing and retaining work

Task lists expose Run and a compact action menu. Pause excludes draft/queued tasks from bulk launch; pausing a running worker cancels it at the tool boundary and retains its checkpoint. Resume continues after inspection of saved geometry rather than repeating completed operations. Pause is not process suspension or geometry rollback.

Archive sessions or task subtrees to hide them while retaining conversations, files and results. Show archived provides restore actions. Archive stops affected queued/running workers; restore does not automatically restart them. Remove excludes a task and its dependent subtree from the plan and future execution; underlying records and model copies are retained on disk. Bulk run excludes paused, archived and removed tasks.

The original session conversation remains the coordination thread. Task views link back to Session chat, and the orchestrator receives current task summaries on follow-up turns. Tree rows and plan-list controls use compact spacing.
