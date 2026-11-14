# Agentic implementation

## Intended workflow

RhinoClient will provide a chat interface and a control plane for launching and managing one or multiple geometry-editing tasks. Humans and agents can both contribute changes to a Rhino model.

An agent finishing a task should produce a proposal for human review, analogous to a pull request. Multiple proposals may present alternative solutions. The human reviews the alternatives and decides which changes to accept into the shared model; task completion does not itself imply acceptance.

## Future Git engine dependency

The intended workflow relies on the sibling `git_engine` project for document versioning and eventual reconciliation of agent and human changes. The control plane should anticipate proposals and human acceptance without implementing against the engine in this phase.

The engine's existing README describes entity history, commits, branches, validation, and merges. A pull-request-style review workflow is a product requirement here, not a claim that the engine already implements that workflow.

## Current scope

Discuss and plan the chat interface, task management, and agent access to Rhino geometry tools. Use `RhinoAI-rhino8-intel` as an implementation reference. No application code or Git engine integration is being written during this discussion.

## Agreed initial execution model

Each task works in a separate document copy. Its edits remain separate from the shared model. A completed task can raise a proposal (called a "PR" in the intended UI); acceptance and applying the changes to the shared model are deferred for the initial version.

Separate copies do not yet prescribe separate Rhino processes or headless documents; the execution mechanism remains to be decided.

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

Open launch-policy decision: a reviewed plan may authorize children to start automatically when their parent produces a result, or require approval of that result as an input first. Both are distinct from accepting the proposal into main. Until resolved, do not assume automatic downstream launch.

## Task geometry scope

Each task has an editable scope consisting of selected objects or layers. The rest of its document copy provides read-only context. Scope is an enforced boundary, not merely an instruction to the agent. Expanding it requires human approval before edits outside the existing scope occur.

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

- How separate document copies are hosted and inspected before Git engine integration.
- How tasks are created, scoped, monitored, interrupted, and reviewed.

## Control-plane functionality and dependencies

The supplied session-dashboard screenshots are a functional reference, not a visual design to copy. RhinoAI's UI is also a reference rather than the chosen interface.

Required direction: users can add tasks, inspect each task's conversation and activity, and stop tasks independently. Task details should expose scope, document copy, runtime/model, lifecycle events, tool calls and results, and the resulting PR. Show usage and cost when the selected runtime provides them; unavailable metrics must remain explicitly unavailable.

Support user-configured provider credentials where the selected runtime supports API-key authentication. Credential setup remains undecided. Keys must stay outside the panel's persistent state, transcripts, task logs, and source control; tasks should refer to a credential profile rather than store a key. Per-task worker ownership does not require a separate provider key for every task. CLI account sign-in and API-key authentication are different capabilities and need runtime-specific support.

Recommendation, not yet a confirmed implementation choice: begin with a local task supervisor owned by RhinoClient, a concurrency limit and queue, persisted task records and an append-only activity log, plus external workers. No separate orchestration platform is needed solely to add, inspect, or stop tasks. A local store can preserve inspection history without promising automatic execution recovery.

Stopping a task should prevent further tool dispatch, request worker cancellation, and report when it actually stops. An in-flight Rhino operation may need to finish; stop is not an automatic rollback. Retain the document copy and activity history for inspection. After a crash, mark uncertain runs interrupted and reconcile the worker/document state before offering continuation; do not blindly replay geometry mutations.

Consider Inngest or another durable orchestration service later if requirements include distributed execution, durable waits, scheduling, or automatic recovery across service restarts. Such a service cannot itself restore a Rhino document or guarantee that a timed-out geometry mutation did not execute. Relevant official references: https://www.inngest.com/docs/durable-execution and https://www.inngest.com/docs/durable-execution/guides-and-advanced/error-handling.
